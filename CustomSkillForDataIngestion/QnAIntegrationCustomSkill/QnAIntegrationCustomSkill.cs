// Copyright (c) Microsoft. All rights reserved.  
// Licensed under the MIT License. See LICENSE file in the project root for full license information.  
using Azure.Search.Documents;
using AzureCognitiveSearch.PowerSkills.Common;
using Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.CognitiveServices.Knowledge.QnAMaker;
using Microsoft.Azure.CognitiveServices.Knowledge.QnAMaker.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;


namespace AzureCognitiveSearch.QnAIntegrationCustomSkill
{
    public static class UploadToQnAMaker
    {
        [FunctionName("upload-to-qna")]
        public static IActionResult RunUploadToQnaMaker(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            [Queue("upload-to-qna"), StorageAccount("AzureWebJobsStorage")] ICollector<QnAQueueMessageBatch> msg,
            ILogger log,
            ExecutionContext executionContext)
        {
            log.LogInformation("UploadToQnAMaker Custom Skill: C# HTTP trigger function processed a request.");

            string skillName = executionContext.FunctionName;
            IEnumerable<WebApiRequestRecord> requestRecords = WebApiSkillHelpers.GetRequestRecords(req);
            if (requestRecords == null)
            {
                return new BadRequestObjectResult($"{skillName} - Invalid request record array.");
            }
            var msgBatch = new QnAQueueMessageBatch(); 
            WebApiSkillResponse response = WebApiSkillHelpers.ProcessRequestRecords(skillName, requestRecords,
                (inRecord, outRecord) =>
                {
                    string id = (inRecord.Data.TryGetValue("id", out object idObject) ? idObject : null) as string;
                    string blobName = (inRecord.Data.TryGetValue("blobName", out object blobNameObject) ? blobNameObject : null) as string;
                    string blobUrl = (inRecord.Data.TryGetValue("blobUrl", out object blobUrlObject) ? blobUrlObject : null) as string;
                    string sasToken = (inRecord.Data.TryGetValue("sasToken", out object sasTokenObject) ? sasTokenObject : null) as string;

                    if (string.IsNullOrWhiteSpace(blobUrl))
                    {
                        outRecord.Errors.Add(new WebApiErrorWarningContract() { Message = $"Parameter '{nameof(blobUrl)}' is required to be present and a valid uri." });
                        return outRecord;
                    }

                    string fileUri = WebApiSkillHelpers.CombineSasTokenWithUri(blobUrl, sasToken);
                    var queueMessage = new QnAQueueMessage
                    {
                        Id = id,
                        FileName = blobName,
                        FileUri = fileUri
                    };
                    msgBatch.Values.Add(queueMessage);
                    if (msgBatch.Values.Count >= 10)
                    {
                        msg.Add(msgBatch);
                        msgBatch.Values.Clear();
                    }
                    outRecord.Data["status"] = "InQueue";
                    return outRecord;
                });
            // Add a list of <= 10 files into one queue message to be extracted as a batch. 
            if (msgBatch.Values.Count > 0)
            {
                msg.Add(msgBatch);
            }
            return new OkObjectResult(response);
        }

        [FunctionName("upload-to-qna-queue-trigger"), Singleton]
        public async static Task Run(
            [QueueTrigger("upload-to-qna", Connection = "AzureWebJobsStorage")] QnAQueueMessageBatch qnaQueueMessage,
            ILogger log)
        {
            log.LogInformation("upload-to-qna-queue-trigger: C# Queue trigger function processed");

            var qnaClient = new QnAMakerClient(new ApiKeyServiceClientCredentials(GetAppSetting("QnAAuthoringKey")))
            {
                Endpoint = $"https://{GetAppSetting("QnAServiceName")}.cognitiveservices.azure.com"
            };

            var updateKB = InitUpdateKB();
            var indexDocuments = new List<IndexDocument>();
            foreach (var msg in qnaQueueMessage.Values)
            {
                if (!IsValidFile(msg.FileName))
                {
                    log.LogError("upload-to-qna-queue-trigger: unable to extract qnas from this file extension " + msg.FileName);
                    indexDocuments.Add(new IndexDocument { id = msg.Id, status = OperationStateType.Failed});
                    continue;
                }
                updateKB.Delete.Sources.Add(msg.FileName);
                updateKB.Add.Files.Add(new FileDTO { FileName = msg.FileName, FileUri = msg.FileUri });
                indexDocuments.Add(new IndexDocument { id = msg.Id });
            }

            var stopwatch = new Stopwatch();
            stopwatch.Start();
            // call update KB using REST API
            var updateOp = await UpdateKB(qnaClient, updateKB, log);
            updateOp = await MonitorOperation(qnaClient, updateOp, log);
            stopwatch.Stop();
            log.LogInformation("upload-to-qna-queue-trigger: update operation time = " + stopwatch.Elapsed.TotalSeconds + " seconds. Number of files processed = " + updateKB.Add.Files.Count);

            // set the extraction status for each file
            foreach ( var msg in qnaQueueMessage.Values)
            {
                var indexDocument = indexDocuments.Where(doc => doc.id == msg.Id).ToList().First();
                indexDocument.status = GetOperationStatus(updateOp, msg.FileName, log);
            }
            var searchClient = new SearchClient(
                new Uri($"https://{GetAppSetting("SearchServiceName")}.search.windows.net"),
                Constants.indexName,
                new Azure.AzureKeyCredential(GetAppSetting("SearchServiceApiKey")));
            // TODO check to make sure this already exists in the index i.e. the indexer has finished indexed this id
            // so that the indexer doesn't overwrite this status with the InQueue status (avoid race condition with indexer)
            await searchClient.MergeOrUploadDocumentsAsync(indexDocuments);

            await qnaClient.Knowledgebase.PublishAsync(GetAppSetting("KnowledgeBaseID"));
        }

        private static async Task<Operation> UpdateKB(QnAMakerClient qnaClient, UpdateKbOperationDTO updateKB, ILogger log)
        {
            var service = "/qnamaker/v4.0";
            string uri = qnaClient.Endpoint + service + "/knowledgebases/" + GetAppSetting("KnowledgeBaseID");

            string content = JsonConvert.SerializeObject(updateKB);
            // To speed up extraction within one batch request and to skip sources that have extraction failure
            string append = ",\"maxDegreeOfParallelism\":4, \"skipSourcesWithExtractionFailure\":true";
            content = content.Insert(content.Length - 1, append);

            // Starts the QnA Maker operation to update the knowledge base
            var response = await Patch(uri, GetAppSetting("QnAAuthoringKey"), content);
            var fields = JsonConvert.DeserializeObject<Dictionary<string, string>>(response.response);
            string opId = fields["operationId"];
            // Get operation status
            var operation = await qnaClient.Operations.GetDetailsAsync(opId);
            return operation;
        }

        private static async Task<Response> Patch(string uri, string key, string body)
        {
            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage())
            {
                request.Method = HttpMethod.Patch;
                request.RequestUri = new Uri(uri);

                if (!string.IsNullOrEmpty(body))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }
                
                request.Headers.Add("Ocp-Apim-Subscription-Key", $"{key}");

                var response = await client.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                return new Response(response, response.Headers, responseBody);
            }
        }

        // <MonitorOperation>
        private static async Task<Operation> MonitorOperation(IQnAMakerClient qnaClient, Operation operation, ILogger log)
        {
            // Loop while operation is running
            for (int i = 0;
                i < 100 && (operation.OperationState == OperationStateType.NotStarted || operation.OperationState == OperationStateType.Running);
                i++)
            {
                log.LogInformation($"Waiting for operation: {operation.OperationId} to complete.");
                await Task.Delay(5000);
                operation = await qnaClient.Operations.GetDetailsAsync(operation.OperationId);
            }
            return operation;
        }
        // </MonitorOperation>

        // Checks valid file type before sending for extraction
        private static bool IsValidFile(string fileName)
        {
            HashSet<string> fileExtensions = new HashSet<string> { "tsv", "pdf", "txt", "docx", "xlsx" };
            string fileExtension;
            try
            {
                fileExtension = Path.GetExtension(fileName).ToLower().TrimStart('.');
            }
            catch
            {
                fileExtension = string.Empty;
            }
            
            return fileExtensions.Contains(fileExtension);
        }

        // Returns the operation state after extraction for fileName
        private static string GetOperationStatus(Operation operation, string fileName, ILogger log)
        {
            string operationState = OperationStateType.Succeeded;
            if (operation.OperationState != OperationStateType.Succeeded && operation.ErrorResponse != null)
            {
                // Checks if the fileName is present in the error response
                var error = operation.ErrorResponse.Error.Details.Where(error => error.Target == fileName);
                if(error != null && error.Any())
                {
                    // Gets error details correspoding to the fileName if it exists 
                    var errorDetails = error.ToList().First();
                    log.LogError("upload-to-qna-queue-trigger: " + errorDetails.Message + " " + errorDetails.Target);
                    operationState = OperationStateType.Failed;
                }
            }

            return operationState;
        }

        private static UpdateKbOperationDTO InitUpdateKB()
        {
            var updateKB = new UpdateKbOperationDTO();
            updateKB.Delete = new UpdateKbOperationDTODelete();
            updateKB.Delete.Sources = new List<string>();
            updateKB.Add = new UpdateKbOperationDTOAdd();
            updateKB.Add.Files = new List<FileDTO>();
            return updateKB;

        }

        private static string GetAppSetting(string key)
        {
            return Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process);
        }

        public class QnAQueueMessageBatch
        {
            public List<QnAQueueMessage> Values { get; set; } = new List<QnAQueueMessage>();
        }

        public class QnAQueueMessage
        {
            public string Id { get; set; }

            public string FileName { get; set; }

            public string FileUri { get; set; }
        }

        public class IndexDocument
        {
            public string id { get; set; }

            public string status { get; set; }
        }

        /// Represents the HTTP response returned by an HTTP request.
        public struct Response
        {
            public HttpResponseHeaders headers;
            public string response;

            public string statusCode;

            public Response(HttpResponseMessage responseMessage, HttpResponseHeaders headers, string response)
            {
                this.headers = headers;
                this.response = response;
                this.statusCode = responseMessage.StatusCode.ToString();
            }
        }
    }
}
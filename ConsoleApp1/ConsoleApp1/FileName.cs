/*
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
namespace WebhookReceiver
{
    class Program // Define class Program
    {
        public static void Main(string[] args) // Main method, entry point of the application
        {
            CreateHostBuilder(args).Build().Run(); // Build and run the host
        }

        public static IHostBuilder CreateHostBuilder(string[] args) => // Method to create the host builder
            Host.CreateDefaultBuilder(args) // Create a default host builder
                .ConfigureServices((hostContext, services) => // Configure services for dependency injection
                {
                    services.AddHostedService<WebhookService>(); // Add WebhookService as a hosted service
                });
    }
    public class WebhookService : BackgroundService
    {
        private readonly ILogger<WebhookService> _logger;
        private readonly HttpClient _httpClient;
        private readonly HttpListener _httpListener;
        string organization = "isbIntern2024";
        string project = "HRD%20Internship";
        string personalAccessToken = "etoyf6zija76t6ggchpvixh2jnbzpynyjzyuuwn2nc2t43xqy2dq";

        public WebhookService(ILogger<WebhookService> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://localhost:8001/");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Webhook Service is starting.");
            _httpListener.Start();
            _logger.LogInformation("Listening for webhook events...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    if (context.Request.HttpMethod == "POST")
                    {
                        using (var reader = new System.IO.StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                        {
                            string webhookPayload = await reader.ReadToEndAsync();
                            await HandleWebhook(webhookPayload);
                        }
                    }

                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    await context.Response.OutputStream.FlushAsync();
                    context.Response.Close();
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995)
                {
                    _logger.LogInformation("HTTP Listener is shutting down.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred while processing webhook events.");
                }
            }

            _httpListener.Stop();
            _logger.LogInformation("Webhook Service is stopping.");
        }

        private async Task HandleWebhook(string webhookPayload)
        {
            _logger.LogInformation($"Webhook received");
            List<string> reviewers = new List<string>();
            try
            {
                var pullRequestPayload = JsonConvert.DeserializeObject<PullRequestPayload>(webhookPayload);

                // Extract relevant information

                string title = pullRequestPayload.Resource.Title;
                string description = pullRequestPayload.Resource.Description;
                string status = pullRequestPayload.Resource.Status;
                string repositoryName = pullRequestPayload.Resource.Repository.Name;
                string createdBy = pullRequestPayload.Resource.CreatedBy.DisplayName;
                var reviewTerms = new List<string> { "urd review", "srs review", "design review", "code review", "other" };
                List<string> reqreviewers;
                // Log extracted information
                _logger.LogInformation($"Title: {title}");
                _logger.LogInformation($"Description: {description}");
                _logger.LogInformation($"Status: {status}");
                _logger.LogInformation($"Repository: {repositoryName}");
                _logger.LogInformation($"Created By: {createdBy}");
                if (pullRequestPayload.Resource.Changes == null)
                {
                    try
                    {
                        string repositoryId = pullRequestPayload.Resource.Repository?.Id;
                        int pullRequestId = pullRequestPayload.Resource.PullRequestId;
                        var iterations = await GetPullRequestIterations(repositoryId, pullRequestId);
                        if (iterations == null || iterations.Count == 0)
                        {
                            _logger.LogError("No iterations found for the pull request.");
                            return;
                        }

                        var latestIteration = iterations.Last();
                        int iterationId = latestIteration.Id;

                        // Fetch changes for the latest iteration
                        _logger.LogInformation($"Fetching pull request changes for Repository ID: {repositoryId}, Pull Request ID: {pullRequestId}, Iteration ID: {iterationId}");
                        pullRequestPayload.Resource.Changes = await GetPullRequestChanges(repositoryId, pullRequestId, iterationId);

                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"An error occurred while fetching pull request changes: {ex.Message}");
                        throw;
                    }
                }
                    async Task<bool> CheckAndAssignTasks(string term)
                    { 
                    bool isTitleContainsTerm = title?.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isFilePathContainsTerm = pullRequestPayload?.Resource?.Changes != null && pullRequestPayload.Resource.Changes.Any(change =>
                        change?.Item?.Path != null && change.Item.Path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (isTitleContainsTerm && isFilePathContainsTerm)
                    {
                        _logger.LogInformation($"The pull request contains '{term}' in the title or file paths.");
                        reqreviewers=AssignTaskToReviewer(term, pullRequestPayload );
                        // Implement this function to assign tasks

                        await CreateWorkItem(webhookPayload, title, reqreviewers);
                        return true;
                    }
                    return false;
                }bool check = false;
                foreach (var term in reviewTerms)
                {
                    bool titleFound = await CheckAndAssignTasks(term);
                    if (titleFound)
                    {
                        check = true;
                        break; // Exit the loop if a match is found
                    }
                   
                     //_logger.LogInformation($"Resource: {JsonConvert.SerializeObject(pullRequestPayload.Resource, Formatting.Indented)}");
                }
                if (check == false)
                {
                    _logger.LogError("Cannot approve pull request!! check your folder and title name for typing errors!!");
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while handling the webhook payload.");
            }
             async Task<List<Iteration>> GetPullRequestIterations(string repositoryId, int pullRequestId)
            {
                string url = $"https://dev.azure.com/{organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/iterations?api-version=7.2-preview.1";
                using (var client = new HttpClient())
                {
                    try
                    {
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{string.Empty}:{personalAccessToken}")));

                        //_logger.LogInformation($"Requesting URL: {url}");

                        HttpResponseMessage response = await client.GetAsync(url);

                        if (!response.IsSuccessStatusCode)
                        {
                            //string errorResponse = await response.Content.ReadAsStringAsync();
                            //_logger.LogError($"Error: {response.StatusCode}. Response: {errorResponse}");
                            throw new HttpRequestException($"Response status code does not indicate success: {response.StatusCode}");
                        }

                        string responseBody = await response.Content.ReadAsStringAsync();
                        var iterationPayload = JsonConvert.DeserializeObject<IterationPayload>(responseBody);
                        return iterationPayload.Value;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"An error occurred while fetching pull request iterations: {ex.Message}");
                        throw;
                    }
                }
            }

            List<string> AssignTaskToReviewer(string term, PullRequestPayload payload)
            {
                _logger.LogInformation($"Assigning tasks related to '{term}' for pull request");
                switch (term.ToLower())
                {
                    case "srs review":
                        reviewers = new List<string> { "Sara", "John" };
                        break;

                    case "urd review":
                        reviewers = new List<string> { "Ali", "Joe" };
                        break;

                    default:
                        _logger.LogWarning($"No specific reviewers assigned for the term '{term}'");
                        break;
                }
                return reviewers;

                //_logger.LogInformation($"Resource: {JsonConvert.SerializeObject(payload.Resource, Formatting.Indented)}");

            }
             async Task<List<Change>> GetPullRequestChanges(string repositoryId, int pullRequestId, int iterationId)
            {
                string url = $"https://dev.azure.com/{organization}/{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/iterations/{iterationId}/changes?api-version=7.2-preview.1";
                using (var client = new HttpClient())
                {
                    try
                    {
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{string.Empty}:{personalAccessToken}")));

                        // Log the URL being requested
                        //_logger.LogInformation($"Requesting URL: {url}");

                        HttpResponseMessage response = await client.GetAsync(url);

                        if (!response.IsSuccessStatusCode)
                        {
                            string errorResponse = await response.Content.ReadAsStringAsync();
                            _logger.LogError($"Error: {response.StatusCode}. Response: {errorResponse}");
                            throw new HttpRequestException($"Response status code does not indicate success: {response.StatusCode}");
                        }

                        string responseBody = await response.Content.ReadAsStringAsync();
                        //_logger.LogInformation($"Raw JSON response: {responseBody}");
                        var changePayload = JsonConvert.DeserializeObject<ChangePayload>(responseBody);
                        //_logger.LogInformation($"Requesting changePayload: {changePayload.ChangeEntries}");
                        return changePayload.ChangeEntries; 
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"An error occurred while fetching pull request changes: {ex.Message}");
                        throw;
                    }
                }
            }
        }


        private async Task CreateWorkItem(string webhookPayload, string title, List<string> customfeildval)
        {
            string customFieldValuesJson = JsonConvert.SerializeObject(customfeildval);
            string workItemType = "Task";

            string createWorkItemUrl = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/${workItemType}?api-version=6.1-preview.3";

            var jsonBody = $@"[
            {{ ""op"": ""add"", ""path"": ""/fields/System.Title"", ""value"":""{title}""}},
            {{ ""op"": ""add"", ""path"": ""/fields/System.Description"", ""value"": ""Description of the work item"" }},
            {{ ""op"": ""add"", ""path"": ""/fields/Custom.reviewer_suggestion"", ""value"": {customFieldValuesJson} }},
            {{ ""op"": ""add"", ""path"": ""/fields/Microsoft.VSTS.Common.Priority"", ""value"": 2 }},
            {{ ""op"": ""add"", ""path"": ""/fields/Microsoft.VSTS.Common.Severity"", ""value"": ""3 - Medium"" }}

            ]";
            

            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json-patch+json");

            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json-patch+json"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{personalAccessToken}")));

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsync(createWorkItemUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Work item created successfully.");


                    string workId = await ExtractAndLogWorkItemId(response);
                    await FetchWorkItems(title, workId); // Fetch work items after successful creation
                }
                else
                {
                    try
                    {
                        string responseContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError($"Failed to create work item. Status code: {response.StatusCode}");
                        _logger.LogError(responseContent);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to read error response content");
                    }
                }

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request to create work item failed");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred while creating work item");
                return;
            }

        }

        private async Task FetchWorkItems(string reqtitle,string workId)
        {
            

            string fetchWorkItemsUrl = $"https://dev.azure.com/{organization}/{project}/_apis/wit/wiql?api-version=7.0";

            var wiqlQuery = new
            {
                query = "Select [System.Id], [System.Title], [System.State] From WorkItems Order By [System.ChangedDate] Desc"
            };

            var content = new StringContent(JsonConvert.SerializeObject(wiqlQuery), Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{personalAccessToken}")));

            HttpResponseMessage response;

            try
            {
                response = await _httpClient.PostAsync(fetchWorkItemsUrl, content);
                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    var jsonResponse = JObject.Parse(responseBody);
                    var workItems = jsonResponse["workItems"] as JArray;
                    if (workItems != null)
                    {
                        _logger.LogInformation("Work items retrieved successfully:");

                        foreach (var workItem in workItems)
                        {
                            var workItemUrl = workItem["url"]?.ToString();
                            if (!string.IsNullOrEmpty(workItemUrl))
                            {
                                var workItemResponse = await _httpClient.GetAsync(workItemUrl);

                                if (workItemResponse.IsSuccessStatusCode)
                                {
                                    var workItemDetails = JObject.Parse(await workItemResponse.Content.ReadAsStringAsync());

                                    var id = workItemDetails["id"]?.ToString();
                                    var fields = workItemDetails["fields"] as JObject;
                                    var title = fields?["System.Title"]?.ToString();
                                    var state = fields?["System.State"]?.ToString();

                                    //_logger.LogInformation($"ID: {id}, Title: {title ?? "N/A"}, State: {state ?? "N/A"}");
                                    if (title == reqtitle && id == workId)
                                    {
                                        _logger.LogInformation("Successfully verified workitem");
                                        _logger.LogInformation($"ID: {id}, Title: {title ?? "N/A"}, State: {state ?? "N/A"}");
                                        _logger.LogInformation(" pull request send for appproval");
                                        break;
                                    }
                                    else
                                    {
                                        _logger.LogError("canot verify workitem succcessful creation");
                                    }
                                }
                                else
                                {
                                    _logger.LogError($"Failed to fetch details for work item at URL {workItemUrl}. Status code: {workItemResponse.StatusCode}");
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Work item URL is missing.");
                            }
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No work items found in the response.");
                    }
                }

                else
                {
                    try
                    {
                        string responseContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError($"Failed to fetch work items. Status code: {response.StatusCode}");
                        _logger.LogError(responseContent);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to read error response content");
                    }
                }

            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request to fetch work items failed");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred while fetching work items");
                return;
            }

            
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Webhook Service is stopping.");
            _httpListener.Stop();
            await base.StopAsync(stoppingToken);
        }

        public override void Dispose()
        {
            _httpClient.Dispose();
            _httpListener.Close();
            base.Dispose();
        }
        private async Task<string> ExtractAndLogWorkItemId(HttpResponseMessage response)
        {
            string workItemId = null;

            if (response.Headers.Location != null)
            {
                string locationHeader = response.Headers.Location.ToString();
                workItemId = locationHeader.Substring(locationHeader.LastIndexOf('/') + 1);
            }
            else
            {
                // If the Location header is null, parse the response content to get the work item ID
                var responseContent = await response.Content.ReadAsStringAsync();
                dynamic responseObject = Newtonsoft.Json.JsonConvert.DeserializeObject(responseContent);
                if (responseObject != null && responseObject.id != null)
                {
                    workItemId = responseObject.id.ToString();
                }
            }

            if (!string.IsNullOrEmpty(workItemId))
            {
                _logger.LogInformation($"Work item ID: {workItemId}");
                return workItemId;
            }
            else
            {
                _logger.LogError("Failed to retrieve the work item ID.");
                return null;
            }
        }
    }

    public class PullRequestPayload
    {
        public Resource Resource { get; set; }
        // Add other properties as needed
    }

    public class Resource
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public Repository Repository { get; set; }
        public CreatedBy CreatedBy { get; set; }
        public List<Change> Changes { get; set; }
        public int PullRequestId { get; set; }
    }

    public class Repository
    {
        public string Name { get; set; }
        public string Id { get; set; }
        // Add other properties as needed
    }

    public class CreatedBy
    {
        public string DisplayName { get; set; }
        public string Url { get; set; }
        // Add other properties as needed
    }
    public class Change
    {
        [JsonProperty("changeTrackingId")]
        public int ChangeTrackingId { get; set; }

        [JsonProperty("changeId")]
        public int ChangeId { get; set; }

        [JsonProperty("item")]
        public Item Item { get; set; }

        [JsonProperty("changeType")]
        public string ChangeType { get; set; }
    }

    public class Item
    {
        [JsonProperty("objectId")]
        public string ObjectId { get; set; }

        [JsonProperty("originalObjectId")]
        public string OriginalObjectId { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }
    }
    public class ChangePayload
    {
        [JsonProperty("changeEntries")]
        public List<Change> ChangeEntries { get; set; }
    }
    public class Iteration
    {
        public int Id { get; set; }
        // Define other properties based on the API response
    }

    public class IterationPayload
    {
        public List<Iteration> Value { get; set; }
    }
}


 
 
 */
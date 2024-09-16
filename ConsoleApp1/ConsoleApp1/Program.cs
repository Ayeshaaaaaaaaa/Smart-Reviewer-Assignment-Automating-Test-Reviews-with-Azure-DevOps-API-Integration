
 using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Http;
using System.Text.Json;

namespace ConsoleApp1
{
    class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHttpClient(); // Register IHttpClientFactory for making HTTP requests
                    services.AddHostedService<WebhookService>(); // Register WebhookService as a hosted service
                });
    }

    public class WebhookService : BackgroundService
    {
        private readonly ILogger<WebhookService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly HttpListener _httpListener;
        private readonly string _organization = "isbIntern2024";
        private string _project;
        private readonly string _personalAccessToken = "etoyf6zija76t6ggchpvixh2jnbzpynyjzyuuwn2nc2t43xqy2dq"; // Retrieve PAT from environment variable

        public WebhookService(ILogger<WebhookService> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://localhost:8001/"); // Prefix for listening to incoming HTTP requests
        }

        /// <summary>
        /// Starts the HTTP listener and processes incoming webhook events asynchronously.
        /// </summary>
        /// <param name="stoppingToken">Cancellation token to stop the service.</param>
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
                        using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
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

        /// <summary>
        /// Handles the incoming webhook payload, processes it, and creates work items if necessary.
        /// </summary>
        /// <param name="webhookPayload">The raw JSON payload from the webhook.</param>
        private async Task HandleWebhook(string webhookPayload)
        {
            _logger.LogInformation("Webhook received");
            List<string> reviewers = new List<string>();

            try
            {
                var pullRequestPayload = JsonConvert.DeserializeObject<PullRequestPayload>(webhookPayload);

                // Extract relevant information from the payload
                string title = pullRequestPayload.Resource.Title;
                string description = pullRequestPayload.Resource.Description;
                string status = pullRequestPayload.Resource.Status;
                string repositoryName = pullRequestPayload.Resource.Repository.Name;
                string createdBy = pullRequestPayload.Resource.CreatedBy.DisplayName;
                string repositoryId = pullRequestPayload.Resource.Repository?.Id;
                int pullRequestId = pullRequestPayload.Resource.PullRequestId;
                _project = pullRequestPayload.Resource.Repository.Project.Name;
                var reviewTerms = new List<string> { "urd review", "srs review", "design review", "code review", "other" };

                // Log extracted information for debugging and record-keeping
                _logger.LogInformation($"Title: {title}");
                _logger.LogInformation($"Description: {description}");
                _logger.LogInformation($"Status: {status}");
                _logger.LogInformation($"Repository: {repositoryName}");
                _logger.LogInformation($"Created By: {createdBy}");
                _logger.LogInformation($"project : {_project}");

                // Check if changes need to be fetched
                if (pullRequestPayload.Resource.Changes == null)
                {
                    try
                    {
                        
                        var iterations = await GetPullRequestIterations(repositoryId, pullRequestId);

                        if (iterations == null || !iterations.Any())
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
                        return;
                    }
                }

                bool check = false;
                foreach (var term in reviewTerms)
                {
                    bool titleFound = await CheckAndAssignTasks(term, title, pullRequestPayload);
                    if (titleFound)
                    {
                        check = true;
                        break;
                    }
                }

                if (!check)
                {
                    
                    _logger.LogError("Cannot approve pull request! Check your folder and title name for typing errors.");
                    string comment = "Cannot approve pull request! Check your folder and title name for typing errors.";
                    await RejectPullRequestWithComment(comment, repositoryId, pullRequestId);
                    //await UpdatePullRequestStatus("Reject", "Pull request contains typing errors.", repositoryId, pullRequestId);
                    await AbandonPullRequest(repositoryId, pullRequestId);

                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while handling the webhook payload.");
            }
        }
        private async Task RejectPullRequestWithComment(string message, string repositoryId, int pullRequestId)
        {
            string url = $"https://dev.azure.com/{_organization}/{_project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.2-preview.1";

            var comment = new
            {
                comments = new[]
                {
                new
                {
                    content = message,
                    commentType = 1
                }
            },
                status = 1
            };

            var json = JsonConvert.SerializeObject(comment);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = content
            };

            var byteArray = Encoding.ASCII.GetBytes($":{_personalAccessToken}");
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            Console.WriteLine("Comment added to pull request.");

        }
        private  async Task AbandonPullRequest(string repositoryId, int pullRequestId)
        {
            string url = $"https://dev.azure.com/{_organization}/{_project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}?api-version=7.2-preview.2";

            var update = new
            {
                status = "abandoned"
            };

            var json = JsonConvert.SerializeObject(update);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = content
            };

            var byteArray = Encoding.ASCII.GetBytes($":{_personalAccessToken}");
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

            try
            {
                var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                Console.WriteLine("Pull request abandoned.");
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"Request error: {e.Message}");
            }
        }
        /// <summary>
        /// Checks if the pull request title or file paths contain a specific term and assigns tasks accordingly.
        /// </summary>
        /// <param name="term">The review term to check for.</param>
        /// <param name="title">The title of the pull request.</param>
        /// <param name="payload">The payload containing pull request details.</param>
        /// <returns>True if tasks were assigned; otherwise, false.</returns>
        private async Task<bool> CheckAndAssignTasks(string term, string title, PullRequestPayload payload)
        {
            bool isTitleContainsTerm = title?.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
            bool isFilePathContainsTerm = payload?.Resource?.Changes != null && payload.Resource.Changes.Any(change =>
                change?.Item?.Path != null && change.Item.Path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);

            if (isTitleContainsTerm && isFilePathContainsTerm)
            {
                _logger.LogInformation($"The pull request contains '{term}' in the title or file paths.");
                var reqReviewers = AssignTaskToReviewer(term);
                await CreateWorkItem(payload, title, reqReviewers);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Determines the list of reviewers to assign based on the review term.
        /// </summary>
        /// <param name="term">The review term for which to assign reviewers.</param>
        /// <returns>A list of reviewer names.</returns>
        private List<string> AssignTaskToReviewer(string term)
        {
            _logger.LogInformation($"Assigning tasks related to '{term}' for pull request");
            switch (term.ToLower())
            {
                case "srs review":
                    return new List<string> { "Sara", "John" };

                case "urd review":
                    return new List<string> { "Ali", "Joe" };

                default:
                    _logger.LogWarning($"No specific reviewers assigned for the term '{term}'");
                    return new List<string>();
            }
        }

        /// <summary>
        /// Retrieves pull request iterations from Azure DevOps.
        /// </summary>
        /// <param name="repositoryId">The ID of the repository.</param>
        /// <param name="pullRequestId">The ID of the pull request.</param>
        /// <returns>A list of pull request iterations.</returns>
        private async Task<List<Iteration>> GetPullRequestIterations(string repositoryId, int pullRequestId)
        {
            string url = $"https://dev.azure.com/{_organization}/{_project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/iterations?api-version=7.2-preview.1";
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_personalAccessToken}")));

            try
            {
                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync();
                var iterationPayload = JsonConvert.DeserializeObject<IterationPayload>(responseBody);
                return iterationPayload.Value;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"An error occurred while fetching pull request iterations: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Retrieves pull request changes for a specific iteration from Azure DevOps.
        /// </summary>
        /// <param name="repositoryId">The ID of the repository.</param>
        /// <param name="pullRequestId">The ID of the pull request.</param>
        /// <param name="iterationId">The ID of the iteration.</param>
        /// <returns>A list of changes for the specified pull request iteration.</returns>
        private async Task<List<Change>> GetPullRequestChanges(string repositoryId, int pullRequestId, int iterationId)
        {
            string url = $"https://dev.azure.com/{_organization}/{_project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/iterations/{iterationId}/changes?api-version=7.2-preview.1";
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_personalAccessToken}")));

            try
            {
                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync();
                var changePayload = JsonConvert.DeserializeObject<ChangePayload>(responseBody);
                return changePayload.ChangeEntries;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"An error occurred while fetching pull request changes: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Creates a work item in Azure DevOps with details from the pull request.
        /// </summary>
        /// <param name="payload">The pull request payload containing details for the work item.</param>
        /// <param name="title">The title of the work item.</param>
        /// <param name="reviewers">A list of reviewers to assign to the work item.</param>
        private async Task CreateWorkItem(PullRequestPayload payload, string title, List<string> reviewers)
        {
            string workItemType = "Task";
            string createWorkItemUrl = $"https://dev.azure.com/{_organization}/{_project}/_apis/wit/workitems/${workItemType}?api-version=6.1-preview.3";

            // Construct the JSON body for the work item creation
            string customFieldValuesJson = JsonConvert.SerializeObject(reviewers.Select(reviewer => new { value = reviewer }));
            string jsonBody = $@"[
                {{ ""op"": ""add"", ""path"": ""/fields/System.Title"", ""value"": ""{title}"" }},
                {{ ""op"": ""add"", ""path"": ""/fields/System.Description"", ""value"": ""{payload.Resource.Description}"" }},
                {{ ""op"": ""add"", ""path"": ""/fields/Custom.reviewer_suggestion"", ""value"": {customFieldValuesJson} }},
                {{ ""op"": ""add"", ""path"": ""/fields/Microsoft.VSTS.Common.Priority"", ""value"": 2 }},
                {{ ""op"": ""add"", ""path"": ""/fields/Microsoft.VSTS.Common.Severity"", ""value"": ""3 - Medium"" }}
            ]";

            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json-patch+json");
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_personalAccessToken}")));

            try
            {
                HttpResponseMessage response = await client.PostAsync(createWorkItemUrl, content);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Work item created successfully.");
                    string workId = await ExtractWorkItemId(response);
                }
                else
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"Failed to create work item. Status code: {response.StatusCode}");
                    _logger.LogError(responseContent);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP request to create work item failed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error occurred while creating work item");
            }
        }

        /// <summary>
        /// Extracts the work item ID from the response after creating the work item.
        /// </summary>
        /// <param name="response">The HTTP response containing the created work item details.</param>
        /// <returns>The ID of the created work item.</returns>
        private async Task<string> ExtractWorkItemId(HttpResponseMessage response)
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
                _logger.LogInformation($"Work item reterived Id: {workItemId}");
                _logger.LogInformation("pull request send for approval");
                return workItemId;
            }
            else
            {
                _logger.LogError("Failed to retrieve the work item.");
                return null;
            }
        }

        /// <summary>
        /// Placeholder method to fetch additional details about work items. 
        /// </summary>
        /// <param name="title">The title of the work item.</param>
        /// <param name="workId">The ID of the work item.</param>

    }

    // Define classes for deserialization
    public class PullRequestPayload
    {
        public Resource Resource { get; set; }
    }

    public class Resource
    {
        public string Title { get; set; }
        public Project Project { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public Repository Repository { get; set; }
        public User CreatedBy { get; set; }
        public List<Change> Changes { get; set; }
        public int PullRequestId { get; set; }
    }

    public class Repository
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public Project Project { get; set; }
    }

    public class User
    {
        public string DisplayName { get; set; }
    }

    public class Iteration
    {
        public int Id { get; set; }
    }

    public class IterationPayload
    {
        public List<Iteration> Value { get; set; }
    }

    public class Change
    {
        public Item Item { get; set; }
    }

    public class Item
    {
        public string Path { get; set; }
    }

    public class ChangePayload
    {
        public List<Change> ChangeEntries { get; set; }
    }
    public class AzureDevOpsWebhookPayload
    {
        public Resource Resource { get; set; }
    }

    public class Project
    {
        public string Name { get; set; }
    }
}

 
 
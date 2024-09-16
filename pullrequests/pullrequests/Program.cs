
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
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing.Internal;
using System.Xml.Serialization;

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
        List<string> reviewerUsernames;
        ReviewerService reviewerService;
        private readonly string _personalAccessToken; // Retrieve PAT from environment variable

        public WebhookService(ILogger<WebhookService> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://localhost:8001/"); // Prefix for listening to incoming HTTP requests


            string xmlFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "C:\\Users\\ayesh\\source\\repos\\pullrequests\\reviewers.xml");
            reviewerService = new ReviewerService(xmlFilePath);

            // Retrieve PAT from the ReviewerService
            _personalAccessToken = reviewerService.GetPAT();

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
                bool titleFound = await CheckAndAssignTasks(title, pullRequestPayload, pullRequestId, repositoryId);
                if (titleFound)
                {
                    check = true;
                }
                if (!check)
                {

                    _logger.LogError("Cannot approve pull request! Check your folder and title name for typing errors.");
                    string comment = "Cannot approve pull request! Check your folder and title name for typing errors.";
                    await RejectPullRequestWithComment(comment, repositoryId, pullRequestId);
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
        private async Task AbandonPullRequest(string repositoryId, int pullRequestId)
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
        private async Task<bool> CheckAndAssignTasks(string title, PullRequestPayload payload, int pullId, string repoId)
        {
            string pattern = @"(srs|urd|design|code) review version (\d+)";
            Match match = Regex.Match(title, pattern, RegexOptions.IgnoreCase);
            List<string> reviewers;
            if (match.Success)
            {
                string reviewType = match.Groups[1].Value;
                int versionNumber = int.Parse(match.Groups[2].Value);

                bool isVersionFolderValid = payload?.Resource?.Changes != null && payload.Resource.Changes.Any(change =>
            change?.Item?.Path != null &&
           ValidateFolderPath(change.Item.Path, versionNumber));

                bool isTitleFolderValid = payload?.Resource?.Changes != null && payload.Resource.Changes.Any(change =>
            change?.Item?.Path != null &&
           ValidateTitlePath(change.Item.Path, reviewType));

                Console.WriteLine($"Version Number: {versionNumber}");
                Console.WriteLine($"Review Type: {reviewType}");
                if (isVersionFolderValid)
                {

                    _logger.LogInformation($"found '{reviewType}'");

                }
                if (isTitleFolderValid)
                {
                    _logger.LogInformation($"found");
                    string xmlFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "C:\\Users\\ayesh\\source\\repos\\pullrequests\\reviewers.xml");
                    ReviewerService reviewerService = new ReviewerService(xmlFilePath);

                    reviewers = reviewerService.AssignTaskToReviewers(reviewType);
                    _logger.LogInformation($"found {reviewers}");
                    if (reviewers != null)
                    {
                        await AddReviewerAsync(pullId, repoId, reviewers);
                        await CreateWorkItem(payload, title, reviewers);

                    }
                    else
                    {
                        Console.WriteLine("no reviewers found");
                    }

                }
                else { }

                return true;
            }

            else
            {
                Console.WriteLine("The input string does not match the expected pattern.");

                return false;

            }
            static bool ValidateFolderPath(string path, int titleVersion)
            {
                // The regex pattern now accommodates both single and double-digit versions for the folder number
                string pattern = $@"/code/{titleVersion:00}-version-\d{{1,2}}.*";
                return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase);
            }
            static bool ValidateTitlePath(string path, string title)
            {
                // The regex pattern now accommodates both single and double-digit versions for the folder number
                string pattern = $@"/documents/\d{{1,2}}-{title}.*";
                return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase);
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
            _logger.LogInformation($"Requesting URL: {url}");
            try
            {

                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"Raw JSON response: {responseBody}");
                var changePayload = JsonConvert.DeserializeObject<ChangePayload>(responseBody);
                _logger.LogInformation($"Requesting changePayload: {changePayload.ChangeEntries}");
                return changePayload.ChangeEntries;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"An error occurred while fetching pull request changes: {ex.Message}");
                throw;
            }
        }


        public async Task AddReviewerAsync(int pullRequestId, string repositoryId, List<string> reviewerUsernames)
        {
            foreach (var reviewer in reviewerUsernames)
            {
                string reviewerId = await GetUserIdByUser(reviewer);
                string url = $"https://dev.azure.com/{_organization}/{_project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/reviewers/{reviewerId}?api-version=7.1-preview.1";



                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var byteArray = Encoding.ASCII.GetBytes($":{_personalAccessToken}");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
                var payloadJson = new
                {
                    vote = 0,
                    id = reviewerId
                };
                var payload = new StringContent(JsonConvert.SerializeObject(payloadJson), Encoding.UTF8, "application/json");

                var response = await client.PutAsync(url, payload);
                var responseContent = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine("Reviewer added successfully.");
                }
                else
                {
                    Console.WriteLine("Error adding reviewer: " + response.ReasonPhrase);
                }
            }
        }

        public async Task<string> GetUserIdByUser(string username)
        {
            string url = $"https://vssps.dev.azure.com/{_organization}/_apis/identities?searchFilter=General&filterValue={username}&queryMembership=None&api-version=7.1-preview.1";

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var byteArray = Encoding.ASCII.GetBytes($":{_personalAccessToken}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            var response = await client.GetAsync(url);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var identities = JsonConvert.DeserializeObject<IdentitiesResponse>(responseContent);

                var user = identities.value.FirstOrDefault();
                if (user != null)
                {
                    _logger.LogInformation(user.id);
                    return user.id;
                }
                else
                {
                    Console.WriteLine("User not found");
                    return null;
                }
            }
            else
            {
                throw new Exception($"Error fetching user ID: {response.ReasonPhrase}");
            }
        }
        private async Task CreateWorkItem(PullRequestPayload payload, string title, List<string> reviewers)
        {
            string workItemType = "Task";
            string createWorkItemUrl = $"https://dev.azure.com/{_organization}/{_project}/_apis/wit/workitems/${workItemType}?api-version=7.1-preview.3";

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
    }
    public class ReviewerService
    {
        private readonly Reviewers _reviewerMappings;
        
        public ReviewerService(string xmlFilePath)
        {
            _reviewerMappings = LoadReviewers(xmlFilePath);
        }

        private Reviewers LoadReviewers(string filePath)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(Reviewers));
            using (FileStream fileStream = new FileStream(filePath, FileMode.Open))
            {
                return (Reviewers)serializer.Deserialize(fileStream);
            }
        }

        public List<string> AssignTaskToReviewers(string term)
        {
            var mapping = _reviewerMappings.Terms
                .FirstOrDefault(t => t.Name.Equals(term, StringComparison.OrdinalIgnoreCase));

            if (mapping != null)
            {
                Console.WriteLine($"Reviewers: {string.Join(", ", mapping.Reviewers)}");
                return mapping.Reviewers;
            }
            else
            {
                Console.WriteLine($"No specific reviewers assigned for the term '{term}'");
                return new List<string>();
            }
        }
        public string GetPAT()
        {
            return _reviewerMappings.PAT;
        }
    }
    // Define classes for deserialization
    public class PullRequestPayload
    {
        public Resource Resource { get; set; }
        public List<ChangeEntry> ChangeEntries { get; set; }
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
        public string PrincipalName { get; set; }
        public string Descriptor { get; set; }
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
        public string ObjectId { get; set; }
        public string OriginalObjectId { get; set; }
    }
    public class ChangeEntry
    {
        public int ChangeTrackingId { get; set; }
        public int ChangeId { get; set; }
        public Item Item { get; set; }
        public string ChangeType { get; set; }
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
    public class Identity
    {
        public string id { get; set; }
        public string displayName { get; set; }
        public string uniqueName { get; set; }
    }

    public class IdentitiesResponse
    {
        public List<Identity> value { get; set; }
    }
    [XmlRoot("Reviewers")]
    public class Reviewers
    {
        [XmlElement("PAT")]
        public string PAT { get; set; }
        [XmlElement("Term")]
        public List<Term> Terms { get; set; }
    }

    public class Term
    {
        [XmlAttribute("name")]
        public string Name { get; set; }

        [XmlElement("Reviewer")]
        public List<string> Reviewers { get; set; }
    }
}



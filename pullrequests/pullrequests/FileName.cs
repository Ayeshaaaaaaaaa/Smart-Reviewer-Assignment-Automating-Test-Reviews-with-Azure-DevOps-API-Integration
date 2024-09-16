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
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing.Internal;

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
                //var reviewTerms = new List<string> { "urd review", "srs review", "design review", "code review", "other" };
                
               






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
                
                    bool titleFound = await CheckAndAssignTasks(title, pullRequestPayload,pullRequestId,repositoryId);
                    if (titleFound)
                    {
                        check = true;
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
        private async Task<bool> CheckAndAssignTasks(string title, PullRequestPayload payload,int pullId,string repoId)
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
                if (isTitleFolderValid) {
                    _logger.LogInformation($"found");
                    reviewers =AssignTaskToReviewer(reviewType);
                    //await AssignReviewers(pullId,repoId, reviewers);
                    await AddReviewerAsync(pullId, repoId);
                }
            }

            else
            {
                Console.WriteLine("The input string does not match the expected pattern.");
                
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


            return true;

        }

        /// <summary>
        /// Determines the list of reviewers to assign based on the review term.
        /// </summary>
        /// <param name="term">The review term for which to assign reviewers.</param>
        /// <returns>A list of reviewer names.</returns>
        private List<string> AssignTaskToReviewer(string term)
        {

            _logger.LogInformation($"Assigning tasks related to '{term}' for pull request");
              // Replace with actual usernames or email addresses
            switch (term.ToLower())
            {
                case "srs":
                    reviewerUsernames= new List<string> { "ayesha.rafiq229@gmail.com", "rana_adil@hotmail.com" };
                    break;
                case "urd":
                    reviewerUsernames = new List<string> { "user1@example.com", "user2@example.com" };
                    break;
                default:
                    _logger.LogWarning($"No specific reviewers assigned for the term '{term}'");
                    break;
            }

            return reviewerUsernames;
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
        /*private  async Task AssignReviewers(int pullRequestId, string repositoryId,  List<string> reviewerUsernames)
        {
            var reviewers = new JArray();

            foreach (var username in reviewerUsernames)
            {
                var userId = await GetUserIdByUsername( username);
                if (userId != null)
                {
                    reviewers.Add(new JObject(new JProperty("id", userId)));
                    _logger.LogInformation($"userid {userId}");
                }
                else
                {
                    Console.WriteLine($"User '{username}' not found.");
                }
            }

            string url = $"https://dev.azure.com/{_organization}/{_project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/reviewers?api-version=7.1-preview.1";
            //""
            var client = _httpClientFactory.CreateClient();
            
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_personalAccessToken}")));

                Console.WriteLine("Reviewers .");
            var reviewersPayload = new JObject(new JProperty("reviewers", reviewers));
            var content = new StringContent(reviewersPayload.ToString(), Encoding.UTF8, "application/json");
            _logger.LogInformation($"content is {reviewersPayload}");
            var response = await client.PutAsync(url, content);
            var responseContent = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"Response Status Code: {response.StatusCode}");
            _logger.LogInformation($"response is {responseContent}");
            response.EnsureSuccessStatusCode();
            _logger.LogInformation($"response is {responseContent}");
                 Console.WriteLine("Reviewers assigned successfully.");
        }

private async Task<string> GetUserIdByUsername(string username)
{
    string url = $"https://vssps.dev.azure.com/{_organization}/_apis/graph/users?api-version=7.1-preview.1";
    var client = _httpClientFactory.CreateClient();

    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_personalAccessToken}")));

    var response = await client.GetAsync(url);
    response.EnsureSuccessStatusCode();

    // var content = await response.Content.ReadAsStringAsync();
    //  var users = JObject.Parse(content)["value"];

    // var user = users.FirstOrDefault(u => u["principalName"].ToString().Equals(username, StringComparison.OrdinalIgnoreCase));
    // _logger.LogInformation($"user {user?["descriptor"].ToString()}");
    // return user?["descriptor"].ToString();


    var content = await response.Content.ReadAsStringAsync();

    var users = JsonConvert.DeserializeObject<JObject>(content)["value"].ToObject<List<User>>();

    var user = users.FirstOrDefault(u => u.PrincipalName.Equals(username, StringComparison.OrdinalIgnoreCase));
    _logger.LogInformation($"user {user?.Descriptor}");
    _logger.LogInformation($"user {user?.PrincipalName}");

    return user?.Descriptor;
}

public async Task AddReviewerAsync(int pullRequestId, string repositoryId)
{
    string reviewerId = await GetUserIdByUsername("ayesha.rafiq229@gmail.com");
    string url = $"https://dev.azure.com/{_organization}/{_project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/reviewers/{reviewerId}?api-version=7.1-preview.1";



    var client = _httpClientFactory.CreateClient();
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    var byteArray = Encoding.ASCII.GetBytes($":{_personalAccessToken}");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

    //var payload = new StringContent("[{\"id\": \"" + reviewerId + "\", \"vote\": 0}]", Encoding.UTF8, "application/json");
    //var payload = new StringContent("[{\"vote\": 0, \"id\": \"" + reviewerId + "\"}]", Encoding.UTF8, "application/json");

    //var response = await client.PatchAsync(url, payload);
    //var responseContent = await response.Content.ReadAsStringAsync();

    //Console.WriteLine($"Response Status Code: {response.StatusCode}");
    //_logger.LogInformation($"response is {responseContent}");
    var payloadJson = new
    {
        vote = 0,
        id = reviewerId,
        isFlagged = true // or hasDeclined = true
    };
    var payload = new StringContent(JsonConvert.SerializeObject(payloadJson), Encoding.UTF8, "application/json");
    _logger.LogInformation($"payload is {payload}");
    var response = await client.PatchAsync(url, payload);
    var responseContent = await response.Content.ReadAsStringAsync();
    _logger.LogInformation($"response is {responseContent}");
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
}



 
 
 
 */
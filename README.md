# Smart-Reviewer-Assignment-Automating-Test-Reviews-with-Azure-DevOps-API-Integration
A system that processes webhook events for managing Git-based pull requests and task assignments using Azure DevOps REST APIs. It evaluates pull request payloads against predefined criteria to automatically approve, reject, or assign reviewers. The system also creates and tracks work items through Azure DevOps integration.<br>
This system is a console-based application that processes incoming webhook events from Azure DevOps, specifically related to pull requests. It listens for HTTP POST requests and evaluates the contents of pull request payloads to decide whether to approve or reject them based on predefined criteria. The system uses Azure DevOps REST APIs to manage pull requests and create work items.<br>
## Technologies Used
•	**C# / .NET Core:** The application is built as a .NET Core Console Application.<br>
•	**Azure DevOps API**: The system interacts with Azure DevOps through its REST APIs to handle pull requests and create work items.<br>
•	**Microsoft.Extensions.Hosting:** Provides hosting capabilities to run the application as a background service.<br>
•	**HttpListener:** Used to listen for incoming webhook events.<br>
•	**IHttpClientFactory**: Manages HTTP requests to external APIs.<br>
•	**Json.NET (Newtonsoft.Json):** For JSON deserialization and manipulation of webhook payloads.<br>
•	**Azure DevOps Personal Access Token (PAT):** Used for authenticating API requests.<br>
 ## Functionalities
•	**Webhook Event Listener:**<br>
o	The application uses an HTTP listener to receive incoming POST requests from Azure DevOps containing pull request information.<br>
o	Upon receiving a request, the application processes the payload asynchronously.<br>
•	**Pull Request Validation:**<br>
o	Extracts relevant details like title, description, repository name, and pull request status from the webhook payload.<br>
o	The system validates the pull request title and file changes based on predefined terms (e.g., "urd review", "srs review", etc.).<br>
•	**Automated Task Assignment:**<br>
o	Based on the detected terms in the pull request, the system assigns tasks to specific reviewers.<br>
o	Reviewers are selected based on the type of review requested (e.g., SRS, URD).<br>
•	**Fetching Pull Request Iterations and Changes:**<br>
o	If the pull request lacks change details, the system makes an API call to Azure DevOps to fetch the latest iterations and associated changes.<br>
o	These changes are used to evaluate whether the pull request meets the required review criteria.<br>
•	**Approval/Rejection Mechanism:**<br>
o	If the pull request meets the criteria, the system proceeds with assigning work items to the relevant reviewers.<br>
o	If validation fails (e.g., incorrect folder or title names), the system rejects the pull request and comments on the pull request with the reason.<br>
o	The pull request is abandoned if it cannot be approved.<br>
•	**Work Item Creation:**<br>
o	The system creates a work item in Azure DevOps for pull requests that pass validation, assigning the task to the reviewers.<br>

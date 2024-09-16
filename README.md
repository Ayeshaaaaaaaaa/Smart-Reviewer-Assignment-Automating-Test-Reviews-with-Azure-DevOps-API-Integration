# Smart-Reviewer-Assignment-Automating-Test-Reviews-with-Azure-DevOps-API-Integration
A system that processes webhook events for managing Git-based pull requests and task assignments using Azure DevOps REST APIs. It evaluates pull request payloads against predefined criteria to automatically approve, reject, or assign reviewers. The system also creates and tracks work items through Azure DevOps integration.<br>
This system is a console-based application that processes incoming webhook events from Azure DevOps, specifically related to pull requests. It listens for HTTP POST requests and evaluates the contents of pull request payloads to decide whether to approve or reject them based on predefined criteria. The system uses Azure DevOps REST APIs to manage pull requests and create work items.<br>
## Technologies Used
•	**C# / .NET Core:** The application is built as a .NET Core Console Application.
•	**Azure DevOps API**: The system interacts with Azure DevOps through its REST APIs to handle pull requests and create work items.
•	**Microsoft.Extensions.Hosting:** Provides hosting capabilities to run the application as a background service.
•	**HttpListener:** Used to listen for incoming webhook events.
•	**IHttpClientFactory**: Manages HTTP requests to external APIs.
•	**Json.NET (Newtonsoft.Json):** For JSON deserialization and manipulation of webhook payloads.
•	**Azure DevOps Personal Access Token (PAT):** Used for authenticating API requests.
 ## Functionalities
•	**Webhook Event Listener:**
o	The application uses an HTTP listener to receive incoming POST requests from Azure DevOps containing pull request information.
o	Upon receiving a request, the application processes the payload asynchronously.
•	**Pull Request Validation:**
o	Extracts relevant details like title, description, repository name, and pull request status from the webhook payload.
o	The system validates the pull request title and file changes based on predefined terms (e.g., "urd review", "srs review", etc.).
•	**Automated Task Assignment:**
o	Based on the detected terms in the pull request, the system assigns tasks to specific reviewers.
o	Reviewers are selected based on the type of review requested (e.g., SRS, URD).
•	**Fetching Pull Request Iterations and Changes:**
o	If the pull request lacks change details, the system makes an API call to Azure DevOps to fetch the latest iterations and associated changes.
o	These changes are used to evaluate whether the pull request meets the required review criteria.
•	**Approval/Rejection Mechanism:**
o	If the pull request meets the criteria, the system proceeds with assigning work items to the relevant reviewers.
o	If validation fails (e.g., incorrect folder or title names), the system rejects the pull request and comments on the pull request with the reason.
o	The pull request is abandoned if it cannot be approved.
•	**Work Item Creation:**
o	The system creates a work item in Azure DevOps for pull requests that pass validation, assigning the task to the reviewers.

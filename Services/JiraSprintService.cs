using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;

namespace CICDTrg.Services
{
    public class JiraSprintService
    {
        private readonly HttpClient _httpClient;

        public JiraSprintService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            
            // Setup Auth from Environment Variables
            var email = Environment.GetEnvironmentVariable("JIRA_EMAIL", EnvironmentVariableTarget.User);
            var token = Environment.GetEnvironmentVariable("JIRA_API_TOKEN", EnvironmentVariableTarget.User);
            var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{email}:{token}"));
            
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
        }

        public async Task<(DateTime StartDate, DateTime? EndDate, string State)> GetSprintDatesAsync(string sprintName)
        {
            var baseUrl = Environment.GetEnvironmentVariable("JIRA_BASE_URL", EnvironmentVariableTarget.User);

            // 1. Search for any issue in this sprint to get a reference
            var jqlRequest = new { jql = $"Sprint = \"{sprintName}\"", maxResults = 1 };
            var content = new StringContent(JsonSerializer.Serialize(jqlRequest), Encoding.UTF8, "application/json");
            
            var searchRes = await _httpClient.PostAsync($"{baseUrl}/rest/api/3/search/jql", content);
            searchRes.EnsureSuccessStatusCode();
            
            var searchJson = await searchRes.Content.ReadAsStringAsync();
            using var searchDoc = JsonDocument.Parse(searchJson);
            
            var issuesArray = searchDoc.RootElement.GetProperty("issues");
            if (issuesArray.GetArrayLength() == 0)
                throw new Exception($"No issues found for Sprint: {sprintName}");

            string issueKey = issuesArray[0].GetProperty("id").GetString();

            // 2. Query the Agile API for that specific issue to extract the Sprint object
            var agileRes = await _httpClient.GetAsync($"{baseUrl}/rest/agile/1.0/issue/{issueKey}");
            agileRes.EnsureSuccessStatusCode();
            
            var agileJson = await agileRes.Content.ReadAsStringAsync();
            using var agileDoc = JsonDocument.Parse(agileJson);
            
            var sprintObj = agileDoc.RootElement.GetProperty("fields").GetProperty("sprint");
            
            return (
                StartDate: sprintObj.GetProperty("startDate").GetDateTime(),
                EndDate: sprintObj.TryGetProperty("endDate", out var endDateProp) && endDateProp.ValueKind != JsonValueKind.Null ? endDateProp.GetDateTime() : null,
                State: sprintObj.GetProperty("state").GetString() // "active" or "closed"
            );
        }
    }
}

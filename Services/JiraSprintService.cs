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
        public async Task<(string Summary, string DescriptionHtml, string DescriptionRaw)> GetIssueDetailsAsync(string issueKey)
        {
            var baseUrl = Environment.GetEnvironmentVariable("JIRA_BASE_URL", EnvironmentVariableTarget.User);
            
            var res = await _httpClient.GetAsync($"{baseUrl}/rest/api/2/issue/{issueKey}?expand=renderedFields");
            if (!res.IsSuccessStatusCode) return (null, null, null);
            
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var summary = doc.RootElement.GetProperty("fields").TryGetProperty("summary", out var summaryProp) && summaryProp.ValueKind != JsonValueKind.Null ? summaryProp.GetString() : "";
            
            string descriptionHtml = "";
            if (doc.RootElement.TryGetProperty("renderedFields", out var renderedFields) && renderedFields.TryGetProperty("description", out var descHtmlProp) && descHtmlProp.ValueKind != JsonValueKind.Null)
            {
                descriptionHtml = descHtmlProp.GetString();
            }

            string descriptionRaw = "";
            if (doc.RootElement.GetProperty("fields").TryGetProperty("description", out var descRawProp) && descRawProp.ValueKind != JsonValueKind.Null)
            {
                descriptionRaw = descRawProp.GetString();
            }

            foreach (var acField in new[] { "customfield_10104", "customfield_10056" })
            {
                if (doc.RootElement.GetProperty("fields").TryGetProperty(acField, out var acRawProp) && acRawProp.ValueKind != JsonValueKind.Null)
                {
                    string acRaw = acRawProp.GetString();
                    if (!string.IsNullOrWhiteSpace(acRaw))
                    {
                        descriptionRaw += $"\n\nAcceptance Criteria:\n{acRaw}";
                        
                        if (doc.RootElement.TryGetProperty("renderedFields", out var rf) && rf.TryGetProperty(acField, out var acHtmlProp) && acHtmlProp.ValueKind != JsonValueKind.Null)
                        {
                            descriptionHtml += $"<br/><h3>Acceptance Criteria</h3>{acHtmlProp.GetString()}";
                        }
                        else
                        {
                            descriptionHtml += $"<br/><h3>Acceptance Criteria</h3><div style='white-space: pre-wrap;'>{acRaw}</div>";
                        }
                        break; 
                    }
                }
            }
            
            return (summary, descriptionHtml, descriptionRaw);
        }
    }
}

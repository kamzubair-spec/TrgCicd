using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CICDTrg.Services
{
    public class DeepSeekExplainerService
    {
        private readonly HttpClient _httpClient;

        public DeepSeekExplainerService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            
            var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.User);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        public async Task<string> ExplainChangesAsync(string gitDiff)
        {
            var model = Environment.GetEnvironmentVariable("DEEPSEEK_FLASH_MODEL", EnvironmentVariableTarget.User) ?? "deepseek-chat";

            var requestBody = new
            {
                model = model,
                messages = new[]
                {
                    new { 
                        role = "system", 
                        content = "You are a Salesforce Architect. I will provide a Git diff. Please summarize the changes in plain English grouped by specific component/file. Keep it quick, easy to read, and highlight the business logic changes. Ignore formatting/spacing changes." 
                    },
                    new { role = "user", content = $"Git Diff:\n{gitDiff}" }
                },
                temperature = 0.2
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("https://api.deepseek.com/chat/completions", content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }

        public async Task<string> ReviewPRAsync(string gitDiff)
        {
            var model = Environment.GetEnvironmentVariable("DEEPSEEK_FLASH_MODEL", EnvironmentVariableTarget.User) ?? "deepseek-chat";

            var requestBody = new
            {
                model = model,
                messages = new[]
                {
                    new { 
                        role = "system", 
                        content = "You are a Senior Salesforce Architect. I will provide a Pull Request Git diff. Provide a highly actionable, concise code review. Highlight anti-patterns, SOQL/DML in loops, missing test coverage, or security flaws. Group feedback logically. If it is good, approve it." 
                    },
                    new { role = "user", content = $"PR Diff:\n{gitDiff}" }
                },
                temperature = 0.2
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            try
            {
                var response = await _httpClient.PostAsync("https://api.deepseek.com/chat/completions", content);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"DeepSeek API Failed: {ex.Message}");
            }
        }
    }
}

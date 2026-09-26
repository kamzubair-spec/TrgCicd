using System;
using System.IO;
using System.Collections.Generic;
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
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // Increase timeout to 5 minutes
            
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

            var configPath = Path.Combine(Directory.GetCurrentDirectory(), "pr_review_config.json");
            var config = new PrReviewConfig();
            
            if (File.Exists(configPath))
            {
                try
                {
                    var jsonContent = await File.ReadAllTextAsync(configPath);
                    config = JsonSerializer.Deserialize<PrReviewConfig>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? config;
                }
                catch { /* fallback to default */ }
            }

            string systemContent = $"You are a {config.Role}. {config.Prompt}\n\nSkills & Focus Areas:\n- " + string.Join("\n- ", config.Skills);

            var requestBody = new
            {
                model = model,
                messages = new[]
                {
                    new { 
                        role = "system", 
                        content = systemContent 
                    },
                    new { role = "user", content = $"PR Diff:\n{gitDiff}" }
                },
                temperature = config.Temperature
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

    public class PrReviewConfig
    {
        public string Role { get; set; } = "Senior Salesforce Architect";
        public string Prompt { get; set; } = "I will provide a Pull Request Git diff. Provide a highly actionable, concise code review. Group feedback logically. If it is good, approve it.";
        public List<string> Skills { get; set; } = new List<string> {
            "Highlight anti-patterns",
            "Identify SOQL/DML in loops",
            "Check for missing test coverage",
            "Find security flaws"
        };
        public double Temperature { get; set; } = 0.2;
    }
}

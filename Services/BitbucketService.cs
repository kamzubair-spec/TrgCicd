using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CICDTrg.Services
{
    public class BitbucketService
    {
        private readonly HttpClient _httpClient;

        public BitbucketService(HttpClient httpClient, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            _httpClient = httpClient;
            
            var username = configuration["BITBUCKET_USERNAME"] ?? Environment.GetEnvironmentVariable("BITBUCKET_USERNAME", EnvironmentVariableTarget.User);
            var appPassword = configuration["BITBUCKET_APP_PASSWORD"] ?? Environment.GetEnvironmentVariable("BITBUCKET_APP_PASSWORD", EnvironmentVariableTarget.User);
            var accessToken = configuration["BITBUCKET_ACCESS_TOKEN"] ?? Environment.GetEnvironmentVariable("BITBUCKET_ACCESS_TOKEN", EnvironmentVariableTarget.User);
            
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(appPassword))
            {
                var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{appPassword}"));
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
            }
            else if(!string.IsNullOrEmpty(accessToken))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }
        }

        public async Task<List<PRModel>> GetPullRequestsAsync(string workspace, string repoSlug, DateTime startDate, DateTime? endDate)
        {
            var prs = new List<PRModel>();
            var url = $"https://api.bitbucket.org/2.0/repositories/{workspace}/{repoSlug}/pullrequests?state=ALL&pagelen=20&sort=-created_on";

            while (!string.IsNullOrEmpty(url))
            {
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                
                bool fetchedOlderThanStartDate = false;

                foreach (var pr in doc.RootElement.GetProperty("values").EnumerateArray())
                {
                    var createdOn = pr.GetProperty("created_on").GetDateTime();
                    var destBranch = pr.GetProperty("destination").GetProperty("branch").GetProperty("name").GetString();
                    var srcBranch = pr.GetProperty("source").GetProperty("branch").GetProperty("name").GetString();
                    
                    if (createdOn < startDate)
                    {
                        fetchedOlderThanStartDate = true;
                        continue; // Keep checking other PRs in this page just in case, but flag that we crossed the boundary
                    }

                    if (!endDate.HasValue || createdOn <= endDate.Value)
                    {
                        prs.Add(new PRModel {
                            Id = pr.GetProperty("id").GetInt32(),
                            Title = pr.GetProperty("title").GetString(),
                            SourceBranch = srcBranch,
                            TargetBranch = destBranch,
                            State = pr.GetProperty("state").GetString(),
                            Author = pr.GetProperty("author").GetProperty("display_name").GetString(),
                            CreatedOn = createdOn
                        });
                    }
                }

                // If we found PRs older than our sprint start date, we don't need to fetch the next page
                if (fetchedOlderThanStartDate)
                {
                    break;
                }

                if (doc.RootElement.TryGetProperty("next", out var nextElement))
                {
                    url = nextElement.GetString();
                }
                else
                {
                    url = null;
                }
            }
            
            return prs;
        }

        public async Task<string> GetPullRequestDiffAsync(string workspace, string repoSlug, int prId)
        {
            var prUrl = $"https://api.bitbucket.org/2.0/repositories/{workspace}/{repoSlug}/pullrequests/{prId}";
            try 
            {
                var prResponse = await _httpClient.GetAsync(prUrl);
                if (!prResponse.IsSuccessStatusCode)
                {
                    var errorBody = await prResponse.Content.ReadAsStringAsync();
                    throw new Exception($"Bitbucket PR API Failed ({prUrl}): Status {prResponse.StatusCode}, Body: {errorBody}");
                }
                var prJson = await prResponse.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(prJson);
                var diffUrl = doc.RootElement.GetProperty("links").GetProperty("diff").GetProperty("href").GetString();
                
                var diffResponse = await _httpClient.GetAsync(diffUrl);
                if (!diffResponse.IsSuccessStatusCode)
                {
                    var errorBody = await diffResponse.Content.ReadAsStringAsync();
                    throw new Exception($"Bitbucket Diff API Failed ({diffUrl}): Status {diffResponse.StatusCode}, Body: {errorBody}");
                }
                return await diffResponse.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }
        }
    }

    public class PRModel
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string SourceBranch { get; set; }
        public string TargetBranch { get; set; }
        public string State { get; set; }
        public string Author { get; set; }
        public DateTime CreatedOn { get; set; }
    }
}

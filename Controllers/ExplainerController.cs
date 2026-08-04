using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using CICDTrg.Services;

namespace CICDTrg.Controllers 
{
    public class ExplainerController : Controller
    {
        private readonly JiraSprintService _jiraService;
        private readonly GitService _gitService;
        private readonly DeepSeekExplainerService _aiService;

        private readonly BitbucketService _bitbucketService;
        private readonly IMemoryCache _cache;

        public ExplainerController(JiraSprintService jiraService, GitService gitService, DeepSeekExplainerService aiService, BitbucketService bitbucketService, IMemoryCache cache)
        {
            _jiraService = jiraService;
            _gitService = gitService;
            _aiService = aiService;
            _bitbucketService = bitbucketService;
            _cache = cache;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public IActionResult PRReviewer()
        {
            return View();
        }

        [HttpGet]
        [HttpPost]
        public async Task<IActionResult> ListPRs(string sprintName, string workspace = "frgphoenix", string repoSlug = "phoenix", int page = 1, string status = "ALL", bool refresh = false)
        {
            try
            {
                string cacheKey = $"prs_{workspace}_{repoSlug}_{sprintName}";
                
                if (refresh)
                {
                    _cache.Remove(cacheKey);
                }

                if (!_cache.TryGetValue(cacheKey, out List<PRModel> allPrs))
                {
                    var sprintInfo = await _jiraService.GetSprintDatesAsync(sprintName);
                    DateTime? targetEndDate = sprintInfo.State.Equals("active", StringComparison.OrdinalIgnoreCase) ? null : sprintInfo.EndDate;

                    allPrs = await _bitbucketService.GetPullRequestsAsync(workspace, repoSlug, sprintInfo.StartDate, targetEndDate);
                    
                    var cacheOptions = new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(5));
                    _cache.Set(cacheKey, allPrs, cacheOptions);
                }

                var filteredPrs = allPrs;
                if (!string.IsNullOrEmpty(status) && status != "ALL")
                {
                    filteredPrs = allPrs.Where(pr => pr.State.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                
                int pageSize = 20;
                var pagedPrs = filteredPrs.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                ViewBag.SprintName = sprintName;
                ViewBag.CurrentPage = page;
                ViewBag.TotalPages = (int)Math.Ceiling((double)filteredPrs.Count / pageSize);
                ViewBag.Workspace = workspace;
                ViewBag.RepoSlug = repoSlug;
                ViewBag.CurrentStatus = status;
                
                return View("PRList", pagedPrs);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error fetching PRs: {ex.Message}");
            }
        }

        [HttpGet]
        public async Task<IActionResult> ReviewPR(int prId, string workspace = "frgphoenix", string repoSlug = "phoenix")
        {
            try
            {
                string diff = await _bitbucketService.GetPullRequestDiffAsync(workspace, repoSlug, prId);
                string review = await _aiService.ReviewPRAsync(diff);
                
                ViewBag.PRId = prId;
                ViewBag.Diff = diff;
                return View("PRReviewResult", review);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error reviewing PR: {ex.Message}");
            }
        }

        [HttpPost]
        public async Task<IActionResult> GenerateExplainer(string sprintName, string branchName, string repoPath = @"C:\TRG Development\phoenix")
        {
            try
            {
                // 1. Get Sprint Dates from Jira
                var sprintInfo = await _jiraService.GetSprintDatesAsync(sprintName);

                // Logic: If active -> use start to now. If closed -> use start to end.
                DateTime? targetEndDate = sprintInfo.State.Equals("active", StringComparison.OrdinalIgnoreCase) 
                    ? null 
                    : sprintInfo.EndDate;

                ViewBag.SprintName = sprintName;
                ViewBag.BranchName = branchName;

                // 2. Get Git Diff
                string rawDiff = _gitService.GetDiffByDate(repoPath, branchName, sprintInfo.StartDate, targetEndDate);

                if (string.IsNullOrWhiteSpace(rawDiff))
                {
                    return View("ExplainerResult", "No changes found in Git for this timeframe.");
                }

                // 3. Let DeepSeek explain it
                string englishExplanation = await _aiService.ExplainChangesAsync(rawDiff);

                return View("ExplainerResult", englishExplanation); 
            }
            catch (Exception ex)
            {
                return BadRequest($"Error generating explainer: {ex.Message}");
            }
        }
    }
}

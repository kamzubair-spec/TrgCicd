using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using CICDTrg.Services;

namespace CICDTrg.Controllers 
{
    public class ExplainerController : Controller
    {
        private readonly JiraSprintService _jiraService;
        private readonly GitService _gitService;
        private readonly DeepSeekExplainerService _aiService;

        private readonly BitbucketService _bitbucketService;

        public ExplainerController(JiraSprintService jiraService, GitService gitService, DeepSeekExplainerService aiService, BitbucketService bitbucketService)
        {
            _jiraService = jiraService;
            _gitService = gitService;
            _aiService = aiService;
            _bitbucketService = bitbucketService;
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

        [HttpPost]
        public async Task<IActionResult> ListPRs(string sprintName, string workspace = "frgphoenix", string repoSlug = "phoenix")
        {
            try
            {
                var sprintInfo = await _jiraService.GetSprintDatesAsync(sprintName);
                DateTime? targetEndDate = sprintInfo.State.Equals("active", StringComparison.OrdinalIgnoreCase) ? null : sprintInfo.EndDate;

                var prs = await _bitbucketService.GetPullRequestsAsync(workspace, repoSlug, sprintInfo.StartDate, targetEndDate);
                
                ViewBag.SprintName = sprintName;
                return View("PRList", prs);
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

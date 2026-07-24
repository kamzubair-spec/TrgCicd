using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using CICDTrg.Models;
using CICDTrg.Data;
using Hangfire;
using CICDTrg.Services;

using Microsoft.AspNetCore.Authorization;

namespace CICDTrg.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly AppDbContext _db;

    public HomeController(ILogger<HomeController> logger, AppDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    public IActionResult Index()
    {
        var recentJobs = _db.DeploymentJobs.OrderByDescending(j => j.StartTime).Take(10).ToList();
        ViewBag.Pipelines = _db.OrgConfigurations.ToList();
        return View(recentJobs);
    }

    [HttpPost]
    public async Task<IActionResult> Deploy(int orgConfigurationId)
    {
        var config = await _db.OrgConfigurations.FindAsync(orgConfigurationId);
        
        var job = new DeploymentJob
        {
            OrgConfigurationId = orgConfigurationId,
            OrgConfigurationName = config?.Name ?? "Unknown Pipeline",
            SourceBranch = config?.SourceBranch ?? "main",
            TriggeredBy = User.Identity?.Name ?? "Admin",
            Status = "Queued"
        };
        _db.DeploymentJobs.Add(job);
        await _db.SaveChangesAsync();

        BackgroundJob.Enqueue<SalesforceDeploymentService>(service => service.ExecuteDeploymentJobAsync(job.Id));

        return RedirectToAction("Index");
    }

    public IActionResult PipelineLogs(int id)
    {
        var config = _db.OrgConfigurations.Find(id);
        if (config == null) return NotFound();

        ViewBag.PipelineName = config.Name;
        ViewBag.PipelineId = config.Id;
        
        var jobs = _db.DeploymentJobs
            .Where(j => j.OrgConfigurationId == id)
            .OrderByDescending(j => j.StartTime)
            .ToList();
            
        return View(jobs);
    }

    public IActionResult JobLogs(int id)
    {
        var job = _db.DeploymentJobs.Find(id);
        if (job == null) return NotFound();
        return View(job);
    }

    public IActionResult Privacy()
    {
        return View();
    }



    [HttpPost]
    public async Task<IActionResult> DeleteJobLog(int id)
    {
        var job = await _db.DeploymentJobs.FindAsync(id);
        if (job != null)
        {
            int orgId = job.OrgConfigurationId;
            _db.DeploymentJobs.Remove(job);
            await _db.SaveChangesAsync();
            return RedirectToAction("PipelineLogs", new { id = orgId });
        }
        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> ClearPipelineLogs(int id)
    {
        var jobs = _db.DeploymentJobs.Where(j => j.OrgConfigurationId == id);
        _db.DeploymentJobs.RemoveRange(jobs);
        await _db.SaveChangesAsync();
        return RedirectToAction("PipelineLogs", new { id = id });
    }

    [AllowAnonymous]
    [HttpGet("/clearlogs")]
    public async Task<IActionResult> ClearLogs()
    {
        _db.DeploymentJobs.RemoveRange(_db.DeploymentJobs);
        await _db.SaveChangesAsync();
        return Content("All deployment logs have been completely wiped from the database.");
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

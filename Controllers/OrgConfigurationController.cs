using Microsoft.AspNetCore.Mvc;
using CICDTrg.Models;
using CICDTrg.Data;
using CICDTrg.Services;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;

namespace CICDTrg.Controllers
{
    [Authorize]
    public class OrgConfigurationController : Controller
    {
        private readonly AppDbContext _db;
        private readonly EncryptionService _encryptionService;

        public OrgConfigurationController(AppDbContext db, EncryptionService encryptionService)
        {
            _db = db;
            _encryptionService = encryptionService;
        }

        public IActionResult Index()
        {
            return View(_db.OrgConfigurations.ToList());
        }

        [HttpPost]
        public async Task<IActionResult> Create(OrgConfiguration model)
        {
            // Encrypt passwords before saving
            if (!string.IsNullOrEmpty(model.EncryptedRepoUsername))
                model.EncryptedRepoUsername = _encryptionService.Encrypt(model.EncryptedRepoUsername);
                
            if (!string.IsNullOrEmpty(model.EncryptedRepoAppPassword))
                model.EncryptedRepoAppPassword = _encryptionService.Encrypt(model.EncryptedRepoAppPassword);
                
            if (model.Username == null)
                model.Username = "";

            if (!string.IsNullOrEmpty(model.EncryptedClientSecret))
                model.EncryptedClientSecret = _encryptionService.Encrypt(model.EncryptedClientSecret);

            if (!string.IsNullOrEmpty(model.EncryptedSshKeyContent))
                model.EncryptedSshKeyContent = _encryptionService.Encrypt(model.EncryptedSshKeyContent);

            _db.OrgConfigurations.Add(model);
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var config = await _db.OrgConfigurations.FindAsync(id);
            if (config == null) return NotFound();
            return View(config);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(OrgConfiguration model)
        {
            var config = await _db.OrgConfigurations.FindAsync(model.Id);
            if (config == null) return NotFound();

            config.Name = model.Name;
            config.OrgType = model.OrgType;
            config.InstanceUrl = model.InstanceUrl;
            config.Username = model.Username ?? "";
            config.AuthMethod = model.AuthMethod;
            config.ClientId = model.ClientId;
            
            if (!string.IsNullOrEmpty(model.EncryptedClientSecret))
                config.EncryptedClientSecret = _encryptionService.Encrypt(model.EncryptedClientSecret);
                
            config.RepoUrl = model.RepoUrl;
            config.SourceBranch = model.SourceBranch;
            
            if (!string.IsNullOrEmpty(model.EncryptedRepoUsername))
                config.EncryptedRepoUsername = _encryptionService.Encrypt(model.EncryptedRepoUsername);
                
            if (!string.IsNullOrEmpty(model.EncryptedRepoAppPassword))
                config.EncryptedRepoAppPassword = _encryptionService.Encrypt(model.EncryptedRepoAppPassword);

            if (!string.IsNullOrEmpty(model.EncryptedSshKeyContent))
                config.EncryptedSshKeyContent = _encryptionService.Encrypt(model.EncryptedSshKeyContent);

            config.IsCheckOnly = model.IsCheckOnly;
            config.TestLevel = model.TestLevel;
            config.SpecifiedTestClasses = model.SpecifiedTestClasses;
            config.AutoMergeTargetBranch = model.AutoMergeTargetBranch;

            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var config = await _db.OrgConfigurations.FindAsync(id);
            if (config != null)
            {
                _db.OrgConfigurations.Remove(config);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }
    }
}

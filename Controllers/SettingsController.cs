using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CICDTrg.Models;
using CICDTrg.Data;
using Microsoft.AspNetCore.Identity;
using System.Linq;
using System.Threading.Tasks;

namespace CICDTrg.Controllers
{
    [Authorize(Roles = "Admin")]
    public class SettingsController : Controller
    {
        private readonly AppDbContext _db;

        public SettingsController(AppDbContext db)
        {
            _db = db;
        }

        public IActionResult Index()
        {
            return View(_db.Users.ToList());
        }

        [HttpPost]
        public async Task<IActionResult> CreateUser(string username, string password, string role)
        {
            if (!_db.Users.Any(u => u.Username == username))
            {
                var user = new AppUser { Username = username, Role = role };
                var hasher = new PasswordHasher<AppUser>();
                user.PasswordHash = hasher.HashPassword(user, password);
                
                _db.Users.Add(user);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> DeleteUser(int id)
        {
            var user = await _db.Users.FindAsync(id);
            if (user != null && user.Username != User.Identity?.Name) // Prevent self-deletion
            {
                _db.Users.Remove(user);
                await _db.SaveChangesAsync();
            }
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> ChangePassword(int id)
        {
            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();
            return View(user);
        }

        [HttpPost]
        public async Task<IActionResult> ChangePassword(int id, string newPassword, string confirmPassword)
        {
            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();

            if (newPassword != confirmPassword)
            {
                ViewBag.Error = "New passwords do not match.";
                return View(user);
            }

            var hasher = new PasswordHasher<AppUser>();
            user.PasswordHash = hasher.HashPassword(user, newPassword);
            await _db.SaveChangesAsync();
            
            ViewBag.Success = $"Password for {user.Username} successfully changed.";
            return View(user);
        }
    }
}

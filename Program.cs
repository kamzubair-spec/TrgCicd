using CICDTrg.Data;
using CICDTrg.Services;
using CICDTrg.Models;
using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
    });
builder.Services.AddAuthorization();

// Setup Entity Framework Core (SQLite)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Setup Data Protection for Encrypting Secrets
builder.Services.AddDataProtection();
builder.Services.AddSingleton<EncryptionService>();

// Setup Hangfire (In-Memory for this demo, use SQL Server in production)
builder.Services.AddHangfire(config => config.UseMemoryStorage());
builder.Services.AddHangfireServer();

// Register our custom deployment service
builder.Services.AddScoped<SalesforceDeploymentService>();

var app = builder.Build();

// Create the SQLite DB on startup and seed admin user
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Auto-migrate new columns safely
    try {
        db.Database.ExecuteSqlRaw("ALTER TABLE OrgConfigurations ADD COLUMN EncryptedSshKeyContent TEXT;");
    } catch { }

    if (!db.Users.Any(u => u.Username == "kamran"))
    {
        var hasher = new PasswordHasher<AppUser>();
        var admin = new AppUser { Username = "kamran", Role = "Admin" };
        admin.PasswordHash = hasher.HashPassword(admin, "kami");
        db.Users.Add(admin);
        db.SaveChanges();
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

// Map Hangfire Dashboard
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    // Need custom authorization filter for hangfire in prod
    Authorization = new[] { new Hangfire.Dashboard.LocalRequestsOnlyAuthorizationFilter() } 
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

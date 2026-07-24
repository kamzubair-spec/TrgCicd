using Microsoft.EntityFrameworkCore;
using CICDTrg.Models;

namespace CICDTrg.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<DeploymentJob> DeploymentJobs { get; set; }
        public DbSet<OrgConfiguration> OrgConfigurations { get; set; }
        public DbSet<AppUser> Users { get; set; }
    }
}

using System;

namespace CICDTrg.Models
{
    public class DeploymentJob
    {
        public int Id { get; set; }
        
        public int OrgConfigurationId { get; set; }
        public string OrgConfigurationName { get; set; } = string.Empty;
        
        public string SourceBranch { get; set; } = string.Empty;
        
        public string TriggeredBy { get; set; } = string.Empty;
        public string Status { get; set; } = "Queued"; // Queued, Running, Success, Failed
        public string? LogOutput { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
    }
}

namespace CICDTrg.Models
{
    public class OrgConfiguration
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty; // e.g., "UAT Sandbox Pipeline"
        
        // 1. Target Org Configuration
        public string OrgType { get; set; } = "Sandbox"; 
        public string InstanceUrl { get; set; } = string.Empty;
        public string? Username { get; set; }
        
        // Auth Config (JWT or ClientCredentials)
        public string AuthMethod { get; set; } = "ClientCredentials"; 
        public string ClientId { get; set; } = string.Empty; // Consumer Key
        public string? EncryptedClientSecret { get; set; } // Consumer Secret for ClientCredentials
        public string? JwtKeyFilePath { get; set; } // For JWT
        
        // 2. Source Control Configuration
        public string RepoUrl { get; set; } = string.Empty;
        public string SourceBranch { get; set; } = string.Empty; // Added branch
        public string? EncryptedRepoUsername { get; set; }
        public string? EncryptedRepoAppPassword { get; set; }
        
        // 3. Post-Deployment Action
        public string? AutoMergeTargetBranch { get; set; } 
        public string? EncryptedSshKeyContent { get; set; }
        
        // 4. Deployment Settings
        public bool IsCheckOnly { get; set; } = false;
        public string TestLevel { get; set; } = "NoTestRun"; // NoTestRun, RunSpecifiedTests, RunLocalTests, RunAllTestsInOrg
        public string? SpecifiedTestClasses { get; set; } 
    }
}

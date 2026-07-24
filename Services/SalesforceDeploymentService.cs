using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CICDTrg.Data;

namespace CICDTrg.Services
{
    public class SalesforceDeploymentService
    {
        private readonly ILogger<SalesforceDeploymentService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly EncryptionService _encryptionService;

        public SalesforceDeploymentService(ILogger<SalesforceDeploymentService> logger, IServiceScopeFactory scopeFactory, EncryptionService encryptionService)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _encryptionService = encryptionService;
        }

        public async Task ExecuteDeploymentJobAsync(int jobId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await db.DeploymentJobs.FindAsync(jobId);
            if (job == null) return;

            job.Status = "Running";
            job.LogOutput = "Starting deployment process...\n";
            await db.SaveChangesAsync();

            string workspacePath = null;
            try
            {
                var config = await db.OrgConfigurations.FindAsync(job.OrgConfigurationId);

                if (config == null)
                {
                    throw new Exception("Pipeline Configuration not found in DB.");
                }

                // 1. Setup workspace
                workspacePath = Path.Combine(Path.GetTempPath(), $"DeployHub_{jobId}");
                if (Directory.Exists(workspacePath)) Directory.Delete(workspacePath, true);
                Directory.CreateDirectory(workspacePath);

                // 2. Clone Repository
                await AppendLog(db, job, $"Trying to connect to Bitbucket (Branch: {job.SourceBranch})...");
                
                string authUrl = config.RepoUrl;
                if (!string.IsNullOrEmpty(config.EncryptedRepoUsername) && !string.IsNullOrEmpty(config.EncryptedRepoAppPassword))
                {
                    string repoUser = Uri.EscapeDataString(_encryptionService.Decrypt(config.EncryptedRepoUsername));
                    string repoPass = Uri.EscapeDataString(_encryptionService.Decrypt(config.EncryptedRepoAppPassword));
                    authUrl = authUrl.Replace("https://", $"https://{repoUser}:{repoPass}@");
                }

                string tempSshKeyFile = null;
                if (!string.IsNullOrEmpty(config.EncryptedSshKeyContent))
                {
                    string decryptedKey = _encryptionService.Decrypt(config.EncryptedSshKeyContent);
                    decryptedKey = decryptedKey.Replace("\r\n", "\n").Trim() + "\n";
                    
                    tempSshKeyFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".pem");
                    File.WriteAllText(tempSshKeyFile, decryptedKey);

                    string currentUser = Environment.UserName;
                    var aclProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = "icacls",
                        Arguments = $"\"{tempSshKeyFile}\" /inheritance:r /grant:r \"{currentUser}:(R)\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    aclProcess?.WaitForExit();
                }

                // Verify Connection first
                int checkConnectionExitCode;
                string sshCommand = "";
                if (!string.IsNullOrEmpty(tempSshKeyFile))
                {
                    string sshKeyCmd = tempSshKeyFile.Replace("\\", "/");
                    sshCommand = $"ssh -i {sshKeyCmd} -o StrictHostKeyChecking=no";
                    checkConnectionExitCode = await RunProcessAsync("git", $"-c core.sshCommand=\"{sshCommand}\" ls-remote \"{authUrl}\"", workspacePath, db, job, config);
                }
                else
                {
                    checkConnectionExitCode = await RunProcessAsync("git", $"ls-remote \"{authUrl}\"", workspacePath, db, job, config);
                }
                
                if (checkConnectionExitCode != 0) throw new Exception("Failed to connect to Bitbucket repository.");
                
                await AppendLog(db, job, "Successfully connected to Bitbucket! Retrieving files...");

                int cloneExitCode;
                if (!string.IsNullOrEmpty(tempSshKeyFile))
                {
                    cloneExitCode = await RunProcessAsync("git", $"clone -c core.sshCommand=\"{sshCommand}\" -b \"{job.SourceBranch}\" \"{authUrl}\" .", workspacePath, db, job, config);
                }
                else
                {
                    cloneExitCode = await RunProcessAsync("git", $"clone -b \"{job.SourceBranch}\" \"{authUrl}\" .", workspacePath, db, job, config);
                }
                if (cloneExitCode != 0) throw new Exception("Git clone failed.");

                await AppendLog(db, job, "Deployment process being started. Connecting to Salesforce...\n");

                // 3. Authenticate to Salesforce
                string authCommand = "";
                if (config.AuthMethod == "ClientCredentials")
                {
                    string clientSecret = _encryptionService.Decrypt(config.EncryptedClientSecret ?? "");
                    using var http = new HttpClient();
                    var content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string, string>("grant_type", "client_credentials"),
                        new KeyValuePair<string, string>("client_id", config.ClientId),
                        new KeyValuePair<string, string>("client_secret", clientSecret)
                    });
                    var response = await http.PostAsync($"{config.InstanceUrl}/services/oauth2/token", content);
                    string responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new Exception($"Failed to authenticate via REST API: {responseBody}");
                    using var doc = JsonDocument.Parse(responseBody);
                    string accessToken = doc.RootElement.GetProperty("access_token").GetString();
                    Environment.SetEnvironmentVariable("SF_ACCESS_TOKEN", accessToken);
                    authCommand = $"org login access-token --instance-url {config.InstanceUrl} --set-default --no-prompt";
                }
                else
                {
                    if (string.IsNullOrEmpty(config.Username)) throw new Exception("Username is required for JWT flow.");
                    authCommand = $"org login jwt --client-id {config.ClientId} --jwt-key-file \"{config.JwtKeyFilePath}\" --username {config.Username} --instance-url {config.InstanceUrl} --set-default";
                }
                
                int authExitCode = await RunProcessAsync("sf.cmd", authCommand, workspacePath, db, job, config);
                Environment.SetEnvironmentVariable("SF_ACCESS_TOKEN", null);

                if (authExitCode != 0) throw new Exception("Salesforce authentication failed.");

                // 4. Deploy code
                await AppendLog(db, job, "\nStarting live Salesforce deployment...");
                
                string deployCommand = "project deploy start --source-dir force-app --wait 30";
                if (!string.IsNullOrEmpty(config.Username)) deployCommand += $" --target-org {config.Username}";
                if (config.IsCheckOnly) deployCommand += " --dry-run";
                if (config.TestLevel != "NoTestRun")
                {
                    deployCommand += $" --test-level {config.TestLevel}";
                    if (config.TestLevel == "RunSpecifiedTests" && !string.IsNullOrEmpty(config.SpecifiedTestClasses))
                    {
                        deployCommand += $" --tests {string.Join(" ", config.SpecifiedTestClasses.Split(','))}";
                    }
                }

                int deployExitCode = await RunProcessAsync("sf.cmd", deployCommand, workspacePath, db, job);

                if (deployExitCode == 0)
                {
                    job.Status = "Success";
                    await AppendLog(db, job, "\nDeployment finished successfully!");
                    
                    if (!string.IsNullOrEmpty(config.AutoMergeTargetBranch))
                    {
                        await AppendLog(db, job, $"\nAuto-merging branch '{job.SourceBranch}' into '{config.AutoMergeTargetBranch}'...");
                        await RunProcessAsync("git", "config user.email \"deployhub@cicd.local\"", workspacePath, db, job);
                        await RunProcessAsync("git", "config user.name \"DeployHub Automation\"", workspacePath, db, job);
                        await RunProcessAsync("git", $"checkout {config.AutoMergeTargetBranch}", workspacePath, db, job, config);
                        await RunProcessAsync("git", $"merge {job.SourceBranch}", workspacePath, db, job, config);
                        
                        int pushExitCode = await RunProcessAsync("git", "push origin", workspacePath, db, job, config);
                        if (pushExitCode == 0) await AppendLog(db, job, "Auto-merge pushed successfully.");
                        else await AppendLog(db, job, "Auto-merge push failed.");
                    }
                }
                else
                {
                    job.Status = "Failed";
                    await AppendLog(db, job, $"\nDeployment failed with exit code {deployExitCode}.");
                }
            }
            catch (Exception ex)
            {
                job.Status = "Failed";
                await AppendLog(db, job, $"\nException during deployment: {ex.Message}");
                _logger.LogError(ex, "Deployment failed.");
            }
            finally
            {
                job.EndTime = DateTime.UtcNow;
                await db.SaveChangesAsync();

                if (!string.IsNullOrEmpty(workspacePath) && Directory.Exists(workspacePath))
                {
                    try 
                    {
                        var rmProcess = Process.Start(new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c rmdir /s /q \"{workspacePath}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        rmProcess?.WaitForExit();
                    } 
                    catch (Exception ex) 
                    {
                        _logger.LogWarning($"Failed to cleanup workspace {workspacePath}: {ex.Message}");
                    }
                }
            }
        }

        private async Task<int> RunProcessAsync(string fileName, string arguments, string workingDirectory, AppDbContext db, CICDTrg.Models.DeploymentJob job, CICDTrg.Models.OrgConfiguration config = null)
        {
            if (fileName == "sf.cmd" || fileName == "sf")
            {
                fileName = "cmd.exe";
                arguments = $"/c sf {arguments}";
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            string tempSshKeyFile = null;
            if (config != null && !string.IsNullOrEmpty(config.EncryptedSshKeyContent))
            {
                string decryptedKey = _encryptionService.Decrypt(config.EncryptedSshKeyContent);
                decryptedKey = decryptedKey.Replace("\r\n", "\n").Trim() + "\n";
                tempSshKeyFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".pem");
                File.WriteAllText(tempSshKeyFile, decryptedKey);
                
                string sshKeyCmd = tempSshKeyFile.Replace("\\", "/");
                processInfo.EnvironmentVariables["GIT_SSH_COMMAND"] = $"ssh -i \"{sshKeyCmd}\" -o StrictHostKeyChecking=no";
            }

            using var process = new Process { StartInfo = processInfo };

            process.OutputDataReceived += async (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    // Ignore git ls-remote output to keep logs clean
                    if (fileName == "git" && arguments.Contains("ls-remote")) return;
                    await AppendLog(db, job, SanitizeLog(args.Data));
                }
            };

            process.ErrorDataReceived += async (sender, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    // Git uses stderr for normal progress outputs, don't label it as ERROR
                    string prefix = (fileName == "git" || fileName == "git.exe") ? "" : "ERROR: ";
                    await AppendLog(db, job, prefix + SanitizeLog(args.Data));
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();
            return process.ExitCode;
        }

        private string SanitizeLog(string rawData)
        {
            string safeData = rawData;
            // Mask git passwords
            if (safeData.Contains("https://") && safeData.Contains("@"))
            {
                safeData = System.Text.RegularExpressions.Regex.Replace(safeData, @"https://.*:.*@", "https://***:***@");
            }
            // Mask Salesforce Client Secrets in CLI commands
            safeData = System.Text.RegularExpressions.Regex.Replace(safeData, @"--client-secret\s+""[^""]+""", "--client-secret \"***\"");
            return safeData;
        }

        private async Task AppendLog(AppDbContext db, CICDTrg.Models.DeploymentJob job, string message)
        {
            job.LogOutput += message + "\n";
            db.Update(job);
            await db.SaveChangesAsync();
        }
    }
}

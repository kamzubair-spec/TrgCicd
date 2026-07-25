using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CICDTrg.Data;

namespace CICDTrg.Services
{
    public class SalesforceDeploymentService
    {
        private class DeploymentState
        {
            public StepState Step1 { get; set; } = new StepState { Title = "STEP 1: Retrieving files to be deployed" };
            public StepState Step2 { get; set; } = new StepState { Title = "STEP 2: Deploying to Salesforce" };
            public StepState Step3 { get; set; } = new StepState { Title = "STEP 3: Updating environmental branch" };
            public bool Finished { get; set; }
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public List<string> DebugLogs { get; set; } = new List<string>();
        }

        private class StepState
        {
            public string Title { get; set; }
            public string Status { get; set; } = "PENDING"; // PENDING, RUNNING, DONE, FAILED
            public int ProgressPercent { get; set; } = 0;
            public string DetailText { get; set; } = "";
        }

        private readonly ILogger<SalesforceDeploymentService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly EncryptionService _encryptionService;
        
        private DeploymentState _state = new DeploymentState();
        private DateTime _lastDbUpdate = DateTime.MinValue;
        private SemaphoreSlim _dbSemaphore = new SemaphoreSlim(1, 1);

        public SalesforceDeploymentService(ILogger<SalesforceDeploymentService> logger, IServiceScopeFactory scopeFactory, EncryptionService encryptionService)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _encryptionService = encryptionService;
        }

        private async Task FlushStateAsync(AppDbContext db, CICDTrg.Models.DeploymentJob job, bool force = false)
        {
            await _dbSemaphore.WaitAsync();
            try 
            {
                if (force || (DateTime.UtcNow - _lastDbUpdate).TotalMilliseconds > 500)
                {
                    job.LogOutput = JsonSerializer.Serialize(_state);
                    db.Update(job);
                    await db.SaveChangesAsync();
                    _lastDbUpdate = DateTime.UtcNow;
                }
            }
            finally 
            {
                _dbSemaphore.Release();
            }
        }

        public async Task ExecuteDeploymentJobAsync(int jobId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await db.DeploymentJobs.FindAsync(jobId);
            if (job == null) return;

            job.Status = "Running";
            _state.Step1.Status = "RUNNING";
            await FlushStateAsync(db, job, true);
            
            string workspacePath = null;
            try
            {
                var config = await db.OrgConfigurations.FindAsync(job.OrgConfigurationId);

                if (config == null) throw new Exception("Pipeline Configuration not found in DB.");

                workspacePath = Path.Combine(Path.GetTempPath(), $"DeployHub_{jobId}");
                if (Directory.Exists(workspacePath)) Directory.Delete(workspacePath, true);
                Directory.CreateDirectory(workspacePath);

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

                string sshCommand = "";
                int checkConnectionExitCode;
                
                _state.Step1.DetailText = $"Connecting to Bitbucket (Branch: {job.SourceBranch})";
                await FlushStateAsync(db, job);
                
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
                
                _state.Step1.DetailText = "Cloning repository...";
                await FlushStateAsync(db, job);

                int cloneExitCode;
                if (!string.IsNullOrEmpty(tempSshKeyFile))
                {
                    cloneExitCode = await RunProcessAsync("git", $"clone --progress -c core.sshCommand=\"{sshCommand}\" -b \"{job.SourceBranch}\" \"{authUrl}\" .", workspacePath, db, job, config);
                }
                else
                {
                    cloneExitCode = await RunProcessAsync("git", $"clone --progress -b \"{job.SourceBranch}\" \"{authUrl}\" .", workspacePath, db, job, config);
                }
                
                if (cloneExitCode != 0) throw new Exception("Git clone failed.");

                _state.Step1.Status = "DONE";
                _state.Step1.ProgressPercent = 100;
                _state.Step1.DetailText = "Clone complete";
                _state.Step2.Status = "RUNNING";
                await FlushStateAsync(db, job, true);

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
                    if (!response.IsSuccessStatusCode) throw new Exception($"Failed to authenticate via REST API: {responseBody}");
                    using var doc = JsonDocument.Parse(responseBody);
                    string accessToken = doc.RootElement.GetProperty("access_token").GetString();
                    Environment.SetEnvironmentVariable("SF_ACCESS_TOKEN", accessToken);
                    authCommand = $"org login access-token --instance-url {config.InstanceUrl} --set-default --no-prompt";
                }
                else
                {
                    authCommand = $"org login jwt --client-id {config.ClientId} --jwt-key-file \"{config.JwtKeyFilePath}\" --username {config.Username} --instance-url {config.InstanceUrl} --set-default";
                }
                
                _state.Step2.DetailText = "Authenticating with Salesforce...";
                await FlushStateAsync(db, job);
                
                int authExitCode = await RunProcessAsync("sf.cmd", authCommand, workspacePath, db, job, config);
                Environment.SetEnvironmentVariable("SF_ACCESS_TOKEN", null);

                if (authExitCode != 0) throw new Exception("Salesforce authentication failed.");

                string deployCommand = "project deploy start --source-dir force-app --async --json";
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

                _state.Step2.DetailText = "Submitting deployment...";
                await FlushStateAsync(db, job);

                var (startExitCode, startOutput) = await RunCommandCaptureOutputAsync("sf.cmd", deployCommand, workspacePath, config);
                string deployJobId = "";
                try {
                    int jsonStartIdx = startOutput.IndexOf("{"); if(jsonStartIdx >= 0) startOutput = startOutput.Substring(jsonStartIdx); using var startDoc = JsonDocument.Parse(startOutput);
                    deployJobId = startDoc.RootElement.GetProperty("result").GetProperty("id").GetString();
                } catch {
                    throw new Exception($"Failed to start deployment: {startOutput}");
                }

                _state.Step2.DetailText = "Deployment started (Waiting for server...)";
                await FlushStateAsync(db, job);

                int deployExitCode = 0;
                string failMsg = "";
                while (true)
                {
                    await Task.Delay(5000);
                    var (repExitCode, repOutput) = await RunCommandCaptureOutputAsync("sf.cmd", $"project deploy report --job-id {deployJobId} --json", workspacePath, config);
                    
                    try {
                        int repJsonIdx = repOutput.IndexOf("{"); if(repJsonIdx >= 0) repOutput = repOutput.Substring(repJsonIdx); 
                        
                        // Dump the live JSON to a file for inspection
                        System.IO.File.WriteAllText("C:\\Users\\s.zubair\\TrgCicd\\latest_polling_payload.json", repOutput);

                        using var repDoc = JsonDocument.Parse(repOutput);
                        var resultObj = repDoc.RootElement.GetProperty("result");
                        string status = resultObj.GetProperty("status").GetString();
                        
                        int deployed = resultObj.GetProperty("numberComponentsDeployed").GetInt32();
                        int total = resultObj.GetProperty("numberComponentsTotal").GetInt32();
                        
                        string currentFile = "";
                        if (resultObj.TryGetProperty("details", out var detailsObj) && detailsObj.TryGetProperty("componentSuccesses", out var successes) && successes.GetArrayLength() > 0)
                        {
                            var lastSuccess = successes[successes.GetArrayLength() - 1];
                            currentFile = lastSuccess.GetProperty("fullName").GetString();
                        }
                        
                        if (total > 0 && deployed <= total) {
                            int pct = (deployed * 100) / total;
                            _state.Step2.ProgressPercent = pct;
                            string pctStr = $"Components: {deployed}/{total} ({pct}%)";
                            if (!string.IsNullOrEmpty(currentFile) && currentFile != "package.xml") _state.Step2.DetailText = $"{pctStr} - {currentFile}";
                            else _state.Step2.DetailText = pctStr;
                        }

                        await FlushStateAsync(db, job);

                        if (status == "Succeeded" || status == "SucceededPartial")
                        {
                            deployExitCode = 0;
                            break;
                        }
                        else if (status == "Failed" || status == "Canceled")
                        {
                            deployExitCode = 1;
                            List<string> errors = new List<string>();
                            if (resultObj.TryGetProperty("details", out var errDetails) && errDetails.TryGetProperty("componentFailures", out var failures))
                            {
                                if (failures.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var failure in failures.EnumerateArray())
                                    {
                                        string fn = failure.TryGetProperty("fullName", out var fnProp) && fnProp.ValueKind != JsonValueKind.Null ? fnProp.GetString() : "Unknown";
                                        string ft = failure.TryGetProperty("componentType", out var ftProp) && ftProp.ValueKind != JsonValueKind.Null ? ftProp.GetString() : "Unknown";
                                        string err = failure.TryGetProperty("problem", out var pProp) && pProp.ValueKind != JsonValueKind.Null ? pProp.GetString() : "Unknown";
                                        errors.Add($"{ft} ({fn}): {err}");
                                    }
                                }
                                else if (failures.ValueKind == JsonValueKind.Object)
                                {
                                    string fn = failures.TryGetProperty("fullName", out var fnProp) && fnProp.ValueKind != JsonValueKind.Null ? fnProp.GetString() : "Unknown";
                                    string ft = failures.TryGetProperty("componentType", out var ftProp) && ftProp.ValueKind != JsonValueKind.Null ? ftProp.GetString() : "Unknown";
                                    string err = failures.TryGetProperty("problem", out var pProp) && pProp.ValueKind != JsonValueKind.Null ? pProp.GetString() : "Unknown";
                                    errors.Add($"{ft} ({fn}): {err}");
                                }
                            }
                            if (errors.Count > 0) {
                                failMsg = "\nFailed Components:\n" + string.Join("\n", errors);
                            }
                            break;
                        }
                    } catch (Exception ex) {
                        _logger.LogWarning($"Parse error during polling: {ex.Message}");
                    }
                }

                if (deployExitCode == 0)
                {
                    _state.Step2.Status = "DONE";
                    _state.Step2.ProgressPercent = 100;
                    _state.Step2.DetailText = "Deployment successful";
                    
                    if (!string.IsNullOrEmpty(config.AutoMergeTargetBranch))
                    {
                        _state.Step3.Status = "RUNNING";
                        _state.Step3.DetailText = $"Merging into {config.AutoMergeTargetBranch}";
                        await FlushStateAsync(db, job, true);

                        await RunProcessAsync("git", "config user.email \"deployhub@cicd.local\"", workspacePath, db, job, config);
                        await RunProcessAsync("git", "config user.name \"DeployHub Automation\"", workspacePath, db, job, config);
                        await RunProcessAsync("git", $"checkout {config.AutoMergeTargetBranch}", workspacePath, db, job, config);
                        await RunProcessAsync("git", $"merge {job.SourceBranch}", workspacePath, db, job, config);
                        
                        await RunProcessAsync("git", "push origin", workspacePath, db, job, config);
                        
                        _state.Step3.Status = "DONE";
                        _state.Step3.ProgressPercent = 100;
                        _state.Step3.DetailText = "Merge complete";
                    }
                    
                    job.Status = "Success";
                    _state.Success = true;
                    _state.Finished = true;
                }
                else
                {
                    throw new Exception($"Deployment failed with exit code 1.{failMsg}");
                }
            }
            catch (Exception ex)
            {
                job.Status = "Failed";
                _state.Success = false;
                _state.Finished = true;
                _state.ErrorMessage = ex.Message;
                
                if (_state.Step1.Status == "RUNNING") _state.Step1.Status = "FAILED";
                if (_state.Step2.Status == "RUNNING") _state.Step2.Status = "FAILED";
                if (_state.Step3.Status == "RUNNING") _state.Step3.Status = "FAILED";
                
                _logger.LogError(ex, "Deployment failed.");
            }
            finally
            {
                job.EndTime = DateTime.UtcNow;
                
                await FlushStateAsync(db, job, true);

                if (!string.IsNullOrEmpty(workspacePath) && Directory.Exists(workspacePath))
                {
                    try { 
                        var rmProcess = Process.Start(new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c rmdir /s /q \"{workspacePath}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        rmProcess?.WaitForExit();
                    } catch { }
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

            processInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

            if (config != null && !string.IsNullOrEmpty(config.EncryptedSshKeyContent))
            {
                string decryptedKey = _encryptionService.Decrypt(config.EncryptedSshKeyContent);
                string tempSshKeyFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".pem");
                File.WriteAllText(tempSshKeyFile, decryptedKey.Replace("\r\n", "\n").Trim() + "\n");
                processInfo.EnvironmentVariables["GIT_SSH_COMMAND"] = $"ssh -i \"{tempSshKeyFile.Replace("\\", "/")}\" -o StrictHostKeyChecking=no";
            }

            using var process = new Process { StartInfo = processInfo };

            DataReceivedEventHandler handler = async (sender, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;
                
                var data = args.Data;

                lock (_state) {
                    _state.DebugLogs.Add(data);
                    if (_state.DebugLogs.Count > 50) _state.DebugLogs.RemoveAt(0);
                }

                if (_state.Step1.Status == "RUNNING") {
                    var gitMatch = System.Text.RegularExpressions.Regex.Match(data, @"Receiving objects:\s+(\d+)%\s+\((.*?)\)");
                    if (gitMatch.Success) {
                        _state.Step1.ProgressPercent = int.Parse(gitMatch.Groups[1].Value);
                        _state.Step1.DetailText = $"Cloning Files: {gitMatch.Groups[1].Value}% ({gitMatch.Groups[2].Value})";
                    }
                } else if (_state.Step2.Status == "RUNNING") {
                    var sfMatch = System.Text.RegularExpressions.Regex.Match(data, @"(\d+)/(\d+)");
                    if (sfMatch.Success) {
                        int done = int.Parse(sfMatch.Groups[1].Value);
                        int total = int.Parse(sfMatch.Groups[2].Value);
                        if (total > 0 && done <= total) {
                            int pct = (done * 100) / total;
                            _state.Step2.ProgressPercent = pct;
                            _state.Step2.DetailText = $"Components: {done}/{total} ({pct}%)";
                        }
                    }
                }

                await FlushStateAsync(db, job);
            };

            process.OutputDataReceived += handler;
            process.ErrorDataReceived += handler;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();
            return process.ExitCode;
        }

        private async Task<(int ExitCode, string Output)> RunCommandCaptureOutputAsync(string fileName, string arguments, string workingDirectory, CICDTrg.Models.OrgConfiguration config = null)
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
            if (config != null && !string.IsNullOrEmpty(config.EncryptedSshKeyContent))
            {
                string decryptedKey = _encryptionService.Decrypt(config.EncryptedSshKeyContent);
                string tempSshKeyFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".pem");
                File.WriteAllText(tempSshKeyFile, decryptedKey.Replace("\r\n", "\n").Trim() + "\n");
                processInfo.EnvironmentVariables["GIT_SSH_COMMAND"] = $"ssh -i \"{tempSshKeyFile.Replace("\\", "/")}\" -o StrictHostKeyChecking=no";
            }
            using var process = new Process { StartInfo = processInfo };
            process.Start();
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outTask;
            if (string.IsNullOrWhiteSpace(output)) output = await errTask;
            return (process.ExitCode, output);
        }
    }
}

using System;
using System.Diagnostics;

namespace CICDTrg.Services
{
    public class GitService
    {
        public string GetDiffByDate(string repoPath, string branchName, DateTime startDate, DateTime? endDate)
        {
            // Format dates for Git (e.g., "2026-07-02 08:51:00")
            string since = startDate.ToString("yyyy-MM-dd HH:mm:ss");
            string cmdArgs = $"log -p --since=\"{since}\" {branchName}";

            // If the sprint is closed, restrict it to the end date
            if (endDate.HasValue)
            {
                string until = endDate.Value.ToString("yyyy-MM-dd HH:mm:ss");
                cmdArgs = $"log -p --since=\"{since}\" --until=\"{until}\" {branchName}";
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = cmdArgs,
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return output;
        }
        public string GetFullBranchDiff(string repoPath, string branchName)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"diff origin/develop...origin/{branchName}",
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return output;
        }

        public System.Collections.Generic.List<BranchModel> GetBranchesCreatedInSprint(string repoPath, DateTime startDate, DateTime? endDate)
        {
            var branches = new System.Collections.Generic.List<BranchModel>();
            
            var p1 = Process.Start(new ProcessStartInfo {
                FileName = "git",
                Arguments = "for-each-ref --format=\"%(refname:short)|%(authorname)\" refs/remotes/origin/",
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            });
            var allBranchesLines = p1.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            p1.WaitForExit();

            var lockObj = new object();

            System.Threading.Tasks.Parallel.ForEach(allBranchesLines, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 10 }, line => {
                var parts = line.Split('|');
                if (parts.Length < 2) return;
                var rawName = parts[0].Trim();
                var branchName = rawName.Replace("origin/", "");
                
                if (branchName.Equals("master", StringComparison.OrdinalIgnoreCase) || 
                    branchName.Equals("main", StringComparison.OrdinalIgnoreCase) || 
                    branchName.Equals("develop", StringComparison.OrdinalIgnoreCase) || 
                    rawName == "origin/HEAD" ||
                    branchName.StartsWith("deploy", StringComparison.OrdinalIgnoreCase) ||
                    branchName.StartsWith("release", StringComparison.OrdinalIgnoreCase) ||
                    branchName.StartsWith("env", StringComparison.OrdinalIgnoreCase) ||
                    branchName.StartsWith("environment", StringComparison.OrdinalIgnoreCase) ||
                    branchName.StartsWith("uat", StringComparison.OrdinalIgnoreCase) ||
                    branchName.StartsWith("qa", StringComparison.OrdinalIgnoreCase)) 
                {
                    return;
                }

                var author = parts[1].Trim();

                var p2 = Process.Start(new ProcessStartInfo {
                    FileName = "git",
                    Arguments = $"log {rawName} --format=\"%cI\" -1",
                    WorkingDirectory = repoPath,
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                });
                var dateStr = p2.StandardOutput.ReadToEnd().Trim();
                p2.WaitForExit();

                if (DateTime.TryParse(dateStr, out DateTime createdDate))
                {
                    if (createdDate >= startDate && (!endDate.HasValue || createdDate <= endDate.Value))
                    {
                        lock(lockObj)
                        {
                            branches.Add(new BranchModel { Name = branchName, TargetDate = createdDate, Author = author });
                        }
                    }
                }
            });

            return System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderByDescending(branches, b => b.TargetDate));
        }
    }
}

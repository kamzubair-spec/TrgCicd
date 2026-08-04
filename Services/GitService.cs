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
    }
}

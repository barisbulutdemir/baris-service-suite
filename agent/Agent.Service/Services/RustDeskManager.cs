using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace Agent.Service.Services
{
    public class RustDeskManager
    {
        private readonly ILogger<RustDeskManager> _logger;
        private readonly string _siteId;
        private string _rustDeskPath = "";
        private string _cachedId = "";
        private string _cachedPassword = "";

        public RustDeskManager(ILogger<RustDeskManager> logger, IConfiguration configuration)
        {
            _logger = logger;
            _siteId = configuration["Orchestrator:SiteId"] ?? "unknown-site";
            InitializePaths();
        }

        private void InitializePaths()
        {
            // Default installation paths for RustDesk
            var paths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RustDesk", "rustdesk.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rustdesk.exe"),
                "rustdesk.exe" // If in PATH
            };

            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    _rustDeskPath = path;
                    _logger.LogInformation($"RustDesk executable found at: {_rustDeskPath}");
                    break;
                }
            }

            if (string.IsNullOrEmpty(_rustDeskPath))
            {
                // Fallback to path check
                _rustDeskPath = "rustdesk.exe";
                _logger.LogWarning("RustDesk executable not found in standard paths. Fallback to 'rustdesk.exe' (assumes it is in PATH).");
            }
        }

        public string GetRustDeskId()
        {
            if (!string.IsNullOrEmpty(_cachedId) && _cachedId != "N/A") return _cachedId;

            // Method 1: Try running via cmd.exe piping to more (solves GUI stdout redirect issue)
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{_rustDeskPath}\" --get-id | more\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        if (process.WaitForExit(3000))
                        {
                            string output = process.StandardOutput.ReadToEnd().Trim();
                            if (!string.IsNullOrEmpty(output) && !output.Contains("Error") && output.All(char.IsDigit) && output.Length >= 6)
                            {
                                _cachedId = output;
                                _logger.LogInformation($"Retrieved RustDesk ID via cmd pipe: {_cachedId}");
                                return _cachedId;
                            }
                        }
                        else
                        {
                            _logger.LogWarning("RustDesk ID retrieval via cmd pipe timed out.");
                            try { process.Kill(); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to get RustDesk ID via cmd pipe: {ex.Message}");
            }

            // Method 2: Fallback to scanning log files for ID pattern
            try
            {
                var logPaths = new List<string>();
                
                // 1. Current user's appdata
                string userAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(userAppData))
                {
                    logPaths.Add(Path.Combine(userAppData, "RustDesk", "log"));
                }

                // 2. All users' appdata in C:\Users
                try
                {
                    string usersDir = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users";
                    if (Directory.Exists(usersDir))
                    {
                        foreach (var dir in Directory.GetDirectories(usersDir))
                        {
                            logPaths.Add(Path.Combine(dir, "AppData", "Roaming", "RustDesk", "log"));
                        }
                    }
                }
                catch { }

                // 3. System service profile appdata
                logPaths.Add(@"C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\log");
                logPaths.Add(@"C:\Windows\System32\config\systemprofile\AppData\Roaming\RustDesk\log");

                foreach (var logDir in logPaths)
                {
                    if (!Directory.Exists(logDir)) continue;

                    // Find all log files (.log) in the log directory and subdirectories (like flutter_ffi)
                    var logFiles = Directory.GetFiles(logDir, "*.log", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTime)
                        .ToList();

                    foreach (var fileInfo in logFiles)
                    {
                        // Open file sharing-friendly to avoid lock issues
                        using (var fs = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs))
                        {
                            string? line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                // Check for pattern: "Session [ID] start"
                                int sessionStartIdx = line.IndexOf("Session ");
                                if (sessionStartIdx >= 0)
                                {
                                    int startIdx = sessionStartIdx + "Session ".Length;
                                    int endIdx = line.IndexOf(" start", startIdx);
                                    if (endIdx > startIdx)
                                    {
                                        string possibleId = line.Substring(startIdx, endIdx - startIdx).Trim();
                                        if (!string.IsNullOrEmpty(possibleId) && possibleId.All(char.IsDigit) && possibleId.Length >= 6)
                                        {
                                            _cachedId = possibleId;
                                            _logger.LogInformation($"Found RustDesk ID '{_cachedId}' in log file: {fileInfo.FullName}");
                                            return _cachedId;
                                        }
                                    }
                                }

                                // Check for pattern: "id updated from [old] to [new]"
                                int updatedFromIdx = line.IndexOf("id updated from ");
                                if (updatedFromIdx >= 0)
                                {
                                    int toIdx = line.IndexOf(" to ", updatedFromIdx);
                                    if (toIdx > updatedFromIdx)
                                    {
                                        string possibleId = line.Substring(toIdx + 4).Trim();
                                        // Remove any trailing log content
                                        int spaceIdx = possibleId.IndexOf(' ');
                                        if (spaceIdx > 0) possibleId = possibleId.Substring(0, spaceIdx);
                                        
                                        if (!string.IsNullOrEmpty(possibleId) && possibleId.All(char.IsDigit) && possibleId.Length >= 6)
                                        {
                                            _cachedId = possibleId;
                                            _logger.LogInformation($"Found RustDesk ID '{_cachedId}' from update line in log file: {fileInfo.FullName}");
                                            return _cachedId;
                                        }
                                    }
                                }

                                // Check for pattern: "id: [ID]"
                                int idColonIdx = line.IndexOf("id: ");
                                if (idColonIdx >= 0)
                                {
                                    string possibleId = line.Substring(idColonIdx + 4).Trim();
                                    int spaceIdx = possibleId.IndexOf(' ');
                                    if (spaceIdx > 0) possibleId = possibleId.Substring(0, spaceIdx);
                                    // Strip non-digits just in case
                                    possibleId = new string(possibleId.Where(char.IsDigit).ToArray());
                                    if (!string.IsNullOrEmpty(possibleId) && possibleId.Length >= 6)
                                    {
                                        _cachedId = possibleId;
                                        _logger.LogInformation($"Found RustDesk ID '{_cachedId}' from id colon in log file: {fileInfo.FullName}");
                                        return _cachedId;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to extract RustDesk ID from logs: {ex.Message}");
            }

            return "N/A";
        }

        public string GetOrCreatePassword()
        {
            if (!string.IsNullOrEmpty(_cachedPassword)) return _cachedPassword;

            // Generate a secure connection password based on the site ID and a local secret
            // To ensure simplicity and security, we set RustDesk's permanent password to this generated value.
            _cachedPassword = GenerateSecurePassword();

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _rustDeskPath,
                    Arguments = $"--password {_cachedPassword}",
                    UseShellExecute = false,
                    RedirectStandardOutput = false, // Do not redirect standard output if not reading it
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        if (process.WaitForExit(3000))
                        {
                            if (process.ExitCode != 0)
                            {
                                _logger.LogError($"Failed to set RustDesk password. Exit code: {process.ExitCode}. Please ensure the Agent is running with Administrator/elevated privileges.");
                            }
                            else
                            {
                                _logger.LogInformation("RustDesk connection password set successfully.");
                            }
                        }
                        else
                        {
                            _logger.LogWarning("RustDesk password configuration timed out.");
                            try { process.Kill(); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error configuring RustDesk password.");
            }

            return _cachedPassword;
        }

        private string GenerateSecurePassword()
        {
            // Simple deterministic secure password for testing, in production this should be a random string saved locally.
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hashBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(_siteId + "BarisTechnicalServiceSecretSalt2026!"));
                var base64 = Convert.ToBase64String(hashBytes);
                // Sanitize password to make sure it's valid alphanumeric for RustDesk
                var clean = new string(base64.Where(char.IsLetterOrDigit).Take(12).ToArray());
                return clean;
            }
        }
    }
}

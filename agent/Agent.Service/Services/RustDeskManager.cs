using System.Diagnostics;
using System.IO;
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
            if (!string.IsNullOrEmpty(_cachedId)) return _cachedId;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _rustDeskPath,
                    Arguments = "--get-id",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        // Wait first, then read to prevent deadlock
                        if (process.WaitForExit(3000))
                        {
                            string output = process.StandardOutput.ReadToEnd().Trim();
                            if (!string.IsNullOrEmpty(output) && !output.Contains("Error"))
                            {
                                _cachedId = output;
                                _logger.LogInformation($"Retrieved RustDesk ID: {_cachedId}");
                                return _cachedId;
                            }
                        }
                        else
                        {
                            _logger.LogWarning("RustDesk ID retrieval timed out.");
                            try { process.Kill(); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving RustDesk ID.");
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

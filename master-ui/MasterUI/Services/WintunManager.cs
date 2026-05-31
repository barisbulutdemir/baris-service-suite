using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MasterUI.Services
{
    public class WintunManager
    {
        private Process? _tun2socksProcess;
        private readonly string _adapterName = "BarisVPN";
        private readonly int _socksPort = 1080;
        
        public event Action<string>? OnLog;

        public bool IsRunning => _tun2socksProcess != null && !_tun2socksProcess.HasExited;

        public async Task<bool> StartAsync()
        {
            Log("[Wintun] Starting virtual network adapter...");

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string tun2socksPath = Path.Combine(baseDir, "tun2socks.exe");
            string wintunDllPath = Path.Combine(baseDir, "wintun.dll");

            // Verify files exist
            if (!File.Exists(tun2socksPath))
            {
                Log($"[Wintun] ERROR: 'tun2socks.exe' not found at {tun2socksPath}. Please ensure it is in the application folder.");
                return false;
            }

            if (!File.Exists(wintunDllPath))
            {
                Log($"[Wintun] ERROR: 'wintun.dll' not found at {wintunDllPath}. Please ensure it is in the application folder.");
                return false;
            }

            try
            {
                // Start tun2socks to create the Wintun adapter and route IP traffic to SOCKS5 proxy
                var startInfo = new ProcessStartInfo
                {
                    FileName = tun2socksPath,
                    Arguments = $"-device tun://{_adapterName} -proxy socks5://127.0.0.1:{_socksPort} -loglevel info",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Verb = "runas" // Request elevation (requires app to run as administrator)
                };

                _tun2socksProcess = new Process { StartInfo = startInfo };
                _tun2socksProcess.OutputDataReceived += (s, e) => { if (e.Data != null) Log($"[tun2socks] {e.Data}"); };
                _tun2socksProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) Log($"[tun2socks-err] {e.Data}"); };

                _tun2socksProcess.Start();
                _tun2socksProcess.BeginOutputReadLine();
                _tun2socksProcess.BeginErrorReadLine();

                Log("[Wintun] tun2socks process started. Waiting 3 seconds for network interface initialization...");
                await Task.Delay(3000);

                if (_tun2socksProcess.HasExited)
                {
                    Log("[Wintun] ERROR: tun2socks process exited unexpectedly.");
                    return false;
                }

                // Configure virtual adapter IP Address
                Log($"[Wintun] Configuring IP Address 192.168.0.99 for {_adapterName}...");
                bool ipSuccess = RunCommand("netsh", $"interface ipv4 set address name=\"{_adapterName}\" source=static address=192.168.0.99 mask=255.255.255.0 gateway=none");
                if (!ipSuccess)
                {
                    Log("[Wintun] WARNING: IP Configuration via netsh failed. Retrying with PowerShell...");
                    RunCommand("powershell", $"-Command \"New-NetIPAddress -InterfaceAlias '{_adapterName}' -IPAddress '192.168.0.99' -PrefixLength 24\"");
                }

                // Configure Route Table (Forces 192.168.0.0/24 to flow through the virtual adapter)
                Log($"[Wintun] Adding route for 192.168.0.0/24 through {_adapterName}...");
                RunCommand("route", $"add 192.168.0.0 mask 255.255.255.0 192.168.0.99 metric 1");

                Log("[Wintun] Virtual network configured successfully. 192.168.0.0/24 traffic will be tunneled.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Wintun] ERROR starting adapter: {ex.Message}");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            Log("[Wintun] Stopping virtual network adapter and cleaning up routing...");

            // 1. Remove Route
            try
            {
                Log("[Wintun] Deleting route for 192.168.0.0/24...");
                RunCommand("route", "delete 192.168.0.0 mask 255.255.255.0");
            }
            catch (Exception ex)
            {
                Log($"[Wintun] Error removing route: {ex.Message}");
            }

            // 2. Kill tun2socks process (which also disposes of the Wintun adapter)
            if (_tun2socksProcess != null && !_tun2socksProcess.HasExited)
            {
                try
                {
                    Log("[Wintun] Terminating tun2socks process...");
                    _tun2socksProcess.Kill();
                    _tun2socksProcess.Dispose();
                }
                catch (Exception ex)
                {
                    Log($"[Wintun] Error terminating tun2socks: {ex.Message}");
                }
            }
            _tun2socksProcess = null;

            Log("[Wintun] Cleanup completed. Network restored.");
        }

        private bool RunCommand(string filename, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = filename,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit(5000);
                        return proc.ExitCode == 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Wintun] Shell Command Failed: {filename} {arguments}. Error: {ex.Message}");
            }
            return false;
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }
    }
}

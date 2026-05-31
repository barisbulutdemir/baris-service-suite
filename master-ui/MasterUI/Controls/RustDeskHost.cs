using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace MasterUI.Controls
{
    public class RustDeskHost : WindowsFormsHost
    {
        private readonly Panel _containerPanel;
        private Process? _rustDeskProcess;
        private IntPtr _rustDeskHwnd = IntPtr.Zero;
        private readonly System.Windows.Forms.Timer _resizeTimer;
        private CancellationTokenSource? _cts;

        // Win32 API Imports
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
        private static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

        private const int GWL_STYLE = -16;
        private const int WS_POPUP = 0x800000;
        private const int WS_CAPTION = 0xC00000;
        private const int WS_THICKFRAME = 0x40000;
        private const int WS_CHILD = 0x40000000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        public event Action<string>? OnLog;

        public RustDeskHost()
        {
            _containerPanel = new Panel
            {
                BackColor = System.Drawing.Color.FromArgb(20, 20, 20),
                Dock = DockStyle.Fill
            };

            Child = _containerPanel;

            // Simple timer to ensure resizing catches up with layout updates
            _resizeTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _resizeTimer.Tick += (s, e) => ResizeEmbeddedWindow();

            _containerPanel.Resize += (s, e) => _resizeTimer.Start();
        }

        public async Task<bool> ConnectAndEmbedAsync(string rustDeskId, string password)
        {
            _cts = new CancellationTokenSource();
            Log($"[RustDeskHost] Starting connection to peer ID {rustDeskId}...");

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string rustDeskPath = Path.Combine(baseDir, "rustdesk.exe");

            // Look in Program Files if not local
            if (!File.Exists(rustDeskPath))
            {
                rustDeskPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "rustdesk.exe");
            }
            if (!File.Exists(rustDeskPath))
            {
                rustDeskPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RustDesk", "rustdesk.exe");
            }

            if (!File.Exists(rustDeskPath))
            {
                Log("[RustDeskHost] ERROR: rustdesk.exe client executable not found in Program Files or Application folder.");
                return false;
            }

            try
            {
                // Start RustDesk client to connect to peer
                var psi = new ProcessStartInfo
                {
                    FileName = rustDeskPath,
                    Arguments = $"--connect {rustDeskId} --password {password} --new",
                    UseShellExecute = false,
                    CreateNoWindow = false // Windows needs to render it so we can grab the handle
                };

                _rustDeskProcess = Process.Start(psi);
                if (_rustDeskProcess == null)
                {
                    Log("[RustDeskHost] ERROR: Failed to launch RustDesk process.");
                    return false;
                }

                // Poll for window handle creation
                Log("[RustDeskHost] Waiting for RustDesk window to initialize...");
                int elapsed = 0;
                while (elapsed < 10000 && !_cts.Token.IsCancellationRequested) // 10 seconds timeout
                {
                    _rustDeskProcess.Refresh();
                    IntPtr hwnd = FindRustDeskWindow(rustDeskId);
                    if (hwnd != IntPtr.Zero)
                    {
                        _rustDeskHwnd = hwnd;
                        break;
                    }
                    await Task.Delay(250, _cts.Token);
                    elapsed += 250;
                }

                if (_rustDeskHwnd == IntPtr.Zero)
                {
                    Log("[RustDeskHost] ERROR: RustDesk window handle could not be captured (timed out).");
                    CloseConnection();
                    return false;
                }

                // Remove Window Border and Title Bar, set as Child process
                Log("[RustDeskHost] Capturing window handle, strip borders and embed...");
                int style = GetWindowLong(_rustDeskHwnd, GWL_STYLE);
                style &= ~WS_POPUP;
                style &= ~WS_CAPTION;
                style &= ~WS_THICKFRAME;
                style |= WS_CHILD;
                SetWindowLong(_rustDeskHwnd, GWL_STYLE, style);

                // Set parent container to our WinForms Panel
                SetParent(_rustDeskHwnd, _containerPanel.Handle);

                // Refit layouts
                SetWindowPos(_rustDeskHwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                ResizeEmbeddedWindow();

                Log("[RustDeskHost] RustDesk connection embedded successfully.");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[RustDeskHost] ERROR: {ex.Message}");
                CloseConnection();
                return false;
            }
        }

        private IntPtr FindRustDeskWindow(string rustDeskId)
        {
            IntPtr foundHwnd = IntPtr.Zero;
            IntPtr backupHwnd = IntPtr.Zero;

            EnumWindows((hwnd, lParam) =>
            {
                if (IsWindowVisible(hwnd))
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    try
                    {
                        using (var proc = Process.GetProcessById((int)pid))
                        {
                            if (proc.ProcessName.Equals("rustdesk", StringComparison.OrdinalIgnoreCase))
                            {
                                var sb = new System.Text.StringBuilder(256);
                                GetWindowText(hwnd, sb, sb.Capacity);
                                string title = sb.ToString();

                                string cleanTitle = title.Replace(" ", "");
                                string cleanId = rustDeskId.Replace(" ", "");

                                if (!string.IsNullOrEmpty(cleanId) && cleanTitle.Contains(cleanId))
                                {
                                    foundHwnd = hwnd;
                                    return false; // Stop enumeration
                                }

                                // Fallback: any visible window of rustdesk that isn't the main control window or settings
                                if (!string.IsNullOrEmpty(title) &&
                                    !title.Equals("RustDesk", StringComparison.OrdinalIgnoreCase) &&
                                    !title.Contains("Administrator") &&
                                    !title.Contains("Service"))
                                {
                                    backupHwnd = hwnd;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore process access or system process errors
                    }
                }
                return true; // Continue enumeration
            }, IntPtr.Zero);

            return foundHwnd != IntPtr.Zero ? foundHwnd : backupHwnd;
        }

        private void ResizeEmbeddedWindow()
        {
            if (_rustDeskHwnd != IntPtr.Zero)
            {
                _resizeTimer.Stop();
                MoveWindow(_rustDeskHwnd, 0, 0, _containerPanel.Width, _containerPanel.Height, true);
            }
        }

        public void CloseConnection()
        {
            _cts?.Cancel();
            _resizeTimer.Stop();

            Log("[RustDeskHost] Closing remote desktop connection...");

            if (_rustDeskProcess != null)
            {
                try
                {
                    if (!_rustDeskProcess.HasExited)
                    {
                        _rustDeskProcess.Kill();
                    }
                    _rustDeskProcess.Dispose();
                }
                catch { }
                _rustDeskProcess = null;
            }
            _rustDeskHwnd = IntPtr.Zero;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CloseConnection();
            }
            base.Dispose(disposing);
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }
    }
}

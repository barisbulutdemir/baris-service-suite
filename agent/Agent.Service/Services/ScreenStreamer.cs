using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using SocketIOClient;
using System.IO.Compression;

namespace Agent.Service.Services
{
    public class ScreenStreamer
    {
        private readonly ILogger<ScreenStreamer> _logger;
        private CancellationTokenSource? _cts;
        private string? _masterSocketId;
        private SocketIO? _client;
        private bool _isStreaming;
        private byte[]? _prevHash;
        private FileStream? _activeFileStream;
        private string? _activeFilePath;
        private bool _activeFileIsFolder = false;
        private string _serverUrl = "https://destek.barisbd.tr";

        // Custom screen quality options
        private int _customMaxWidth = 1024;
        private int _customQuality = 35;
        private bool _hasCustomQuality = false;

        // P2P UDP Hole Punching fields
        private UdpClient? _udpClient;
        private IPEndPoint? _remoteUdpEP;
        private bool _p2pConnected = false;
        private CancellationTokenSource? _udpCts;
        private DateTime _lastInputTime = DateTime.MinValue;

        // Pinvokes for input injection
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, uint dwExtraInfo);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

        // Pinvokes for cursor capture
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorInfo(out CURSORINFO pci);

        private const int CURSOR_SHOWING = 0x00000001;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public ScreenStreamer(ILogger<ScreenStreamer> logger)
        {
            _logger = logger;
        }

        public void Start(SocketIO client, string masterSocketId, string serverUrl)
        {
            if (_isStreaming) return;
            
            _client = client;
            _masterSocketId = masterSocketId;
            _serverUrl = serverUrl;
            _isStreaming = true;
            _cts = new CancellationTokenSource();
            _prevHash = null;

            Task.Run(() => CaptureLoopAsync(_cts.Token));
            Task.Run(() => ClipboardPollLoopAsync(_cts.Token));
            _logger.LogInformation($"[ScreenStreamer] Screen sharing started for Master: {masterSocketId}");
            AgentLogger.Log("ScreenStreamer", $"Screen sharing started for Master: {masterSocketId}");
        }

        public void Stop()
        {
            if (!_isStreaming) return;
            _isStreaming = false;
            _cts?.Cancel();
            CleanupUdpHolePunching();
            _logger.LogInformation("[ScreenStreamer] Screen sharing stopped.");
            AgentLogger.Log("ScreenStreamer", "Screen sharing stopped.");
        }

        private async Task CaptureLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {


                    byte[] jpegBytes = CaptureAndProcessScreen();
                    if (jpegBytes != null && jpegBytes.Length > 0 && _client != null && _client.Connected && !string.IsNullOrEmpty(_masterSocketId))
                    {
                        bool sentP2P = false;
                        if (_p2pConnected && _udpClient != null && _remoteUdpEP != null && jpegBytes.Length <= 60 * 1024)
                        {
                            try
                            {
                                await _udpClient.SendAsync(jpegBytes, jpegBytes.Length, _remoteUdpEP);
                                sentP2P = true;
                            }
                            catch { }
                        }

                        if (!sentP2P)
                        {
                            string base64Data = Convert.ToBase64String(jpegBytes);
                            
                            // Emit to orchestrator via tunnel-data
                            await _client.EmitAsync("tunnel-data", new object[] { new 
                            { 
                                masterSocketId = _masterSocketId, 
                                connectionId = "screen-share", 
                                chunk = base64Data 
                            } });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ScreenStreamer] Error in screen capture/send loop");
                }

                // Pacing delay: 80ms for TCP relay (up to 12.5 FPS), 40ms for UDP P2P (up to 25 FPS)
                int loopDelay = _p2pConnected ? 40 : 80;
                await Task.Delay(loopDelay, token);
            }
        }

        private byte[] CaptureAndProcessScreen()
        {
            try
            {
                var primaryScreen = Screen.PrimaryScreen;
                if (primaryScreen == null)
                {
                    _logger.LogWarning("[ScreenStreamer] PrimaryScreen is null.");
                    return Array.Empty<byte>();
                }
                int origWidth = primaryScreen.Bounds.Width;
                int origHeight = primaryScreen.Bounds.Height;

                // Max width and quality dynamically adjusted based on connection type and custom settings
                int targetWidth = origWidth;
                int targetHeight = origHeight;
                int maxWidth = _p2pConnected ? 1280 : 1024;
                long quality = _p2pConnected ? 45L : 30L;

                if (_hasCustomQuality)
                {
                    maxWidth = _customMaxWidth;
                    quality = _customQuality;
                }

                if (origWidth > maxWidth)
                {
                    double ratio = (double)origHeight / origWidth;
                    targetWidth = maxWidth;
                    targetHeight = (int)(maxWidth * ratio);
                }

                using (Bitmap origBmp = new Bitmap(origWidth, origHeight))
                {
                    using (Graphics g = Graphics.FromImage(origBmp))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, origBmp.Size);
                        DrawCursor(g);
                    }

                    // Check if screen changed significantly or if we had recent user input to show cursor movement
                    bool forceSend = (DateTime.UtcNow - _lastInputTime).TotalMilliseconds < 1000;
                    if (!forceSend && !HasScreenChanged(origBmp))
                    {
                        return Array.Empty<byte>(); // Skip sending if screen is idle
                    }

                    Bitmap bmpToSave = origBmp;
                    Bitmap? scaledBmp = null;
                    if (origWidth > maxWidth)
                    {
                        scaledBmp = new Bitmap(origBmp, targetWidth, targetHeight);
                        bmpToSave = scaledBmp;
                    }

                    try
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            ImageCodecInfo? jpegCodec = GetEncoderInfo("image/jpeg");
                            if (jpegCodec != null)
                            {
                                EncoderParameters encoderParams = new EncoderParameters(1);
                                encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality); // Dynamic Quality
                                bmpToSave.Save(ms, jpegCodec, encoderParams);
                            }
                            else
                            {
                                bmpToSave.Save(ms, ImageFormat.Jpeg);
                            }
                            return ms.ToArray();
                        }
                    }
                    finally
                    {
                        scaledBmp?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScreenStreamer] Failed to capture/process screen");
                return Array.Empty<byte>();
            }
        }

        private void DrawCursor(Graphics g)
        {
            try
            {
                var cursorInfo = new CURSORINFO();
                cursorInfo.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(cursorInfo);
                if (GetCursorInfo(out cursorInfo) && cursorInfo.flags == CURSOR_SHOWING)
                {
                    using (var icon = Icon.FromHandle(cursorInfo.hCursor))
                    {
                        g.DrawIcon(icon, cursorInfo.ptScreenPos.X, cursorInfo.ptScreenPos.Y);
                    }
                }
            }
            catch { }
        }

        private bool HasScreenChanged(Bitmap bmp)
        {
            try
            {
                // Create a tiny 16x16 thumbnail for quick comparisons
                using (Bitmap thumb = new Bitmap(bmp, 16, 16))
                {
                    byte[] currentHash = new byte[16 * 16 * 4];
                    BitmapData data = thumb.LockBits(new Rectangle(0, 0, 16, 16), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, currentHash, 0, currentHash.Length);
                    thumb.UnlockBits(data);

                    if (_prevHash == null || _prevHash.Length != currentHash.Length)
                    {
                        _prevHash = currentHash;
                        return true;
                    }

                    bool changed = false;
                    for (int i = 0; i < currentHash.Length; i += 4)
                    {
                        // Allow minor pixel variance (e.g. noise or slight changes)
                        if (Math.Abs(currentHash[i] - _prevHash[i]) > 8 ||
                            Math.Abs(currentHash[i + 1] - _prevHash[i + 1]) > 8 ||
                            Math.Abs(currentHash[i + 2] - _prevHash[i + 2]) > 8)
                        {
                            changed = true;
                            break;
                        }
                    }

                    if (changed)
                    {
                        _prevHash = currentHash;
                    }
                    return changed;
                }
            }
            catch
            {
                return true; // Send on error
            }
        }

        private ImageCodecInfo? GetEncoderInfo(string mimeType)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < codecs.Length; i++)
            {
                if (codecs[i].MimeType == mimeType)
                    return codecs[i];
            }
            return null;
        }

        public void HandleInputEvent(string json)
        {
            _lastInputTime = DateTime.UtcNow;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString() ?? "";

                    if (type == "mousemove" || type == "mousedown" || type == "mouseup")
                    {
                        double x = root.GetProperty("x").GetDouble();
                        double y = root.GetProperty("y").GetDouble();

                        var primaryScreen = Screen.PrimaryScreen;
                        if (primaryScreen == null) return;

                        int screenWidth = primaryScreen.Bounds.Width;
                        int screenHeight = primaryScreen.Bounds.Height;
                        int targetX = (int)(x * screenWidth);
                        int targetY = (int)(y * screenHeight);

                        SetCursorPos(targetX, targetY);

                        if (type == "mousedown")
                        {
                            string button = root.GetProperty("button").GetString() ?? "left";
                            if (button == "left") mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                            else if (button == "right") mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                            else if (button == "middle") mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, 0);
                        }
                        else if (type == "mouseup")
                        {
                            string button = root.GetProperty("button").GetString() ?? "left";
                            if (button == "left") mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                            else if (button == "right") mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                            else if (button == "middle") mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, 0);
                        }
                    }
                    else if (type == "keydown" || type == "keyup")
                    {
                        int keyCode = root.GetProperty("keyCode").GetInt32();
                        uint flags = (type == "keyup") ? KEYEVENTF_KEYUP : 0;
                        keybd_event((byte)keyCode, 0, flags, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScreenStreamer] Error handling input event");
            }
        }

        public void HandleFileTransferPayload(string json)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString() ?? "";

                    if (type == "start")
                    {
                        string fileName = root.GetProperty("fileName").GetString() ?? "file";
                        long fileSize = root.GetProperty("fileSize").GetInt64();
                        _activeFileIsFolder = root.TryGetProperty("isFolder", out var isFolderProp) && isFolderProp.GetBoolean();

                        string destDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                        if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir))
                        {
                            destDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                        }
                        if (!Directory.Exists(destDir))
                        {
                            destDir = AppDomain.CurrentDomain.BaseDirectory;
                        }

                        _activeFilePath = Path.Combine(destDir, fileName);
                        _activeFileStream = new FileStream(_activeFilePath, FileMode.Create, FileAccess.Write);
                        
                        _logger.LogInformation($"[ScreenStreamer] Receiving file: {fileName} ({fileSize} bytes) -> {_activeFilePath}");
                        AgentLogger.Log("ScreenStreamer", $"Receiving file: {fileName} ({fileSize} bytes) -> {_activeFilePath}");
                    }
                    else if (type == "chunk" && _activeFileStream != null)
                    {
                        string base64Data = root.GetProperty("data").GetString() ?? "";
                        byte[] bytes = Convert.FromBase64String(base64Data);
                        _activeFileStream.Write(bytes, 0, bytes.Length);
                        
                        // Send back ACK to prevent socket buffer saturation
                        _ = SendFileTransferAckAsync();
                    }
                    else if (type == "end" && _activeFileStream != null)
                    {
                        _activeFileStream.Close();
                        _activeFileStream.Dispose();
                        _activeFileStream = null;
                        
                        _logger.LogInformation($"[ScreenStreamer] File received successfully: {_activeFilePath}");
                        AgentLogger.Log("ScreenStreamer", $"File received successfully: {_activeFilePath}");

                        if (_activeFileIsFolder && !string.IsNullOrEmpty(_activeFilePath) && File.Exists(_activeFilePath))
                        {
                            try
                            {
                                string destParentDir = Path.GetDirectoryName(_activeFilePath) ?? "";
                                string folderName = Path.GetFileNameWithoutExtension(_activeFilePath);
                                string targetDir = Path.Combine(destParentDir, folderName);

                                _logger.LogInformation($"[ScreenStreamer] Folder transfer detected. Extracting {_activeFilePath} to {targetDir}...");
                                AgentLogger.Log("ScreenStreamer", $"Folder transfer detected. Extracting to {targetDir}...");

                                if (!Directory.Exists(targetDir))
                                {
                                    Directory.CreateDirectory(targetDir);
                                }

                                ZipFile.ExtractToDirectory(_activeFilePath, targetDir);
                                File.Delete(_activeFilePath);

                                _logger.LogInformation($"[ScreenStreamer] Folder extraction complete.");
                                AgentLogger.Log("ScreenStreamer", "Folder extraction complete.");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"[ScreenStreamer] Failed to extract folder: {ex.Message}");
                                AgentLogger.Log("ScreenStreamer", $"Failed to extract folder: {ex.Message}");
                            }
                        }
                        
                        _activeFilePath = null;
                        _activeFileIsFolder = false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScreenStreamer] Error in file transfer");
                AgentLogger.Log("ScreenStreamer", $"Error in file transfer: {ex.Message}");
                if (_activeFileStream != null)
                {
                    try { _activeFileStream.Close(); } catch { }
                    try { _activeFileStream.Dispose(); } catch { }
                    _activeFileStream = null;
                }
            }
        }

        private async Task SendFileTransferAckAsync()
        {
            if (_client != null && _client.Connected && !string.IsNullOrEmpty(_masterSocketId))
            {
                try
                {
                    var ackPayload = new { type = "ack" };
                    string json = System.Text.Json.JsonSerializer.Serialize(ackPayload);
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    string base64Data = Convert.ToBase64String(bytes);

                    await _client.EmitAsync("tunnel-data", new object[] { new 
                    { 
                        masterSocketId = _masterSocketId, 
                        connectionId = "file-transfer-ack", 
                        chunk = base64Data 
                    } });
                }
                catch { }
            }
        }

        private string _lastClipboardText = "";
        private bool _isClipboardActive = false;

        private bool IsSession0()
        {
            try
            {
                return System.Diagnostics.Process.GetCurrentProcess().SessionId == 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task ClipboardPollLoopAsync(CancellationToken token)
        {
            if (IsSession0())
            {
                _logger.LogInformation("[ScreenStreamer] Session 0 detected (Windows Service). Clipboard synchronization is disabled.");
                AgentLogger.Log("ScreenStreamer", "Session 0 detected (Windows Service). Clipboard synchronization is disabled.");
                return;
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    string currentText = GetClipboardTextSTA();
                    if (!string.IsNullOrEmpty(currentText) && currentText != _lastClipboardText)
                    {
                        _lastClipboardText = currentText;

                        // Create payload
                        var payload = new
                        {
                            type = "clipboard",
                            text = currentText
                        };
                        string json = System.Text.Json.JsonSerializer.Serialize(payload);
                        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                        string base64Data = Convert.ToBase64String(bytes);

                        if (_client != null && _client.Connected && !string.IsNullOrEmpty(_masterSocketId))
                        {
                            await _client.EmitAsync("tunnel-data", new object[] { new 
                            { 
                                masterSocketId = _masterSocketId, 
                                connectionId = "clipboard-sync", 
                                chunk = base64Data 
                            } });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[ScreenStreamer] Clipboard poll error: {ex.Message}");
                }
                await Task.Delay(1000, token);
            }
        }

        public void HandleClipboardSyncPayload(string json)
        {
            if (IsSession0()) return;

            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString() ?? "";
                    if (type == "clipboard")
                    {
                        string text = root.GetProperty("text").GetString() ?? "";
                        if (!string.IsNullOrEmpty(text) && text != _lastClipboardText)
                        {
                            _lastClipboardText = text;
                            SetClipboardTextSTA(text);
                            _logger.LogInformation("[Clipboard] Synced clipboard from Master.");
                            AgentLogger.Log("ScreenStreamer", "Synced clipboard from Master.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScreenStreamer] Error setting remote clipboard");
            }
        }



        private string GetClipboardTextSTA()
        {
            if (IsSession0()) return "";
            if (_isClipboardActive) return ""; // Guard: prevent concurrent thread spawning if hung

            _isClipboardActive = true;
            string text = "";
            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            text = Clipboard.GetText();
                        }
                    }
                    catch { }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                if (!thread.Join(500)) // Limit wait time to 500ms
                {
                    _logger.LogWarning("[ScreenStreamer] GetClipboardTextSTA timed out. Disabling clipboard sync to prevent thread leaks.");
                    AgentLogger.Log("ScreenStreamer", "GetClipboardTextSTA timed out. Disabling clipboard sync to prevent thread leaks.");
                    return ""; // Keep _isClipboardActive = true to prevent further calls
                }
                _isClipboardActive = false; // Reset gatekeeper on success
            }
            catch
            {
                _isClipboardActive = false;
            }
            return text;
        }

        private void SetClipboardTextSTA(string text)
        {
            if (IsSession0()) return;
            if (_isClipboardActive) return; // Guard: prevent thread spawn if Get is hung

            _isClipboardActive = true;
            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        Clipboard.SetText(text);
                    }
                    catch { }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                if (!thread.Join(500))
                {
                    _logger.LogWarning("[ScreenStreamer] SetClipboardTextSTA timed out. Disabling clipboard sync to prevent thread leaks.");
                    AgentLogger.Log("ScreenStreamer", "SetClipboardTextSTA timed out. Disabling clipboard sync to prevent thread leaks.");
                    return;
                }
                _isClipboardActive = false; // Reset gatekeeper on success
            }
            catch
            {
                _isClipboardActive = false;
            }
        }

        #region UDP Hole Punching & P2P Stream

        public void HandleUdpHolePunchPayload(string json)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString() ?? "";
                    if (type == "init")
                    {
                        string endpointStr = root.GetProperty("endpoint").GetString() ?? "";
                        if (IPEndPoint.TryParse(endpointStr, out IPEndPoint? masterEP))
                        {
                            _logger.LogInformation($"[P2P] Master public endpoint received: {masterEP}. Running STUN...");
                            AgentLogger.Log("ScreenStreamer", $"Master public endpoint received: {masterEP}. Running STUN...");
                            
                            _ = Task.Run(() => InitUdpHolePunchingAsync(masterEP));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScreenStreamer] Error handling UDP hole punch payload");
            }
        }

        private async Task InitUdpHolePunchingAsync(IPEndPoint masterEP)
        {
            CleanupUdpHolePunching();

            _udpCts = new CancellationTokenSource();
            _p2pConnected = false;

            try
            {
                _udpClient = new UdpClient(0);
                if (_udpClient.Client.LocalEndPoint is IPEndPoint localEP)
                {
                    int localPort = localEP.Port;
                    _logger.LogInformation($"[P2P] Local UDP port reserved: {localPort}. Querying STUN...");
                    AgentLogger.Log("ScreenStreamer", $"Local UDP port reserved: {localPort}. Querying STUN...");
                }
                else
                {
                    _logger.LogWarning("[P2P] Failed to get LocalEndPoint.");
                    return;
                }

                IPEndPoint? myPublicEP = await Task.Run(() => GetPublicIP(_udpClient));
                if (myPublicEP == null)
                {
                    _logger.LogWarning("[P2P] Failed to get public IP from STUN. Falling back to server tannel.");
                    AgentLogger.Log("ScreenStreamer", "Failed to get public IP from STUN. Falling back to server tannel.");
                    return;
                }

                _logger.LogInformation($"[P2P] Public endpoint resolved: {myPublicEP}. Sending ACK to Master...");
                AgentLogger.Log("ScreenStreamer", $"Public endpoint resolved: {myPublicEP}. Sending ACK to Master...");

                // Start UDP Receive Loop
                _ = Task.Run(() => ReceiveUdpPacketsAsync(_udpCts.Token));

                // Send ack back to master via socket tannel
                var ackPayload = new
                {
                    type = "ack",
                    endpoint = myPublicEP.ToString()
                };
                string jsonPayload = System.Text.Json.JsonSerializer.Serialize(ackPayload);
                byte[] bytes = Encoding.UTF8.GetBytes(jsonPayload);
                string base64Data = Convert.ToBase64String(bytes);

                if (_client != null && _client.Connected && !string.IsNullOrEmpty(_masterSocketId))
                {
                    await _client.EmitAsync("tunnel-data", new object[] { new 
                    { 
                        masterSocketId = _masterSocketId, 
                        connectionId = "udp-holepunch-ack", 
                        chunk = base64Data 
                    } });
                }

                // Start punching to Master
                _ = Task.Run(() => PunchLoopAsync(masterEP, _udpCts.Token));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[P2P] Error initializing P2P");
            }
        }

        private void CleanupUdpHolePunching()
        {
            _udpCts?.Cancel();
            _udpCts = null;

            _p2pConnected = false;
            _remoteUdpEP = null;

            if (_udpClient != null)
            {
                try { _udpClient.Close(); } catch { }
                try { _udpClient.Dispose(); } catch { }
                _udpClient = null;
            }
        }

        private IPEndPoint? GetPublicIP(UdpClient client)
        {
            var stunServers = new (string Host, int Port)[]
            {
                ("stun.l.google.com", 19302),
                ("stun1.l.google.com", 19302),
                ("stun2.l.google.com", 19302),
                ("stun.sipgate.net", 10000),
                ("stun.voipbuster.com", 3478)
            };

            foreach (var stun in stunServers)
            {
                _logger.LogInformation($"[P2P] Querying STUN server: {stun.Host}:{stun.Port}...");
                AgentLogger.Log("ScreenStreamer", $"[P2P] Querying STUN server: {stun.Host}:{stun.Port}...");
                try
                {
                    IPAddress[] ips = Dns.GetHostAddresses(stun.Host);
                    if (ips.Length == 0)
                    {
                        _logger.LogWarning($"[P2P] STUN Error ({stun.Host}): DNS lookup failed.");
                        AgentLogger.Log("ScreenStreamer", $"[P2P] STUN Error ({stun.Host}): DNS lookup failed.");
                        continue;
                    }
                    var ip = ips[0];

                    client.Client.SendTimeout = 1000;
                    client.Client.ReceiveTimeout = 1000;

                    byte[] request = new byte[20];
                    request[1] = 0x01; // Binding Request
                    new Random().NextBytes(new ArraySegment<byte>(request, 4, 16).Array!);

                    client.Send(request, request.Length, new IPEndPoint(ip, stun.Port));
                    IPEndPoint? remoteEP = null;
                    byte[] response = client.Receive(ref remoteEP);

                    if (response.Length >= 20)
                    {
                        int i = 20;
                        while (i < response.Length)
                        {
                            int attrType = (response[i] << 8) | response[i + 1];
                            int attrLen = (response[i + 2] << 8) | response[i + 3];
                            if (attrType == 0x0001) // MAPPED-ADDRESS
                            {
                                int mappedPort = (response[i + 6] << 8) | response[i + 7];
                                string resolvedIp = $"{response[i + 8]}.{response[i + 9]}.{response[i + 10]}.{response[i + 11]}";
                                var publicEP = new IPEndPoint(IPAddress.Parse(resolvedIp), mappedPort);
                                _logger.LogInformation($"[P2P] STUN Success ({stun.Host}): Public IP = {publicEP}");
                                AgentLogger.Log("ScreenStreamer", $"[P2P] STUN Success ({stun.Host}): Public IP = {publicEP}");
                                return publicEP;
                            }
                            else if (attrType == 0x0020) // XOR-MAPPED-ADDRESS
                            {
                                int mappedPort = ((response[i + 6] << 8) | response[i + 7]) ^ 0x2112;
                                byte[] ipBytes = new byte[4];
                                ipBytes[0] = (byte)(response[i + 8] ^ 0x21);
                                ipBytes[1] = (byte)(response[i + 9] ^ 0x12);
                                ipBytes[2] = (byte)(response[i + 10] ^ 0xA4);
                                ipBytes[3] = (byte)(response[i + 11] ^ 0x42);
                                var publicEP = new IPEndPoint(new IPAddress(ipBytes), mappedPort);
                                _logger.LogInformation($"[P2P] STUN Success ({stun.Host}): Public IP (XOR) = {publicEP}");
                                AgentLogger.Log("ScreenStreamer", $"[P2P] STUN Success ({stun.Host}): Public IP (XOR) = {publicEP}");
                                return publicEP;
                            }
                            i += 4 + attrLen;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[P2P] STUN Timeout or Error ({stun.Host}): {ex.Message}");
                    AgentLogger.Log("ScreenStreamer", $"[P2P] STUN Timeout or Error ({stun.Host}): {ex.Message}");
                }
            }
            return null;
        }

        private async Task ReceiveUdpPacketsAsync(CancellationToken token)
        {
            if (_udpClient == null) return;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(token);
                    byte[] data = result.Buffer;
                    IPEndPoint remoteEP = result.RemoteEndPoint;

                    if (data.Length == 5 && Encoding.UTF8.GetString(data) == "PUNCH")
                    {
                        byte[] okBytes = Encoding.UTF8.GetBytes("OK");
                        await _udpClient.SendAsync(okBytes, okBytes.Length, remoteEP);

                        if (!_p2pConnected)
                        {
                            _p2pConnected = true;
                            _remoteUdpEP = remoteEP;
                            _logger.LogInformation($"[P2P] Direct P2P tunnel established! Partner IP: {remoteEP}");
                            AgentLogger.Log("ScreenStreamer", $"Direct P2P tunnel established! Partner IP: {remoteEP}");
                        }
                    }
                    else if (data.Length == 2 && Encoding.UTF8.GetString(data) == "OK")
                    {
                        if (!_p2pConnected)
                        {
                            _p2pConnected = true;
                            _remoteUdpEP = remoteEP;
                            _logger.LogInformation($"[P2P] Direct P2P tunnel established! Partner IP: {remoteEP}");
                            AgentLogger.Log("ScreenStreamer", $"Direct P2P tunnel established! Partner IP: {remoteEP}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore
                }
            }
        }

        private async Task PunchLoopAsync(IPEndPoint remoteEP, CancellationToken token)
        {
            if (_udpClient == null) return;

            byte[] punchBytes = Encoding.UTF8.GetBytes("PUNCH");
            _logger.LogInformation($"[P2P] Punching hole to Master endpoint: {remoteEP}");

            for (int i = 0; i < 50 && !token.IsCancellationRequested && !_p2pConnected; i++)
            {
                try
                {
                    await _udpClient.SendAsync(punchBytes, punchBytes.Length, remoteEP);
                }
                catch { }
                await Task.Delay(100, token);
            }
        }

        #endregion

        #region Diagnostic Speed Test

        public async void HandleSpeedTestPayload(string json)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString() ?? "";

                    if (type == "ping")
                    {
                        long timestamp = root.GetProperty("timestamp").GetInt64();

                        // 1. Measure Agent latency to server
                        var startTime = DateTime.Now;
                        int agentPing = 0;
                        try
                        {
                            using (var httpClient = new System.Net.Http.HttpClient())
                            {
                                httpClient.Timeout = TimeSpan.FromMilliseconds(2000);
                                string pingUrl = _serverUrl;
                                var resp = await httpClient.GetAsync(pingUrl);
                                agentPing = (int)(DateTime.Now - startTime).TotalMilliseconds;
                            }
                        }
                        catch
                        {
                            agentPing = -1;
                        }

                        // 2. Respond back with latency
                        var pongPayload = new
                        {
                            type = "pong",
                            timestamp = timestamp,
                            agentPing = agentPing
                        };
                        await SendSpeedTestPayloadAsync(pongPayload);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScreenStreamer] Error handling speed test payload");
            }
        }

        private async Task SendSpeedTestPayloadAsync(object payload)
        {
            if (_client != null && _client.Connected && !string.IsNullOrEmpty(_masterSocketId))
            {
                try
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(payload);
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    string base64Data = Convert.ToBase64String(bytes);

                    await _client.EmitAsync("tunnel-data", new object[] { new 
                    { 
                        masterSocketId = _masterSocketId, 
                        connectionId = "connection-speed-test", 
                        chunk = base64Data 
                    } });
                }
                catch { }
            }
        }

        public void HandleScreenQualityPayload(string json)
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    _customMaxWidth = root.GetProperty("maxWidth").GetInt32();
                    _customQuality = root.GetProperty("quality").GetInt32();
                    _hasCustomQuality = true;
                    _logger.LogInformation($"[ScreenStreamer] Screen quality updated by master: {_customMaxWidth}px, {_customQuality}%");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScreenStreamer] Error parsing screen quality settings");
            }
        }

        #endregion
    }
}

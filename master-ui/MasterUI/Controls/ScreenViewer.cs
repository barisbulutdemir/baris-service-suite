using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MasterUI.Services;

namespace MasterUI.Controls
{
    public class ScreenViewer : System.Windows.Controls.Image
    {
        private readonly OrchestratorClient _orchestrator;
        private readonly string _siteId;
        private bool _isActive;
        private DateTime _lastMouseMoveTime = DateTime.MinValue;
        public bool BypassSocketFrames { get; set; } = false;
        private readonly Action<int, bool>? _onFrameReceived;

        public ScreenViewer(OrchestratorClient orchestrator, string siteId, Action<int, bool>? onFrameReceived = null)
        {
            _orchestrator = orchestrator;
            _siteId = siteId;
            _onFrameReceived = onFrameReceived;
            
            Focusable = true;
            ClipToBounds = true;
            Stretch = Stretch.Uniform;

            // Attach event handlers
            MouseDown += OnMouseDownHandler;
            MouseUp += OnMouseUpHandler;
            MouseMove += OnMouseMoveHandler;
            PreviewKeyDown += OnPreviewKeyDownHandler;
            PreviewKeyUp += OnPreviewKeyUpHandler;

            // Subscribe to remote data
            _orchestrator.OnTunnelData += HandleTunnelData;
            
            _isActive = true;
            
            // Send start command
            _ = StartScreenShareAsync();
        }

        private async System.Threading.Tasks.Task StartScreenShareAsync()
        {
            try
            {
                // Send start control command
                await _orchestrator.SendTunnelDataAsync(_siteId, "screen-share", System.Text.Encoding.UTF8.GetBytes("start"));
            }
            catch { }
        }

        public async System.Threading.Tasks.Task StopScreenShareAsync()
        {
            if (!_isActive) return;
            _isActive = false;
            
            _orchestrator.OnTunnelData -= HandleTunnelData;
            
            try
            {
                // Send stop control command
                await _orchestrator.SendTunnelDataAsync(_siteId, "screen-share", System.Text.Encoding.UTF8.GetBytes("stop"));
            }
            catch { }
        }

        public void UpdateFrame(byte[] data, bool isUdp = false)
        {
            if (!_isActive) return;

            // Record stats
            _onFrameReceived?.Invoke(data.Length, isUdp);

            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                {
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    
                    Source = bitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenViewer] Error decoding frame: {ex.Message}");
            }
        }

        private void HandleTunnelData(string connectionId, byte[] data)
        {
            if (connectionId != "screen-share" || !_isActive) return;
            if (BypassSocketFrames) return;

            Dispatcher.Invoke(() =>
            {
                UpdateFrame(data, false);
            });
        }

        private void OnMouseMoveHandler(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Throttle mouse movements to prevent socket congestion (max 83 events per second)
            if ((DateTime.Now - _lastMouseMoveTime).TotalMilliseconds < 12)
            {
                return;
            }
            _lastMouseMoveTime = DateTime.Now;

            SendInputEvent(new
            {
                type = "mousemove",
                x = GetNormalizedX(e),
                y = GetNormalizedY(e)
            });
        }

        private void OnMouseDownHandler(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            Focus(); // Get focus for keyboard events
            
            string button = e.ChangedButton == MouseButton.Left ? "left" : 
                            e.ChangedButton == MouseButton.Right ? "right" : "middle";

            SendInputEvent(new
            {
                type = "mousedown",
                button = button,
                x = GetNormalizedX(e),
                y = GetNormalizedY(e)
            });
        }

        private void OnMouseUpHandler(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            string button = e.ChangedButton == MouseButton.Left ? "left" : 
                            e.ChangedButton == MouseButton.Right ? "right" : "middle";

            SendInputEvent(new
            {
                type = "mouseup",
                button = button,
                x = GetNormalizedX(e),
                y = GetNormalizedY(e)
            });
        }

        public event Action<System.Collections.Specialized.StringCollection>? OnFilesPasted;

        private void OnPreviewKeyDownHandler(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Check if Ctrl+V is pressed
            if (e.Key == System.Windows.Input.Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsFileDropList())
                    {
                        var filePaths = System.Windows.Clipboard.GetFileDropList();
                        if (filePaths != null && filePaths.Count > 0)
                        {
                            OnFilesPasted?.Invoke(filePaths);
                            e.Handled = true;
                            return;
                        }
                    }
                }
                catch { }
            }

            int vkCode = KeyInterop.VirtualKeyFromKey(e.Key);
            SendInputEvent(new
            {
                type = "keydown",
                keyCode = vkCode
            });
            e.Handled = true;
        }

        private void OnPreviewKeyUpHandler(object sender, System.Windows.Input.KeyEventArgs e)
        {
            int vkCode = KeyInterop.VirtualKeyFromKey(e.Key);
            SendInputEvent(new
            {
                type = "keyup",
                keyCode = vkCode
            });
            e.Handled = true;
        }

        private double GetNormalizedX(System.Windows.Input.MouseEventArgs e)
        {
            System.Windows.Point p = e.GetPosition(this);
            return Math.Max(0.0, Math.Min(1.0, p.X / ActualWidth));
        }

        private double GetNormalizedY(System.Windows.Input.MouseEventArgs e)
        {
            System.Windows.Point p = e.GetPosition(this);
            return Math.Max(0.0, Math.Min(1.0, p.Y / ActualHeight));
        }

        private void SendInputEvent(object payload)
        {
            if (!_isActive) return;

            try
            {
                string json = JsonSerializer.Serialize(payload);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                _ = _orchestrator.SendTunnelDataAsync(_siteId, "screen-share", bytes);
            }
            catch { }
        }
    }
}

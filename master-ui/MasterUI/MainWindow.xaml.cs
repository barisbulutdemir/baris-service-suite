using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MasterUI.Controls;
using MasterUI.Services;
using MasterUI.Models;
using System.IO.Compression;

namespace MasterUI
{
    public partial class MainWindow : Window
    {
        private OrchestratorClient? _orchestrator;
        private LocalSocksServer? _socksServer;
        private WintunManager? _wintunManager;
        private ScreenViewer? _screenViewer;
        
        private string _serverUrl = "http://3.73.144.148:3030";
        private string _authToken = "BarisServis2026!";
        private string? _activeSiteId;
        private bool _isConnecting = false;
        private double _lastSidebarWidth = 280;
        private bool _sidebarCollapsed = false;
        private System.Windows.Threading.DispatcherTimer? _clipboardTimer;
        private string _lastClipboardText = "";
        
        // P2P UDP Hole Punching fields
        private UdpClient? _udpClient;
        private IPEndPoint? _remoteUdpEP;
        private bool _p2pConnected = false;
        private CancellationTokenSource? _udpCts;

        // Screen share speed and stats tracking
        private bool _activeEnableTunnel = false;
        private bool _activeEnableScreen = false;
        private int _fpsCounter = 0;
        private long _bytesCounter = 0;
        private string _streamType = "Bağlanıyor...";
        private System.Windows.Threading.DispatcherTimer? _statsTimer;
        private TaskCompletionSource<bool>? _fileChunkAckTcs;

        public ObservableCollection<SiteUI> Sites { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            SitesListBox.ItemsSource = Sites;

            // Load appsettings.json dynamically
            LoadConfiguration();

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        private void LoadConfiguration()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    using (var doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("Orchestrator", out var orchProp))
                        {
                            if (orchProp.TryGetProperty("Url", out var urlProp))
                            {
                                string? urlValue = urlProp.GetString();
                                if (!string.IsNullOrEmpty(urlValue))
                                {
                                    _serverUrl = urlValue;
                                }
                            }
                            if (orchProp.TryGetProperty("AuthToken", out var tokenProp))
                            {
                                string? tokenValue = tokenProp.GetString();
                                if (!string.IsNullOrEmpty(tokenValue))
                                {
                                    _authToken = tokenValue;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Check Administrator Rights
            if (!CheckAdminRights())
            {
                AdminWarningOverlay.Visibility = Visibility.Visible;
                return;
            }

            // Setup Win32 Drag and Drop to bypass UIPI (Admin elevation drag-drop block)
            SetupDragAndDropBypass();

            // 2. Initialize and Connect to Orchestrator
            try
            {
                _orchestrator = new OrchestratorClient(_serverUrl, _authToken);
                _orchestrator.OnLog += Log;
                _orchestrator.OnSitesListUpdated += UpdateSitesList;
                _orchestrator.OnSessionTerminated += HandleSessionTerminated;
                _orchestrator.OnChatSystemStatusChanged += HandleChatSystemStatusChanged;
                _orchestrator.OnTunnelData += HandleTunnelData;

                await _orchestrator.ConnectAsync();

                // Initialize clipboard sync timer
                _clipboardTimer = new System.Windows.Threading.DispatcherTimer();
                _clipboardTimer.Interval = TimeSpan.FromSeconds(1);
                _clipboardTimer.Tick += ClipboardTimer_Tick;
                _clipboardTimer.Start();

                // Update server connection status UI
                Dispatcher.Invoke(() =>
                {
                    ServerStatusIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 124, 65)); // Green
                    ServerStatusText.Text = $"Sunucu: Bağlı ({_serverUrl})";
                });
            }
            catch (Exception ex)
            {
                Log($"[Socket] Sunucu bağlantı hatası: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    ServerStatusText.Text = "Sunucu: Bağlantı Başarısız";
                });
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _clipboardTimer?.Stop();
            if (_orchestrator != null)
            {
                _orchestrator.OnTunnelData -= HandleTunnelData;
            }
            CleanupActiveSession();
            _orchestrator?.DisconnectAsync();
        }

        private bool CheckAdminRights()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void CloseAppButton_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            if (_sidebarCollapsed)
            {
                // Expand
                SidebarColumn.MinWidth = 200;
                SidebarColumn.Width = new GridLength(_lastSidebarWidth);
                SidebarPanel.Visibility = Visibility.Visible;
                SidebarSplitter.Visibility = Visibility.Visible;
                ExpandSidebarBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Collapse
                _lastSidebarWidth = SidebarColumn.ActualWidth;
                SidebarColumn.MinWidth = 0;
                SidebarColumn.Width = new GridLength(0);
                SidebarPanel.Visibility = Visibility.Collapsed;
                SidebarSplitter.Visibility = Visibility.Collapsed;
                ExpandSidebarBtn.Visibility = Visibility.Visible;
            }
            _sidebarCollapsed = !_sidebarCollapsed;
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(Environment.NewLine + $"[{DateTime.Now:HH:mm:ss}] {message}");
                LogScrollViewer.ScrollToEnd();
            });

            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "master_debug.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private void UpdateSitesList(List<SiteInfo> activeSites)
        {
            Dispatcher.Invoke(() =>
            {
                Sites.Clear();
                foreach (var site in activeSites)
                {
                    Sites.Add(new SiteUI
                    {
                        Id = site.id,
                        Name = site.name,
                        Status = site.status,
                        RustDeskId = site.rustDeskId ?? "Yok",
                        RustDeskPassword = site.rustDeskPassword ?? "",
                        Country = site.location?.country ?? "",
                        City = site.location?.city ?? "",
                        Lat = site.location?.lat,
                        Lon = site.location?.lon,
                        Isp = site.location?.isp ?? ""
                    });
                }
            });
        }

        private async void SitesListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var selectedSite = SitesListBox.SelectedItem as SiteUI;
            if (selectedSite != null)
            {
                await StartConnectionAsync(selectedSite, enableTunnel: false, enableScreen: true);
            }
        }

        private async void MenuConnectScreenOnly_Click(object sender, RoutedEventArgs e)
        {
            var selectedSite = GetSiteFromContextMenu(sender);
            if (selectedSite != null)
            {
                await StartConnectionAsync(selectedSite, enableTunnel: false, enableScreen: true);
            }
        }

        private async void MenuConnectTunnelOnly_Click(object sender, RoutedEventArgs e)
        {
            var selectedSite = GetSiteFromContextMenu(sender);
            if (selectedSite != null)
            {
                await StartConnectionAsync(selectedSite, enableTunnel: true, enableScreen: false);
            }
        }

        private async void MenuConnectWithTunnel_Click(object sender, RoutedEventArgs e)
        {
            var selectedSite = GetSiteFromContextMenu(sender);
            if (selectedSite != null)
            {
                await StartConnectionAsync(selectedSite, enableTunnel: true, enableScreen: true);
            }
        }

        private async void MenuRenameSite_Click(object sender, RoutedEventArgs e)
        {
            var selectedSite = GetSiteFromContextMenu(sender);
            if (selectedSite != null)
            {
                var dialog = new RenameDialog(selectedSite.Name)
                {
                    Owner = this
                };

                if (dialog.ShowDialog() == true)
                {
                    string newName = dialog.NewName;
                    Log($"[Arayüz] Şantiye '{selectedSite.Name}' ismi '{newName}' olarak değiştiriliyor...");
                    await _orchestrator!.RenameSiteAsync(selectedSite.Id, newName);
                }
            }
        }

        private async void MenuDeleteSite_Click(object sender, RoutedEventArgs e)
        {
            var selectedSite = GetSiteFromContextMenu(sender);
            if (selectedSite != null)
            {
                var result = System.Windows.MessageBox.Show(
                    $"'{selectedSite.Name}' şantiyesini sistemden silmek istediğinize emin misiniz?\n\n" +
                    "Not: Eğer şantiye ajanı aktif ise ilk bağlantı denemesinde sistemde otomatik olarak yeniden oluşturulacaktır.",
                    "Şantiyeyi Sil",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Yes)
                {
                    Log($"[Arayüz] Şantiye '{selectedSite.Name}' sistemden siliniyor...");
                    await _orchestrator!.DeleteSiteAsync(selectedSite.Id);
                }
            }
        }

        private SiteUI? GetSiteFromContextMenu(object sender)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem?.Parent as ContextMenu;
            var target = contextMenu?.PlacementTarget as FrameworkElement;
            return target?.DataContext as SiteUI ?? SitesListBox.SelectedItem as SiteUI;
        }

        private async Task StartConnectionAsync(SiteUI selectedSite, bool enableTunnel, bool enableScreen)
        {
            if (_isConnecting || _activeSiteId != null) return;

            if (!selectedSite.IsOnline)
            {
                System.Windows.MessageBox.Show("Seçilen şantiye çevrimdışı. Bağlantı kurulamaz.", "Bağlantı Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isConnecting = true;
            _activeSiteId = selectedSite.Id;
            _activeEnableTunnel = enableTunnel;
            _activeEnableScreen = enableScreen;

            string modeLabel = (enableTunnel, enableScreen) switch
            {
                (true, true) => "Ekran + Tünel",
                (true, false) => "Sadece Tünel",
                (false, true) => "Sadece Ekran",
                _ => "Bilinmeyen Mod"
            };
            Log($"[Bağlantı] {selectedSite.Name} şantiyesine bağlanılıyor ({modeLabel})...");

            ActiveConnectionHeader.Text = $"{selectedSite.Name} Şantiyesine Bağlanılıyor...";
            ActiveConnectionSubheader.Text = $"{modeLabel} oturumu başlatılıyor, lütfen bekleyin...";

            try
            {
                // 1. Start Session on Orchestrator
                bool sessionStarted = await _orchestrator!.StartSessionAsync(selectedSite.Id);
                if (!sessionStarted)
                {
                    throw new Exception("Sunucu oturum başlatma isteğini reddetti veya zaman aşımına uğradı.");
                }

                Log("[Bağlantı] Sunucu el sıkışması tamamlandı.");

                // 2. Start tunnel if requested
                if (enableTunnel)
                {
                    Log("[Bağlantı] SOCKS5 ve Wintun ağ tüneli başlatılıyor...");
                    _socksServer = new LocalSocksServer(1080, _orchestrator, selectedSite.Id);
                    _socksServer.OnLog += Log;
                    _socksServer.Start();

                    _wintunManager = new WintunManager();
                    _wintunManager.OnLog += Log;
                    
                    bool wintunStarted = await _wintunManager.StartAsync();
                    if (!wintunStarted)
                    {
                        throw new Exception("Wintun sanal ağ tüneli başlatılamadı.");
                    }
                }

                // 3. Update UI Header status
                string statusText = (enableTunnel, enableScreen) switch
                {
                    (true, true) => "IP Tüneli Aktif (192.168.0.0/24) | Uzak Masaüstü Aktif",
                    (true, false) => "IP Tüneli Aktif (192.168.0.0/24) | Ekran Kapalı",
                    (false, true) => "Sadece Ekran Paylaşımı Aktif (Ağ Tüneli Kapalı)",
                    _ => ""
                };

                Dispatcher.Invoke(() =>
                {
                    ActiveConnectionHeader.Text = $"BAĞLI: {selectedSite.Name}";
                    ActiveConnectionSubheader.Text = statusText;
                    DisconnectButton.Visibility = Visibility.Visible;
                    SpeedTestButton.Visibility = Visibility.Visible;
                    QualityComboBox.Visibility = Visibility.Visible;
                    FullscreenButton.Visibility = Visibility.Visible;
                });

                // 4. Connect and Embed Custom Screen Share (only if screen requested)
                if (enableScreen)
                {
                    _screenViewer = new ScreenViewer(_orchestrator!, selectedSite.Id, (len, isUdp) => RecordFrameReceived(len, isUdp));
                    _screenViewer.OnFilesPasted += (files) =>
                    {
                        var list = new List<string>();
                        foreach (var file in files)
                        {
                            if (!string.IsNullOrEmpty(file))
                                list.Add(file);
                        }
                        if (list.Count > 0)
                        {
                            _ = Task.Run(async () => await SendFilesAsync(list));
                        }
                    };
                    RemoteDesktopContainer.Children.Add(_screenViewer);

                    // Switch to Remote Desktop Tab
                    MainTabControl.SelectedIndex = 1;
                    Log("[Bağlantı] Özel ekran paylaşımı ve kontrol modülü başlatıldı.");

                    // Start stats display timer
                    StartStatsTimer();

                    // Initialize UDP Hole Punching for P2P Screen Streaming
                    _ = InitUdpHolePunchingAsync(selectedSite.Id);
                }
                else
                {
                    // Tunnel-only mode: stay on dashboard tab, show log
                    Log("[Bağlantı] Sadece tünel modu aktif. TIA Portal veya CX-Programmer'dan 192.168.0.1 adresine bağlanabilirsiniz.");
                }
            }
            catch (Exception ex)
            {
                Log($"[Bağlantı] HATA: {ex.Message}");
                System.Windows.MessageBox.Show($"Bağlantı sırasında bir hata oluştu:\n{ex.Message}", "Bağlantı Başarısız", MessageBoxButton.OK, MessageBoxImage.Error);
                CleanupActiveSession();
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSiteId != null)
            {
                Log("[Bağlantı] Kullanıcı oturumu kapatmak istedi.");
                if (_orchestrator != null)
                {
                    try
                    {
                        await _orchestrator.StopSessionAsync(_activeSiteId);
                    }
                    catch { }
                }
                CleanupActiveSession();
            }
        }

        private void HandleSessionTerminated(string reason)
        {
            Log($"[Bağlantı] Uzak oturum sunucu tarafından sonlandırıldı. Gerekçe: {reason}");
            CleanupActiveSession();
        }

        private void CleanupActiveSession()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.Thread.ThreadState == System.Threading.ThreadState.Stopped)
            {
                RunCleanup();
                return;
            }

            try
            {
                Dispatcher.Invoke(() =>
                {
                    RunCleanup();
                });
            }
            catch { }
        }

        private void RunCleanup()
        {
            try
            {
                ActiveConnectionHeader.Text = "Boşta";
                ActiveConnectionSubheader.Text = "Bağlanmak için bir şantiye seçin";
                DisconnectButton.Visibility = Visibility.Collapsed;
                SpeedTestButton.Visibility = Visibility.Collapsed;
                QualityComboBox.Visibility = Visibility.Collapsed;
                FullscreenButton.Visibility = Visibility.Collapsed;
                
                // Stop stats timer
                StopStatsTimer();
                
                // Clear custom screen share viewer
                if (_screenViewer != null)
                {
                    _ = _screenViewer.StopScreenShareAsync();
                    _screenViewer = null;
                }

                // Cleanup UDP Hole Punching resources
                CleanupUdpHolePunching();



                RemoteDesktopContainer.Children.Clear();

                // Close Wintun Adapter
                _wintunManager?.Stop();
                _wintunManager = null;

                // Stop SOCKS5 Proxy Server
                _socksServer?.Stop();
                _socksServer = null;

                _activeSiteId = null;
                MainTabControl.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                try { Log($"[Sistem] Oturum kapatılırken hata oluştu: {ex.Message}"); } catch { }
            }
        }

        private void HandleChatSystemStatusChanged(bool enabled)
        {
            Dispatcher.Invoke(() =>
            {
                // Görüşme sistemi arayüzden kaldırıldığı için bu durumu loglamıyoruz
                // Log($"[Arayüz] Web Görüşme Sistemi durumu güncellendi: {(enabled ? "AKTİF" : "PASİF")}");
            });
        }



        #region Win32 Drag-and-Drop Bypass for UIPI

        [DllImport("ole32.dll")]
        private static extern int RevokeDragDrop(IntPtr hwnd);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, [Out] StringBuilder? lpszFile, uint cch);

        [DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr hDrop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

        private const uint WM_DROPFILES = 0x0233;
        private const uint MSGFLT_ADD = 1;

        private void SetupDragAndDropBypass()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    Log("[Dosya Aktarımı] Pencere Handle'ı bulunamadı. Sürükle-bırak devre dışı.");
                    return;
                }

                // Revoke WPF's default OLE drag-drop registration to allow WM_DROPFILES
                try
                {
                    RevokeDragDrop(hwnd);
                }
                catch { }

                // Register window to accept drops via old-style API
                DragAcceptFiles(hwnd, true);

                // Allow drop and copy data messages through UIPI filter
                ChangeWindowMessageFilter(WM_DROPFILES, MSGFLT_ADD);
                ChangeWindowMessageFilter(0x0049, MSGFLT_ADD); // WM_COPYGLOBALDATA
                ChangeWindowMessageFilter(0x004A, MSGFLT_ADD); // WM_COPYDATA

                // Hook WndProc to intercept WM_DROPFILES
                var hwndSource = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                hwndSource?.AddHook(HwndMessageHook);

                Log("[Dosya Aktarımı] Yönetici engeli aşma (UIPI Bypass) aktifleştirildi.");
            }
            catch (Exception ex)
            {
                Log($"[Dosya Aktarımı] UIPI Bypass kurulum hatası: {ex.Message}");
            }
        }

        private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)WM_DROPFILES)
            {
                HandleDropFiles(wParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void HandleDropFiles(IntPtr hDrop)
        {
            try
            {
                uint fileCount = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                List<string> filePaths = new List<string>();
                for (uint i = 0; i < fileCount; i++)
                {
                    StringBuilder sb = new StringBuilder(1024);
                    if (DragQueryFile(hDrop, i, sb, (uint)sb.Capacity) > 0)
                    {
                        filePaths.Add(sb.ToString());
                    }
                }
                DragFinish(hDrop);

                if (filePaths.Count > 0)
                {
                    if (_activeSiteId != null && _screenViewer != null && MainTabControl.SelectedIndex == 1)
                    {
                        _ = Task.Run(async () => await SendFilesAsync(filePaths));
                    }
                    else
                    {
                        Log("[Dosya Aktarımı] Aktif bir uzak bağlantı oturumu (RemoteDesktop) olmadığından dosya gönderilmedi.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Dosya Aktarımı] Dosya okuma hatası: {ex.Message}");
            }
        }

        private async Task SendFilesAsync(List<string> filePaths)
        {
            foreach (var filePath in filePaths)
            {
                bool isFolder = Directory.Exists(filePath);
                if (!File.Exists(filePath) && !isFolder) continue;

                string actualFilePath = filePath;
                string tempZipPath = null;

                try
                {
                    string fileName = Path.GetFileName(filePath);
                    long fileSize = 0;

                    if (isFolder)
                    {
                        Log($"[Dosya Aktarımı] '{fileName}' klasörü sıkıştırılıyor...");
                        fileName = fileName + ".zip";
                        tempZipPath = Path.Combine(Path.GetTempPath(), fileName + "_" + Path.GetRandomFileName() + ".zip");
                        ZipFile.CreateFromDirectory(filePath, tempZipPath);
                        actualFilePath = tempZipPath;
                        fileSize = new FileInfo(tempZipPath).Length;
                    }
                    else
                    {
                        fileSize = new FileInfo(filePath).Length;
                    }

                    Log($"[Dosya Aktarımı] '{fileName}' gönderiliyor ({fileSize / (1024.0 * 1024.0):F2} MB)...");

                    // Show progress UI on UI thread
                    Dispatcher.Invoke(() =>
                    {
                        FileTransferProgressBar.Value = 0;
                        FileTransferFileNameText.Text = $"{fileName} ({fileSize / (1024.0 * 1024.0):F2} MB)";
                        FileTransferStatsText.Text = "Hız: Hesaplanıyor... | Kalan Süre: Hesaplanıyor...";
                        FileTransferOverlay.Visibility = Visibility.Visible;
                    });

                    // 1. Send start chunk
                    var startPayload = new
                    {
                        type = "start",
                        fileName = fileName,
                        fileSize = fileSize,
                        isFolder = isFolder
                    };
                    await SendFileTransferPayloadAsync(startPayload);

                    // 2. Send file chunks
                    long totalBytesSent = 0;
                    var startTime = DateTime.Now;

                    using (var fs = new FileStream(actualFilePath, FileMode.Open, FileAccess.Read))
                    {
                        byte[] buffer = new byte[64 * 1024]; // 64KB chunks
                        int bytesRead;
                        while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            byte[] actualBytes = new byte[bytesRead];
                            Array.Copy(buffer, actualBytes, bytesRead);

                            _fileChunkAckTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                            var chunkPayload = new
                            {
                                type = "chunk",
                                data = Convert.ToBase64String(actualBytes)
                            };
                            await SendFileTransferPayloadAsync(chunkPayload);
                            totalBytesSent += bytesRead;

                            // Calculate statistics
                            var elapsed = (DateTime.Now - startTime).TotalSeconds;
                            double speed = elapsed > 0 ? (totalBytesSent / 1024.0) / elapsed : 0;
                            double remainingBytes = fileSize - totalBytesSent;
                            double remainingSeconds = speed > 0 ? (remainingBytes / 1024.0) / speed : 0;

                            Dispatcher.Invoke(() =>
                            {
                                FileTransferProgressBar.Value = ((double)totalBytesSent / fileSize) * 100;
                                string speedStr = speed > 1024 ? $"{speed / 1024.0:F1} MB/s" : $"{speed:F1} KB/s";
                                FileTransferStatsText.Text = $"Hız: {speedStr} | {totalBytesSent / (1024.0 * 1024.0):F2} / {fileSize / (1024.0 * 1024.0):F2} MB";
                            });

                            // Wait for ACK from remote Agent with a 5 second safety timeout
                            var ackTask = _fileChunkAckTcs.Task;
                            var timeoutTask = Task.Delay(5000);
                            var completedTask = await Task.WhenAny(ackTask, timeoutTask);

                            if (completedTask == timeoutTask)
                            {
                                Log("[Dosya Aktarımı] HATA: Karşı bilgisayardan onay alınamadı (zaman aşımı). Gönderim durduruluyor.");
                                break;
                            }
                        }
                    }

                    // 3. Send end chunk
                    var endPayload = new
                    {
                        type = "end"
                    };
                    await SendFileTransferPayloadAsync(endPayload);

                    Log($"[Dosya Aktarımı] '{fileName}' başarıyla gönderildi.");
                }
                catch (Exception ex)
                {
                    Log($"[Dosya Aktarımı] '{Path.GetFileName(filePath)}' gönderilemedi: {ex.Message}");
                }
                finally
                {
                    // Hide progress UI
                    Dispatcher.Invoke(() =>
                    {
                        FileTransferOverlay.Visibility = Visibility.Collapsed;
                    });

                    // Clean up temp zip file if created
                    if (tempZipPath != null && File.Exists(tempZipPath))
                    {
                        try { File.Delete(tempZipPath); } catch { }
                    }
                }
            }
        }

        private async Task SendFileTransferPayloadAsync(object payload)
        {
            if (_orchestrator == null || _activeSiteId == null) return;
            string json = System.Text.Json.JsonSerializer.Serialize(payload);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await _orchestrator.SendTunnelDataAsync(_activeSiteId, "file-transfer", bytes);
        }

        private async void ClipboardTimer_Tick(object? sender, EventArgs e)
        {
            if (_activeSiteId == null || _screenViewer == null || _orchestrator == null) return;

            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string currentText = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrEmpty(currentText) && currentText != _lastClipboardText)
                    {
                        _lastClipboardText = currentText;

                        var payload = new
                        {
                            type = "clipboard",
                            text = currentText
                        };
                        
                        string json = System.Text.Json.JsonSerializer.Serialize(payload);
                        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                        await _orchestrator.SendTunnelDataAsync(_activeSiteId, "clipboard-sync", bytes);
                    }
                }
            }
            catch { }
        }

        private void HandleTunnelData(string connectionId, byte[] data)
        {
            if (connectionId == "clipboard-sync")
            {
                try
                {
                    string json = System.Text.Encoding.UTF8.GetString(data);
                    using (var doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        string type = root.GetProperty("type").GetString() ?? "";
                        if (type == "clipboard")
                        {
                            string text = root.GetProperty("text").GetString() ?? "";
                            if (!string.IsNullOrEmpty(text) && text != _lastClipboardText)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    try
                                    {
                                        _lastClipboardText = text;
                                        System.Windows.Clipboard.SetText(text);
                                        Log("[Pano] Uzak bilgisayardan kopyalanan metin panoya alındı.");
                                    }
                                    catch { }
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Pano] Uzak pano verisi işlenirken hata oluştu: {ex.Message}");
                }
            }
            else if (connectionId == "udp-holepunch-ack")
            {
                try
                {
                    string json = Encoding.UTF8.GetString(data);
                    using (var doc = System.Text.Json.JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        string type = root.GetProperty("type").GetString() ?? "";
                        if (type == "ack")
                        {
                            string endpointStr = root.GetProperty("endpoint").GetString() ?? "";
                            if (IPEndPoint.TryParse(endpointStr, out IPEndPoint? agentEP))
                            {
                                Log($"[P2P] Ajan dış adresi alındı: {agentEP}. Delik açma başlatılıyor...");
                                if (_udpCts != null)
                                {
                                    _ = Task.Run(() => PunchLoopAsync(agentEP, _udpCts.Token));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[P2P] El sıkışma verisi işlenirken hata: {ex.Message}");
                }
            }
            else if (connectionId == "file-transfer-ack")
            {
                _fileChunkAckTcs?.TrySetResult(true);
            }
            else if (connectionId == "connection-speed-test")
            {
                HandleSpeedTestResponse(data);
            }
        }

        #region UDP Hole Punching & P2P Stream

        private async Task InitUdpHolePunchingAsync(string siteId)
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
                    Log($"[P2P] Yerel port ayrıldı: {localPort}. STUN sorgusu yapılıyor...");
                }
                else
                {
                    Log("[P2P] HATA: Yerel uç nokta IPEndPoint türünde değil.");
                    return;
                }

                IPEndPoint? myPublicEP = await Task.Run(() => GetPublicIP(_udpClient));
                if (myPublicEP == null)
                {
                    Log("[P2P] STUN sunucusundan dış IP adresi alınamadı. Standart sunucu akışı kullanılacak.");
                    return;
                }

                Log($"[P2P] Dış IP ve Port tespit edildi: {myPublicEP}. Ajan ile el sıkışılıyor...");

                _ = Task.Run(() => ReceiveUdpPacketsAsync(_udpCts.Token));

                var initPayload = new
                {
                    type = "init",
                    endpoint = myPublicEP.ToString()
                };
                string json = System.Text.Json.JsonSerializer.Serialize(initPayload);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _orchestrator!.SendTunnelDataAsync(siteId, "udp-holepunch-init", bytes);
            }
            catch (Exception ex)
            {
                Log($"[P2P] HATA: P2P başlatma hatası: {ex.Message}");
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
                Log($"[P2P] STUN sunucusu sorgulanıyor: {stun.Host}:{stun.Port}...");
                try
                {
                    IPAddress[] ips = Dns.GetHostAddresses(stun.Host);
                    if (ips.Length == 0)
                    {
                        Log($"[P2P] STUN hatası ({stun.Host}): DNS çözümlenemedi.");
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
                                Log($"[P2P] STUN Başarılı ({stun.Host}): Dış adres = {publicEP}");
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
                                Log($"[P2P] STUN Başarılı ({stun.Host}): Dış adres (XOR) = {publicEP}");
                                return publicEP;
                            }
                            i += 4 + attrLen;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[P2P] STUN zaman aşımı veya hata ({stun.Host}): {ex.Message}");
                }
            }
            return null;
        }

        public void RecordFrameReceived(int byteCount, bool isUdp)
        {
            _fpsCounter++;
            _bytesCounter += byteCount;
            _streamType = isUdp ? "Direct P2P (UDP)" : "Bulut Sunucu (TCP)";
        }

        private void StartStatsTimer()
        {
            StopStatsTimer();

            _fpsCounter = 0;
            _bytesCounter = 0;
            _streamType = "Bağlanıyor...";

            _statsTimer = new System.Windows.Threading.DispatcherTimer();
            _statsTimer.Interval = TimeSpan.FromSeconds(1);
            _statsTimer.Tick += (s, e) =>
            {
                if (_activeSiteId == null)
                {
                    StopStatsTimer();
                    return;
                }

                double speedKb = _bytesCounter / 1024.0;
                string speedStr = speedKb > 1024 ? $"{speedKb / 1024.0:F1} MB/s" : $"{speedKb:F1} KB/s";
                int fps = _fpsCounter;

                // Reset counters
                _fpsCounter = 0;
                _bytesCounter = 0;

                string connectionInfo = $"[{_streamType} | FPS: {fps} | Ekran Veri Hızı: {speedStr}]";
                
                string baseText = (_activeEnableTunnel, _activeEnableScreen) switch
                {
                    (true, true) => "IP Tüneli Aktif (192.168.0.0/24) | Uzak Masaüstü",
                    (true, false) => "IP Tüneli Aktif (192.168.0.0/24) | Ekran Kapalı",
                    (false, true) => "Sadece Ekran Paylaşımı (Ağ Tüneli Kapalı)",
                    _ => "Bağlantı Aktif"
                };

                if (_activeEnableScreen)
                {
                    ActiveConnectionSubheader.Text = $"{baseText} {connectionInfo}";
                }
                else
                {
                    ActiveConnectionSubheader.Text = baseText;
                }
            };
            _statsTimer.Start();
        }

        private void StopStatsTimer()
        {
            if (_statsTimer != null)
            {
                _statsTimer.Stop();
                _statsTimer = null;
            }
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
                            Log($"[P2P] Bağlantı başarıyla kuruldu! Karşı IP: {remoteEP}");
                            if (_screenViewer != null)
                            {
                                _screenViewer.BypassSocketFrames = true;
                            }
                        }
                    }
                    else if (data.Length == 2 && Encoding.UTF8.GetString(data) == "OK")
                    {
                        if (!_p2pConnected)
                        {
                            _p2pConnected = true;
                            _remoteUdpEP = remoteEP;
                            Log($"[P2P] Bağlantı başarıyla kuruldu! Karşı IP: {remoteEP}");
                            if (_screenViewer != null)
                            {
                                _screenViewer.BypassSocketFrames = true;
                            }
                        }
                    }
                    else if (data.Length > 0 && _p2pConnected)
                    {
                        if (_screenViewer != null)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                _screenViewer?.UpdateFrame(data);
                            });
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Ignore transient exceptions
                }
            }
        }

        private async Task PunchLoopAsync(IPEndPoint remoteEP, CancellationToken token)
        {
            if (_udpClient == null) return;

            byte[] punchBytes = Encoding.UTF8.GetBytes("PUNCH");
            Log($"[P2P] Ajan adresine delik açılıyor: {remoteEP}...");

            for (int i = 0; i < 50 && !token.IsCancellationRequested && !_p2pConnected; i++)
            {
                try
                {
                    await _udpClient.SendAsync(punchBytes, punchBytes.Length, remoteEP);
                }
                catch { }
                await Task.Delay(100, token);
            }

            if (!_p2pConnected && !token.IsCancellationRequested)
            {
                Log("[P2P] P2P delik açma zaman aşımına uğradı. Standart sunucu akışı ile devam ediliyor.");
            }
        }

        #endregion

        #endregion

        #region Diagnostic Connection Speed Test

        private int _masterSpeedTestPing = 0;
        private TaskCompletionSource<string>? _speedTestTcs;

        private async void SpeedTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSiteId == null || _isConnecting) return;

            SpeedTestButton.IsEnabled = false;
            SpeedTestButton.Content = "Ölçülüyor...";
            Log("[Hız Testi] Sunucu ve bağlantı gecikme ölçümü başlatıldı...");

            try
            {
                _speedTestTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                // 1. Measure Master Ping to Server
                var startTime = DateTime.Now;
                int masterPing = 0;
                try
                {
                    using (var httpClient = new System.Net.Http.HttpClient())
                    {
                        httpClient.Timeout = TimeSpan.FromMilliseconds(2000);
                        var resp = await httpClient.GetAsync(_serverUrl);
                        masterPing = (int)(DateTime.Now - startTime).TotalMilliseconds;
                    }
                }
                catch
                {
                    masterPing = -1;
                }
                _masterSpeedTestPing = masterPing;

                // 2. Request Agent Ping
                var pingRequest = new 
                { 
                    type = "ping",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                await SendSpeedTestPayloadAsync(pingRequest);

                // Wait for the result (8 second safety timeout)
                var resultTask = _speedTestTcs.Task;
                var timeoutTask = Task.Delay(8000);
                var completedTask = await Task.WhenAny(resultTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Log("[Hız Testi] HATA: Ajan bilgisayardan yanıt alınamadı (zaman aşımı).");
                    System.Windows.MessageBox.Show("Uzak ajan bilgisayarından hız testi yanıtı alınamadı.", "Hız Testi Başarısız", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    string report = await resultTask;
                    System.Windows.MessageBox.Show(report, "Hız Testi Sonuçları", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log($"[Hız Testi] HATA: Test gerçekleştirilemedi: {ex.Message}");
            }
            finally
            {
                SpeedTestButton.IsEnabled = true;
                SpeedTestButton.Content = "Hız Testi Yap";
            }
        }

        private async void HandleSpeedTestResponse(byte[] data)
        {
            try
            {
                string json = Encoding.UTF8.GetString(data);
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString() ?? "";

                    if (type == "pong")
                    {
                        long sendTimestamp = root.GetProperty("timestamp").GetInt64();
                        int agentPing = root.GetProperty("agentPing").GetInt32();
                        
                        long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        long rtt = currentTimestamp - sendTimestamp;

                        string masterPingStr = _masterSpeedTestPing == -1 ? "Zaman Aşımı / Hata" : $"{_masterSpeedTestPing} ms";
                        string agentPingStr = agentPing == -1 ? "Zaman Aşımı / Hata" : $"{agentPing} ms";

                        Log($"[Hız Testi] Master -> Sunucu Gecikmesi: {masterPingStr}");
                        Log($"[Hız Testi] Ajan -> Sunucu Gecikmesi: {agentPingStr}");
                        Log($"[Hız Testi] Master -> Sunucu -> Ajan Toplam Döngü (RTT): {rtt} ms");

                        string pingDiagnostic = "";
                        if (_masterSpeedTestPing > 200 || agentPing > 200)
                        {
                            pingDiagnostic = "Açıklama: Bağlantı gecikmesi oldukça YÜKSEK. Sunucu konumu çok uzak (ABD) veya internet hattınız yoğun. Bu durum ekran donmasına ve yavaşlığa yol açar.";
                        }
                        else if (_masterSpeedTestPing > 100 || agentPing > 100)
                        {
                            pingDiagnostic = "Açıklama: Bağlantı gecikmesi ORTA seviyede. Akıcı kontrol mümkündür ancak hafif bir mouse gecikmesi hissedilebilir.";
                        }
                        else
                        {
                            pingDiagnostic = "Açıklama: Bağlantı gecikmesi DÜŞÜK (İyi). Bağlantınızın çok akıcı çalışması gerekir.";
                        }

                        string resultReport = $"=== HIZ VE GECİKME TESTİ SONUÇLARI ===\n\n" +
                                             $"Local Master -> Sunucu Latency : {masterPingStr}\n" +
                                             $"Uzak Ajan -> Sunucu Latency : {agentPingStr}\n" +
                                             $"Master -> Ajan Toplam RTT : {rtt} ms\n\n" +
                                             $"{pingDiagnostic}\n\n" +
                                             $"Donmanın hangi tarafın internetinden kaynaklandığını anlamak için yukarıdaki Ping değerlerini kontrol edin. Yüksek Ping olan taraf bağlantıda yavaşlığa neden olmaktadır.";

                        _speedTestTcs?.TrySetResult(resultReport);
                    }
                }
            }
            catch (Exception ex)
            {
                _speedTestTcs?.TrySetException(ex);
            }
        }

        private async Task SendSpeedTestPayloadAsync(object payload)
        {
            if (_orchestrator == null || _activeSiteId == null) return;
            string json = System.Text.Json.JsonSerializer.Serialize(payload);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await _orchestrator.SendTunnelDataAsync(_activeSiteId, "connection-speed-test", bytes);
        }

        #endregion

        #region Screen Quality & Fullscreen Modes

        private bool _isFullscreen = false;
        private WindowStyle _savedWindowStyle;
        private ResizeMode _savedResizeMode;
        private WindowState _savedWindowState;

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape && _isFullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreen();
        }

        private void ToggleFullscreen()
        {
            if (!_isFullscreen)
            {
                // Save current state
                _savedWindowStyle = WindowStyle;
                _savedResizeMode = ResizeMode;
                _savedWindowState = WindowState;

                // Go Fullscreen
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;

                // Hide sidebar if visible
                if (!_sidebarCollapsed)
                {
                    ToggleSidebar_Click(this, new RoutedEventArgs());
                }

                FullscreenButton.Content = "Küçült";
                _isFullscreen = true;
                Log("[Arayüz] Tam ekran moduna geçildi. Çıkmak için ESC tuşuna basabilirsiniz.");
            }
            else
            {
                // Restore state
                WindowStyle = _savedWindowStyle;
                ResizeMode = _savedResizeMode;
                WindowState = _savedWindowState;

                // Show sidebar if it was open
                if (_sidebarCollapsed)
                {
                    ToggleSidebar_Click(this, new RoutedEventArgs());
                }

                FullscreenButton.Content = "Tam Ekran";
                _isFullscreen = false;
                Log("[Arayüz] Normal ekran moduna dönüldü.");
            }
        }

        private void QualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_activeSiteId == null || _orchestrator == null || QualityComboBox == null || QualityComboBox.SelectedItem == null) return;

            var selectedItem = (ComboBoxItem)QualityComboBox.SelectedItem;
            string tag = selectedItem.Tag?.ToString() ?? "medium";

            int maxWidth = 1024;
            int quality = 35;

            switch (tag)
            {
                case "low":
                    maxWidth = 800;
                    quality = 25;
                    break;
                case "medium":
                    maxWidth = 1024;
                    quality = 35;
                    break;
                case "high":
                    maxWidth = 1280;
                    quality = 50;
                    break;
                case "ultra":
                    maxWidth = 1920;
                    quality = 70;
                    break;
            }

            _ = SendScreenQualitySettingsAsync(maxWidth, quality);
        }

        private async Task SendScreenQualitySettingsAsync(int maxWidth, int quality)
        {
            if (_orchestrator == null || _activeSiteId == null) return;
            try
            {
                var payload = new { maxWidth = maxWidth, quality = quality };
                string json = System.Text.Json.JsonSerializer.Serialize(payload);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                await _orchestrator.SendTunnelDataAsync(_activeSiteId, "screen-share-quality", bytes);
                Log($"[Görüntü Ayarı] Çözünürlük ve kalite güncellendi: {maxWidth}px, %{quality}");
            }
            catch (Exception ex)
            {
                Log($"[Görüntü Ayarı] Hata: Ayarlar gönderilemedi: {ex.Message}");
            }
        }

        #endregion
    }
}
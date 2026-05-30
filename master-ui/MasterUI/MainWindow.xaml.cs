using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MasterUI.Controls;
using MasterUI.Services;
using MasterUI.Models;

namespace MasterUI
{
    public partial class MainWindow : Window
    {
        private OrchestratorClient? _orchestrator;
        private LocalSocksServer? _socksServer;
        private WintunManager? _wintunManager;
        private RustDeskHost? _rustDeskHost;
        
        private readonly string _serverUrl = "http://localhost:3000";
        private readonly string _authToken = "BarisServis2026!";
        private string? _activeSiteId;
        private bool _isConnecting = false;

        public ObservableCollection<SiteUI> Sites { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            SitesListBox.ItemsSource = Sites;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Check Administrator Rights
            if (!CheckAdminRights())
            {
                AdminWarningOverlay.Visibility = Visibility.Visible;
                return;
            }

            // 2. Initialize and Connect to Orchestrator
            try
            {
                _orchestrator = new OrchestratorClient(_serverUrl, _authToken);
                _orchestrator.OnLog += Log;
                _orchestrator.OnSitesListUpdated += UpdateSitesList;
                _orchestrator.OnSessionTerminated += HandleSessionTerminated;

                await _orchestrator.ConnectAsync();

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

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(Environment.NewLine + $"[{DateTime.Now:HH:mm:ss}] {message}");
                LogScrollViewer.ScrollToEnd();
            });
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
                        RustDeskPassword = site.rustDeskPassword ?? ""
                    });
                }
            });
        }

        private async void SitesListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isConnecting || _activeSiteId != null) return;

            var selectedSite = SitesListBox.SelectedItem as SiteUI;
            if (selectedSite == null) return;

            if (!selectedSite.IsOnline)
            {
                System.Windows.MessageBox.Show("Seçilen şantiye çevrimdışı. Bağlantı kurulamaz.", "Bağlantı Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isConnecting = true;
            _activeSiteId = selectedSite.Id;
            Log($"[Bağlantı] {selectedSite.Name} şantiyesine bağlanılıyor...");

            ActiveConnectionHeader.Text = $"{selectedSite.Name} Şantiyesine Bağlanılıyor...";
            ActiveConnectionSubheader.Text = "Tünel ve uzak masaüstü oturumu başlatılıyor, lütfen bekleyin...";

            try
            {
                // 1. Start Session on Orchestrator
                bool sessionStarted = await _orchestrator!.StartSessionAsync(selectedSite.Id);
                if (!sessionStarted)
                {
                    throw new Exception("Sunucu oturum başlatma isteğini reddetti veya zaman aşımına uğradı.");
                }

                Log("[Bağlantı] Sunucu el sıkışması tamamlandı. SOCKS5 ve ağ tüneli başlatılıyor...");

                // 2. Start Local SOCKS5 Proxy
                _socksServer = new LocalSocksServer(1080, _orchestrator, selectedSite.Id);
                _socksServer.OnLog += Log;
                _socksServer.Start();

                // 3. Start Wintun / tun2socks Routing
                _wintunManager = new WintunManager();
                _wintunManager.OnLog += Log;
                
                bool wintunStarted = await _wintunManager.StartAsync();
                if (!wintunStarted)
                {
                    throw new Exception("Wintun sanal ağ tüneli başlatılamadı.");
                }

                // 4. Update UI Header status
                Dispatcher.Invoke(() =>
                {
                    ActiveConnectionHeader.Text = $"BAĞLI: {selectedSite.Name}";
                    ActiveConnectionSubheader.Text = $"IP Tüneli Aktif (192.168.0.1) | SOCKS5 Tüneli Aktif (Port 1080)";
                    DisconnectButton.Visibility = Visibility.Visible;
                });

                // 5. Connect and Embed RustDesk Client
                _rustDeskHost = new RustDeskHost();
                _rustDeskHost.OnLog += Log;
                RemoteDesktopContainer.Children.Add(_rustDeskHost);

                // Switch to Remote Desktop Tab
                MainTabControl.SelectedIndex = 1;

                bool rustDeskConnected = await _rustDeskHost.ConnectAndEmbedAsync(selectedSite.RustDeskId, selectedSite.RustDeskPassword);
                if (!rustDeskConnected)
                {
                    Log("[RustDeskHost] WARNING: Gömülü uzak masaüstü bağlantısı kurulamadı. Ekranı yenilemek için tüneli açık tutabilirsiniz.");
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
                // Inform server we are closing the session
                await _orchestrator!.StopSessionAsync(_activeSiteId);
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
            Dispatcher.Invoke(() =>
            {
                ActiveConnectionHeader.Text = "Tünel ve Uzak Masaüstü Boşta";
                ActiveConnectionSubheader.Text = "Bağlantı kurmak için sol listeden aktif bir şantiyeye çift tıklayın.";
                DisconnectButton.Visibility = Visibility.Collapsed;
                
                // Clear RustDesk window
                if (_rustDeskHost != null)
                {
                    _rustDeskHost.CloseConnection();
                    RemoteDesktopContainer.Children.Clear();
                    _rustDeskHost = null;
                }

                // Close Wintun Adapter
                _wintunManager?.Stop();
                _wintunManager = null;

                // Stop SOCKS5 Proxy Server
                _socksServer?.Stop();
                _socksServer = null;

                _activeSiteId = null;
                MainTabControl.SelectedIndex = 0;
            });
        }
    }
}
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
        
        private readonly string _serverUrl = "https://remote.barisbd.tr";
        private readonly string _authToken = "BarisServis2026!";
        private string? _activeSiteId;
        private bool _isConnecting = false;
        private double _lastSidebarWidth = 280;
        private bool _sidebarCollapsed = false;

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
                });

                // 4. Connect and Embed RustDesk Client (only if screen requested)
                if (enableScreen)
                {
                    _rustDeskHost = new RustDeskHost();
                    _rustDeskHost.OnLog += Log;
                    RemoteDesktopContainer.Children.Add(_rustDeskHost);

                    // Switch to Remote Desktop Tab
                    MainTabControl.SelectedIndex = 1;

                    bool rustDeskConnected = await _rustDeskHost.ConnectAndEmbedAsync(selectedSite.RustDeskId, selectedSite.RustDeskPassword);
                    if (!rustDeskConnected)
                    {
                        Log("[RustDeskHost] WARNING: Gömülü uzak masaüstü bağlantısı kurulamadı.");
                    }
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
                ActiveConnectionHeader.Text = "Boşta";
                ActiveConnectionSubheader.Text = "Bağlanmak için bir şantiye seçin";
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
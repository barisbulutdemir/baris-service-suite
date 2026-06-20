using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Agent.Service.Services;

namespace Agent.Service
{
    public class AgentTrayContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly SocketClient _socketClient;
        private readonly CancellationTokenSource _cts;
        private readonly IHost _host;
        private readonly Task _hostTask;
        private StatusForm? _statusForm;

        public AgentTrayContext(SocketClient socketClient, CancellationTokenSource cts, IHost host, Task hostTask)
        {
            _socketClient = socketClient;
            _cts = cts;
            _host = host;
            _hostTask = hostTask;

            // Context Menu
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Durumu Göster", null, OnShowStatus);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Çıkış", null, OnExit);

            // Tray Icon Setup
            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Tech Service Agent",
                ContextMenuStrip = contextMenu,
                Visible = true
            };

            _trayIcon.DoubleClick += OnShowStatus;

            _trayIcon.BalloonTipTitle = "Tech Service Agent";
            _trayIcon.BalloonTipText = "Ajan başarıyla başlatıldı ve sistem tepsisinde arka planda çalışıyor.";
            _trayIcon.ShowBalloonTip(3000);
        }

        private void OnShowStatus(object? sender, EventArgs e)
        {
            if (_statusForm == null || _statusForm.IsDisposed)
            {
                _statusForm = new StatusForm(_socketClient);
            }
            _statusForm.Show();
            _statusForm.WindowState = FormWindowState.Normal;
            _statusForm.Activate();
        }

        private void OnExit(object? sender, EventArgs e)
        {
            _trayIcon.Visible = false;
            
            try
            {
                _statusForm?.Close();
            }
            catch { }

            // Terminate background Host safely
            _cts.Cancel();
            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(3)).Wait();
                _hostTask.Wait();
            }
            catch { }
            finally
            {
                _host.Dispose();
            }

            Application.Exit();
        }
    }
}

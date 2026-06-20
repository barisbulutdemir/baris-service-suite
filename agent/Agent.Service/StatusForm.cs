using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Agent.Service.Services;

namespace Agent.Service
{
    public class StatusForm : Form
    {
        private readonly SocketClient _socketClient;
        private readonly Label _lblId;
        private readonly Label _lblName;
        private readonly Label _lblStatus;
        private readonly Label _lblLocation;
        private readonly TextBox _txtLog;
        private readonly System.Windows.Forms.Timer _updateTimer;

        public StatusForm(SocketClient socketClient)
        {
            _socketClient = socketClient;

            // Form properties
            this.Text = "Servis Ajanı Durum Bilgisi";
            this.Size = new Size(580, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.FromArgb(245, 246, 247);

            // Title
            var lblTitle = new Label
            {
                Text = "Servis Ajanı Durum Bilgisi",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                Location = new Point(15, 15),
                Size = new Size(400, 25),
                ForeColor = Color.FromArgb(0, 122, 204)
            };
            this.Controls.Add(lblTitle);

            // GroupBox for details
            var grpDetails = new GroupBox
            {
                Text = "Sistem Detayları",
                Location = new Point(15, 45),
                Size = new Size(535, 130),
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            this.Controls.Add(grpDetails);

            // Labels inside GroupBox
            _lblId = CreateLabel(25, "Şantiye ID: ", grpDetails);
            _lblName = CreateLabel(50, "Şantiye İsmi: ", grpDetails);
            _lblStatus = CreateLabel(75, "Bağlantı Durumu: ", grpDetails);
            _lblLocation = CreateLabel(100, "Lokasyon: ", grpDetails);

            // Log header
            var lblLogHeader = new Label
            {
                Text = "Sistem Logları (Son Olaylar):",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Location = new Point(15, 185),
                Size = new Size(200, 20)
            };
            this.Controls.Add(lblLogHeader);

            // Log TextBox
            _txtLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(15, 205),
                Size = new Size(535, 185),
                Font = new Font("Consolas", 8.5f),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(30, 30, 30)
            };
            this.Controls.Add(_txtLog);

            // Close button
            var btnClose = new Button
            {
                Text = "Kapat",
                Location = new Point(475, 400),
                Size = new Size(75, 28),
                Font = new Font("Segoe UI", 9),
                DialogResult = DialogResult.OK
            };
            btnClose.Click += (s, e) => this.Close();
            this.Controls.Add(btnClose);

            // Timer for status update
            _updateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _updateTimer.Tick += (s, e) => UpdateStatus();
            _updateTimer.Start();

            // Events
            AgentLogger.OnLog += AppendLog;
            this.FormClosing += (s, e) => {
                AgentLogger.OnLog -= AppendLog;
                _updateTimer.Stop();
            };

            // Initial load
            UpdateStatus();
            LoadExistingLogs();
        }

        private Label CreateLabel(int y, string prefix, GroupBox parent)
        {
            var label = new Label
            {
                Text = prefix,
                Location = new Point(15, y),
                Size = new Size(505, 20),
                Font = new Font("Segoe UI", 9, FontStyle.Regular),
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right
            };
            parent.Controls.Add(label);
            return label;
        }

        private void UpdateStatus()
        {
            string status = _socketClient.IsConnected ? "Orkestratör'e Bağlı" : "Orkestratör'e Bağlanıyor...";
            _lblId.Text = $"Şantiye ID: {_socketClient.SiteId}";
            _lblName.Text = $"Şantiye İsmi: {_socketClient.SiteName}";
            _lblStatus.Text = $"Bağlantı Durumu: {status}";
            _lblLocation.Text = $"Lokasyon: {_socketClient.LocationString}";
        }

        private void LoadExistingLogs()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BarisServiceSuite");
                string path = Path.Combine(folder, "debug_log.txt");
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    int start = Math.Max(0, lines.Length - 100); // load last 100 lines
                    for (int i = start; i < lines.Length; i++)
                    {
                        _txtLog.AppendText(lines[i] + Environment.NewLine);
                    }
                }
            }
            catch { }
        }

        private void AppendLog(string message)
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action<string>(AppendLog), message);
                return;
            }
            _txtLog.AppendText(message + Environment.NewLine);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SocketIOClient;

namespace Agent.Service.Services
{
    public class SocketClient
    {
        private readonly ILogger<SocketClient> _logger;
        private readonly IConfiguration _configuration;
        private readonly TunnelHandler _tunnelHandler;
        private readonly ScreenStreamer _screenStreamer;
        
        private SocketIO? _client;
        private readonly string _url;
        private readonly string _authToken;
        private readonly string _siteId;
        private readonly string _siteName;
        private bool _shouldReconnect = true;

        private string _locationCountry = "";
        private string _locationCity = "";
        private string _locationLat = "";
        private string _locationLon = "";
        private string _locationIsp = "";
        private string _locationString = "Bilinmiyor";

        public string SiteId => _siteId;
        public string SiteName => _siteName;
        public bool IsConnected => _client?.Connected ?? false;
        public string LocationString => _locationString;

        public SocketClient(
            ILogger<SocketClient> logger, 
            IConfiguration configuration, 
            TunnelHandler tunnelHandler,
            ScreenStreamer screenStreamer)
        {
            _logger = logger;
            _configuration = configuration;
            _tunnelHandler = tunnelHandler;
            _screenStreamer = screenStreamer;

            _url = _configuration["Orchestrator:Url"] ?? "http://3.73.144.148";
            _authToken = _configuration["Orchestrator:AuthToken"] ?? "BarisServis2026!";
            
            _siteId = _configuration["Orchestrator:SiteId"] ?? "";
            _siteName = _configuration["Orchestrator:SiteName"] ?? "";

            if (string.IsNullOrEmpty(_siteId))
            {
                // Load or create persistent random agent ID
                var config = AgentConfigManager.LoadOrCreate();
                _siteId = config.SiteId;
                _siteName = config.SiteName;
            }
        }

        private async Task FetchLocationAsync()
        {
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                var response = await http.GetStringAsync("http://ip-api.com/json/");
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                if (root.GetProperty("status").GetString() == "success")
                {
                    _locationCountry = root.GetProperty("country").GetString() ?? "";
                    _locationCity = root.GetProperty("city").GetString() ?? "";
                    _locationLat = root.GetProperty("lat").GetRawText();
                    _locationLon = root.GetProperty("lon").GetRawText();
                    _locationIsp = root.GetProperty("isp").GetString() ?? "";
                    _locationString = $"{_locationCity}, {_locationCountry} ({_locationIsp})";
                    _logger.LogInformation($"[SocketClient] Geolocation retrieved: {_locationString}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[SocketClient] Failed to retrieve geolocation: {ex.Message}");
            }
        }

        private void LogToFile(string message)
        {
            AgentLogger.Log("SocketClient", message);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Starting Socket.io Client to connect to Orchestrator at {_url}...");
            LogToFile($"Starting Socket.io Client to connect to Orchestrator at {_url}...");

            try
            {
                // Run location fetch in the background and wait at most 3 seconds so it never deadlocks startup
                await Task.WhenAny(FetchLocationAsync(), Task.Delay(3000, cancellationToken));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Location fetch task error: {ex.Message}");
                LogToFile($"Location fetch task error: {ex.Message}");
            }

            var options = new SocketIOOptions
            {
                Auth = new { token = _authToken },
                Query = new NameValueCollection
                {
                    { "role", "agent" },
                    { "siteId", _siteId },
                    { "siteName", _siteName },
                    { "rustDeskId", "N/A" },
                    { "rustDeskPassword", "N/A" },
                    { "locationCountry", _locationCountry },
                    { "locationCity", _locationCity },
                    { "locationLat", _locationLat },
                    { "locationLon", _locationLon },
                    { "locationIsp", _locationIsp }
                },
                Reconnection = true
            };

            try
            {
                _client = new SocketIO(new Uri(_url), options);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to initialize SocketIO client with URI.");
                LogToFile($"Failed to initialize SocketIO client: {ex.Message}");
                return;
            }

            _client.OnConnected += (sender, e) =>
            {
                _logger.LogInformation("[SocketClient] Connected to Orchestrator successfully.");
                LogToFile("[SocketClient] Connected to Orchestrator successfully.");
            };

            _client.OnDisconnected += (sender, e) =>
            {
                _logger.LogWarning($"[SocketClient] Disconnected from Orchestrator: {e}");
                LogToFile($"[SocketClient] Disconnected from Orchestrator: {e}");
                _tunnelHandler.CloseAllTunnels();
            };

            _client.OnError += (sender, e) =>
            {
                _logger.LogError($"[SocketClient] Error: {e}");
                LogToFile($"[SocketClient] Error: {e}");
            };

            // Register TCP/UDP tunnel packets handling
            _tunnelHandler.RegisterEvents(_client);

            // Register Screen Share command handling
            _tunnelHandler.OnScreenShareCommand += (masterSocketId, command) =>
            {
                if (command == "start")
                {
                    _screenStreamer.Start(_client!, masterSocketId, _url);
                }
                else if (command == "stop")
                {
                    _screenStreamer.Stop();
                }
                else if (command.StartsWith("{"))
                {
                    _screenStreamer.HandleInputEvent(command);
                }
            };

            // Register File Transfer packet handling
            _tunnelHandler.OnFileTransferPayload += (payload) =>
            {
                _screenStreamer.HandleFileTransferPayload(payload);
            };

            // Register Clipboard Sync packet handling
            _tunnelHandler.OnClipboardSyncPayload += (payload) =>
            {
                _screenStreamer.HandleClipboardSyncPayload(payload);
            };

            // Register UDP Holepunch packet handling
            _tunnelHandler.OnUdpHolePunchPayload += (payload) =>
            {
                _screenStreamer.HandleUdpHolePunchPayload(payload);
            };

            // Register Speed Test packet handling
            _tunnelHandler.OnSpeedTestPayload += (payload) =>
            {
                _screenStreamer.HandleSpeedTestPayload(payload);
            };

            // Register Screen Quality packet handling
            _tunnelHandler.OnScreenQualityPayload += (payload) =>
            {
                _screenStreamer.HandleScreenQualityPayload(payload);
            };



            // Handle server command to terminate session
            _client.On("session-stopped", context =>
            {
                _logger.LogInformation("[SocketClient] Session stopped command received. Closing tunnels.");
                LogToFile("[SocketClient] Session stopped command received. Closing tunnels.");
                _tunnelHandler.CloseAllTunnels();
                _screenStreamer.Stop();
                return Task.CompletedTask;
            });

            _client.On("session-started", context =>
            {
                _logger.LogInformation("[SocketClient] Session started command received. Ready for tunnel streams.");
                LogToFile("[SocketClient] Session started command received. Ready for tunnel streams.");
                return Task.CompletedTask;
            });

            await ConnectWithRetryAsync(cancellationToken);
        }

        private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _shouldReconnect)
            {
                try
                {
                    LogToFile($"Attempting socket connection to {_url}...");
                    await _client!.ConnectAsync();
                    LogToFile("ConnectAsync returned.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[SocketClient] Connection failed: {ex.Message}. Retrying in 5 seconds...");
                    LogToFile($"[SocketClient] Connection failed: {ex.Message} (Type: {ex.GetType().Name})");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("Stopping Socket.io Client...");
            _shouldReconnect = false;
            
            _tunnelHandler.Shutdown();
            _screenStreamer.Stop();

            if (_client != null)
            {
                try
                {
                    await _client.DisconnectAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disconnecting socket client.");
                }
                _client.Dispose();
            }
        }
    }
}

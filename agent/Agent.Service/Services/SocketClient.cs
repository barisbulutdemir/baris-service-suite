using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
        private readonly RustDeskManager _rustDeskManager;
        private readonly TunnelHandler _tunnelHandler;
        
        private SocketIO? _client;
        private readonly string _url;
        private readonly string _authToken;
        private readonly string _siteId;
        private readonly string _siteName;
        private bool _shouldReconnect = true;

        public SocketClient(
            ILogger<SocketClient> logger, 
            IConfiguration configuration, 
            RustDeskManager rustDeskManager,
            TunnelHandler tunnelHandler)
        {
            _logger = logger;
            _configuration = configuration;
            _rustDeskManager = rustDeskManager;
            _tunnelHandler = tunnelHandler;

            _url = _configuration["Orchestrator:Url"] ?? "http://localhost:3000";
            _authToken = _configuration["Orchestrator:AuthToken"] ?? "BarisServis2026!";
            _siteId = _configuration["Orchestrator:SiteId"] ?? Environment.MachineName;
            _siteName = _configuration["Orchestrator:SiteName"] ?? $"{Environment.MachineName} Şantiyesi";
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Starting Socket.io Client to connect to Orchestrator at {_url}...");

            string rustDeskId = _rustDeskManager.GetRustDeskDeskIdAndVerify();
            string rustDeskPassword = _rustDeskManager.GetOrCreatePassword();

            var options = new SocketIOOptions
            {
                Auth = new { token = _authToken },
                Query = new NameValueCollection
                {
                    { "role", "agent" },
                    { "siteId", _siteId },
                    { "siteName", _siteName },
                    { "rustDeskId", rustDeskId },
                    { "rustDeskPassword", rustDeskPassword }
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
                return;
            }

            _client.OnConnected += (sender, e) =>
            {
                _logger.LogInformation("[SocketClient] Connected to Orchestrator successfully.");
            };

            _client.OnDisconnected += (sender, e) =>
            {
                _logger.LogWarning($"[SocketClient] Disconnected from Orchestrator: {e}");
                _tunnelHandler.CloseAllTunnels();
            };

            _client.OnError += (sender, e) =>
            {
                _logger.LogError($"[SocketClient] Error: {e}");
            };

            // Register TCP/UDP tunnel packets handling
            _tunnelHandler.RegisterEvents(_client);

            // Handle server command to terminate session
            _client.On("session-stopped", context =>
            {
                _logger.LogInformation("[SocketClient] Session stopped command received. Closing tunnels.");
                _tunnelHandler.CloseAllTunnels();
                return Task.CompletedTask;
            });

            _client.On("session-started", context =>
            {
                _logger.LogInformation("[SocketClient] Session started command received. Ready for tunnel streams.");
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
                    await _client!.ConnectAsync();
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[SocketClient] Connection failed: {ex.Message}. Retrying in 5 seconds...");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        public async Task StopAsync()
        {
            _logger.LogInformation("Stopping Socket.io Client...");
            _shouldReconnect = false;
            
            _tunnelHandler.Shutdown();

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

    public static class RustDeskExtensions
    {
        public static string GetRustDeskDeskIdAndVerify(this RustDeskManager manager)
        {
            try
            {
                return manager.GetRustDeskId();
            }
            catch
            {
                return "N/A";
            }
        }
    }
}

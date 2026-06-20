using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using SocketIOClient;

namespace MasterUI.Services
{
    public class OrchestratorClient
    {
        private SocketIO? _client;
        private readonly string _url;
        private readonly string _authToken;
        
        // Track connection acknowledgments from Agent
        private readonly ConcurrentDictionary<string, (bool Success, string? Error)> _connectionAcks = new();

        public event Action<List<SiteInfo>>? OnSitesListUpdated;
        public event Action<string, bool, string?>? OnTunnelOpened;
        public event Action<string, byte[]>? OnTunnelData;
        public event Action<string>? OnTunnelClosed;
        public event Action<string, string, int, byte[]>? OnTunnelUdp;
        public event Action<string>? OnSessionTerminated;
        public event Action<string>? OnLog;
        public event Action<bool>? OnChatSystemStatusChanged;

        public bool IsConnected => _client?.Connected ?? false;

        public OrchestratorClient(string url, string authToken)
        {
            _url = url;
            _authToken = authToken;
        }

        public async Task ConnectAsync()
        {
            Log($"[Socket] Connecting to Orchestrator at {_url}...");

            var options = new SocketIOOptions
            {
                Auth = new { token = _authToken },
                Query = new NameValueCollection
                {
                    { "role", "master" }
                },
                Reconnection = true
            };

            _client = new SocketIO(new Uri(_url), options);

            _client.OnConnected += (sender, e) =>
            {
                Log("[Socket] Connected to Orchestrator.");
            };

            _client.OnDisconnected += (sender, e) =>
            {
                Log($"[Socket] Disconnected from Orchestrator: {e}");
            };

            _client.OnError += (sender, e) =>
            {
                Log($"[Socket] Error: {e}");
            };

            // Site List updates
            _client.On("sites-list", context =>
            {
                try
                {
                    var sites = context.GetValue<List<SiteInfo>>(0);
                    OnSitesListUpdated?.Invoke(sites ?? new List<SiteInfo>());
                }
                catch (Exception ex)
                {
                    Log($"[Socket] Error parsing sites list: {ex.Message}");
                }
                return Task.CompletedTask;
            });

            // Chat system status updates
            _client.On("chat-system-status", context =>
            {
                try
                {
                    var status = context.GetValue<ChatSystemStatusResponse>(0);
                    OnChatSystemStatusChanged?.Invoke(status?.enabled ?? false);
                }
                catch (Exception ex)
                {
                    Log($"[Socket] Error parsing chat system status: {ex.Message}");
                }
                return Task.CompletedTask;
            });

            // Tunnel Acknowledged by Agent
            _client.On("tunnel-opened", context =>
            {
                try
                {
                    var obj = context.GetValue<System.Text.Json.JsonElement>(0);
                    var connectionId = obj.GetProperty("connectionId").GetString() ?? "";
                    var success = obj.GetProperty("success").GetBoolean();
                    string? error = null;
                    if (obj.TryGetProperty("error", out var errProp))
                    {
                        error = errProp.GetString();
                    }

                    _connectionAcks[connectionId] = (success, error);
                    OnTunnelOpened?.Invoke(connectionId, success, error);
                }
                catch (Exception ex)
                {
                    Log($"[Socket] Error parsing tunnel-opened: {ex.Message}");
                }
                return Task.CompletedTask;
            });

            // Tunnel TCP Data received from Agent
            _client.On("tunnel-data", context =>
            {
                try
                {
                    var obj = context.GetValue<System.Text.Json.JsonElement>(0);
                    var connectionId = obj.GetProperty("connectionId").GetString() ?? "";
                    var base64Data = obj.GetProperty("chunk").GetString() ?? "";
                    byte[] data;
                    try
                    {
                        data = Convert.FromBase64String(base64Data);
                    }
                    catch
                    {
                        try { data = System.Text.Encoding.UTF8.GetBytes(base64Data); } catch { data = Array.Empty<byte>(); }
                    }

                    OnTunnelData?.Invoke(connectionId, data);
                }
                catch (Exception ex)
                {
                    Log($"[Socket] Error parsing tunnel-data: {ex.Message}");
                }
                return Task.CompletedTask;
            });

            // Tunnel Closed by Agent
            _client.On("tunnel-close", context =>
            {
                try
                {
                    var obj = context.GetValue<System.Text.Json.JsonElement>(0);
                    var connectionId = obj.GetProperty("connectionId").GetString() ?? "";
                    OnTunnelClosed?.Invoke(connectionId);
                }
                catch (Exception ex)
                {
                    Log($"[Socket] Error parsing tunnel-close: {ex.Message}");
                }
                return Task.CompletedTask;
            });

            // Tunnel UDP Packet received from Agent
            _client.On("tunnel-udp", context =>
            {
                try
                {
                    var obj = context.GetValue<System.Text.Json.JsonElement>(0);
                    var connectionId = obj.GetProperty("connectionId").GetString() ?? "";
                    var host = obj.GetProperty("host").GetString() ?? "";
                    var port = obj.GetProperty("port").GetInt32();
                    var base64Data = obj.GetProperty("chunk").GetString() ?? "";
                    byte[] data;
                    try
                    {
                        data = Convert.FromBase64String(base64Data);
                    }
                    catch
                    {
                        data = Array.Empty<byte>();
                    }

                    OnTunnelUdp?.Invoke(connectionId, host, port, data);
                }
                catch (Exception ex)
                {
                    Log($"[Socket] Error parsing tunnel-udp: {ex.Message}");
                }
                return Task.CompletedTask;
            });

            _client.On("session-terminated", context =>
            {
                var reason = context.GetValue<string>(0) ?? "Unknown";
                Log($"[Socket] Session terminated by server. Reason: {reason}");
                OnSessionTerminated?.Invoke(reason);
                return Task.CompletedTask;
            });

            await _client.ConnectAsync();
        }

        public async Task<bool> StartSessionAsync(string siteId)
        {
            if (_client == null || !_client.Connected) return false;
            
            bool success = false;
            var tcs = new TaskCompletionSource<bool>();

            await _client.EmitAsync("start-session", new object[] { new { siteId } }, response =>
            {
                try
                {
                    var res = response.GetValue<SessionResponse>(0);
                    success = res?.success ?? false;
                    tcs.SetResult(success);
                }
                catch (Exception ex)
                {
                    Log($"[Socket] Error starting session: {ex.Message}");
                    tcs.SetResult(false);
                }
                return Task.CompletedTask;
            });

            // Set 5s timeout on callback wait
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            if (completedTask == tcs.Task)
            {
                return tcs.Task.Result;
            }
            return false;
        }

        public async Task StopSessionAsync(string siteId)
        {
            if (_client != null && _client.Connected)
            {
                await _client.EmitAsync("stop-session", new object[] { new { siteId } });
                Log($"[Socket] Stopped session for site: {siteId}");
            }
        }

        #region Tunnel Transmission Methods (Master -> Agent)

        public async Task SendTunnelOpenAsync(string siteId, string connectionId, string host, int port)
        {
            if (_client != null && _client.Connected)
            {
                _connectionAcks.TryRemove(connectionId, out _);
                await _client.EmitAsync("tunnel-open", new object[] { new { siteId, connectionId, host, port } });
            }
        }

        public async Task SendTunnelDataAsync(string siteId, string connectionId, byte[] chunk)
        {
            if (_client != null && _client.Connected)
            {
                string base64Data = Convert.ToBase64String(chunk);
                await _client.EmitAsync("tunnel-data", new object[] { new { siteId, connectionId, chunk = base64Data } });
            }
        }

        public async Task SendTunnelCloseAsync(string siteId, string connectionId)
        {
            if (_client != null && _client.Connected)
            {
                await _client.EmitAsync("tunnel-close", new object[] { new { siteId, connectionId } });
            }
        }

        public async Task SendTunnelUdpAsync(string siteId, string connectionId, string host, int port, byte[] chunk)
        {
            if (_client != null && _client.Connected)
            {
                string base64Data = Convert.ToBase64String(chunk);
                await _client.EmitAsync("tunnel-udp", new object[] { new { siteId, connectionId, host, port, chunk = base64Data } });
            }
        }

        #endregion

        public bool IsConnectionConfirmed(string connectionId, out bool success, out string? error)
        {
            if (_connectionAcks.TryGetValue(connectionId, out var ack))
            {
                success = ack.Success;
                error = ack.Error;
                return true;
            }
            success = false;
            error = null;
            return false;
        }

        public async Task RenameSiteAsync(string siteId, string newName)
        {
            if (_client != null && _client.Connected)
            {
                await _client.EmitAsync("rename-site", new object[] { new { siteId, newName } });
                Log($"[Socket] Şantiye {siteId} için yeni isim talebi gönderildi: '{newName}'");
            }
        }

        public async Task DeleteSiteAsync(string siteId)
        {
            if (_client != null && _client.Connected)
            {
                await _client.EmitAsync("delete-site", new object[] { new { siteId } });
                Log($"[Socket] Şantiye {siteId} için silme talebi gönderildi.");
            }
        }

        public async Task ToggleChatSystemAsync(bool enabled)
        {
            if (_client != null && _client.Connected)
            {
                await _client.EmitAsync("toggle-chat-system", new object[] { new { enabled } });
                Log($"[Socket] Web Görüşme Sistemi durum değiştirme talebi gönderildi: {(enabled ? "AKTİF" : "PASİF")}");
            }
        }

        public async Task DisconnectAsync()
        {
            if (_client != null)
            {
                await _client.DisconnectAsync();
                _client.Dispose();
                _client = null;
            }
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }
    }

    public class ChatSystemStatusResponse
    {
        public bool enabled { get; set; }
    }

    public class SessionResponse
    {
        public bool success { get; set; }
        public string? error { get; set; }
    }

    public class SiteInfo
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string status { get; set; } = "offline";
        public string? rustDeskId { get; set; }
        public string? rustDeskPassword { get; set; }
        public string? socketId { get; set; }
        public long? lastSeen { get; set; }
        public LocationDetails? location { get; set; }
    }

    public class LocationDetails
    {
        public string? country { get; set; }
        public string? city { get; set; }
        public double? lat { get; set; }
        public double? lon { get; set; }
        public string? isp { get; set; }
    }
}

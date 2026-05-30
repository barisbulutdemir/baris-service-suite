using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SocketIOClient;

namespace Agent.Service.Services
{
    public class TunnelHandler
    {
        private readonly ILogger<TunnelHandler> _logger;
        private readonly ConcurrentDictionary<string, TcpClient> _tcpConnections = new();
        private readonly ConcurrentDictionary<string, UdpClient> _udpClients = new();
        private readonly ConcurrentDictionary<string, DateTime> _udpLastActive = new();
        private readonly CancellationTokenSource _udpCleanupCts = new();

        public TunnelHandler(ILogger<TunnelHandler> logger)
        {
            _logger = logger;
            Task.Run(CleanupInactiveUdpClients);
        }

        public void RegisterEvents(SocketIO client)
        {
            client.On("tunnel-open", async context =>
            {
                var masterSocketId = context.GetValue<string>(0) ?? "";
                var connectionId = context.GetValue<string>(1) ?? "";
                var host = context.GetValue<string>(2) ?? "";
                var port = context.GetValue<int>(3);

                _logger.LogInformation($"[Tunnel] Request to open TCP tunnel. ConnectionId: {connectionId}, Target: {host}:{port}");
                await HandleTcpOpenAsync(client, masterSocketId, connectionId, host, port);
            });

            client.On("tunnel-data", context =>
            {
                var connectionId = context.GetValue<string>(1) ?? "";
                var base64Data = context.GetValue<string>(2) ?? "";
                byte[] chunk;
                try
                {
                    chunk = Convert.FromBase64String(base64Data);
                }
                catch
                {
                    try
                    {
                        chunk = context.GetValue<byte[]>(2);
                    }
                    catch
                    {
                        chunk = Array.Empty<byte>();
                    }
                }

                HandleTcpData(connectionId, chunk);
                return Task.CompletedTask;
            });

            client.On("tunnel-close", context =>
            {
                var connectionId = context.GetValue<string>(1) ?? "";
                HandleTcpClose(connectionId);
                return Task.CompletedTask;
            });

            client.On("tunnel-udp", context =>
            {
                var masterSocketId = context.GetValue<string>(0) ?? "";
                var connectionId = context.GetValue<string>(1) ?? "";
                var host = context.GetValue<string>(2) ?? "";
                var port = context.GetValue<int>(3);
                var base64Data = context.GetValue<string>(4) ?? "";
                byte[] chunk;
                try
                {
                    chunk = Convert.FromBase64String(base64Data);
                }
                catch
                {
                    try
                    {
                        chunk = context.GetValue<byte[]>(4);
                    }
                    catch
                    {
                        chunk = Array.Empty<byte>();
                    }
                }

                HandleUdpPacket(client, masterSocketId, connectionId, host, port, chunk);
                return Task.CompletedTask;
            });

            client.On("session-terminated", context =>
            {
                _logger.LogInformation("[Tunnel] Session terminated by orchestrator. Cleaning up all connections.");
                CloseAllTunnels();
                return Task.CompletedTask;
            });
        }

        #region TCP Tunnelling

        private async Task HandleTcpOpenAsync(SocketIO client, string masterSocketId, string connectionId, string host, int port)
        {
            try
            {
                var tcpClient = new TcpClient();
                _tcpConnections[connectionId] = tcpClient;

                var connectTask = tcpClient.ConnectAsync(host, port);
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == timeoutTask || !tcpClient.Connected)
                {
                    throw new TimeoutException($"Connection to {host}:{port} timed out.");
                }

                _logger.LogInformation($"[Tunnel] TCP connected to {host}:{port} for ConnectionId: {connectionId}");

                await client.EmitAsync("tunnel-opened", new object[] { new { masterSocketId, connectionId, success = true } });

                _ = Task.Run(() => StartTcpReadLoopAsync(client, masterSocketId, connectionId, tcpClient));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[Tunnel] Failed to open TCP connection to {host}:{port}");
                _tcpConnections.TryRemove(connectionId, out _);
                await client.EmitAsync("tunnel-opened", new object[] { new { masterSocketId, connectionId, success = false, error = ex.Message } });
            }
        }

        private async Task StartTcpReadLoopAsync(SocketIO client, string masterSocketId, string connectionId, TcpClient tcpClient)
        {
            var stream = tcpClient.GetStream();
            var buffer = new byte[8192];

            try
            {
                while (tcpClient.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    byte[] data = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);

                    string base64Data = Convert.ToBase64String(data);
                    await client.EmitAsync("tunnel-data", new object[] { new { masterSocketId, connectionId, chunk = base64Data } });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Tunnel] Error reading from TCP connection {connectionId}: {ex.Message}");
            }
            finally
            {
                _logger.LogInformation($"[Tunnel] TCP connection {connectionId} closed.");
                CleanupTcpConnection(connectionId);
                await client.EmitAsync("tunnel-close", new object[] { new { masterSocketId, connectionId } });
            }
        }

        private void HandleTcpData(string connectionId, byte[] chunk)
        {
            if (_tcpConnections.TryGetValue(connectionId, out var tcpClient) && tcpClient.Connected)
            {
                try
                {
                    var stream = tcpClient.GetStream();
                    stream.Write(chunk, 0, chunk.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[Tunnel] Error writing TCP data to connection {connectionId}");
                    CleanupTcpConnection(connectionId);
                }
            }
        }

        private void HandleTcpClose(string connectionId)
        {
            _logger.LogInformation($"[Tunnel] Master requested closure of connection {connectionId}");
            CleanupTcpConnection(connectionId);
        }

        private void CleanupTcpConnection(string connectionId)
        {
            if (_tcpConnections.TryRemove(connectionId, out var tcpClient))
            {
                try { tcpClient.Close(); } catch { }
                try { tcpClient.Dispose(); } catch { }
            }
        }

        #endregion

        #region UDP Tunnelling

        private void HandleUdpPacket(SocketIO client, string masterSocketId, string connectionId, string host, int port, byte[] chunk)
        {
            try
            {
                _udpLastActive[connectionId] = DateTime.UtcNow;

                if (!_udpClients.TryGetValue(connectionId, out var udpClient))
                {
                    _logger.LogInformation($"[Tunnel] Creating new UDP tunnel for ConnectionId: {connectionId}");
                    udpClient = new UdpClient();
                    _udpClients[connectionId] = udpClient;

                    _ = Task.Run(() => StartUdpReadLoopAsync(client, masterSocketId, connectionId, udpClient));
                }

                udpClient.Send(chunk, chunk.Length, host, port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[Tunnel] Error handling UDP packet for {connectionId}");
            }
        }

        private async Task StartUdpReadLoopAsync(SocketIO client, string masterSocketId, string connectionId, UdpClient udpClient)
        {
            try
            {
                while (true)
                {
                    var result = await udpClient.ReceiveAsync();
                    _udpLastActive[connectionId] = DateTime.UtcNow;

                    string base64Data = Convert.ToBase64String(result.Buffer);
                    await client.EmitAsync("tunnel-udp", new object[] { new 
                    { 
                        masterSocketId, 
                        connectionId, 
                        host = result.RemoteEndPoint.Address.ToString(), 
                        port = result.RemoteEndPoint.Port, 
                        chunk = base64Data 
                    } });
                }
            }
            catch (Exception)
            {
                // Expected when UdpClient is closed
            }
            finally
            {
                CleanupUdpClient(connectionId);
            }
        }

        private void CleanupUdpClient(string connectionId)
        {
            if (_udpClients.TryRemove(connectionId, out var udpClient))
            {
                try { udpClient.Close(); } catch { }
                try { udpClient.Dispose(); } catch { }
            }
            _udpLastActive.TryRemove(connectionId, out _);
        }

        private async Task CleanupInactiveUdpClients()
        {
            while (!_udpCleanupCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10000, _udpCleanupCts.Token);
                    var now = DateTime.UtcNow;

                    foreach (var pair in _udpLastActive)
                    {
                        if ((now - pair.Value).TotalSeconds > 60)
                        {
                            _logger.LogInformation($"[Tunnel] Inactivity timeout. Cleaning up UDP client: {pair.Key}");
                            CleanupUdpClient(pair.Key);
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Tunnel] Error cleaning up inactive UDP clients");
                }
            }
        }

        #endregion

        public void CloseAllTunnels()
        {
            _logger.LogInformation("[Tunnel] Closing all active TCP and UDP tunnel connections.");
            
            foreach (var connectionId in _tcpConnections.Keys)
            {
                CleanupTcpConnection(connectionId);
            }

            foreach (var connectionId in _udpClients.Keys)
            {
                CleanupUdpClient(connectionId);
            }
        }

        public void Shutdown()
        {
            _udpCleanupCts.Cancel();
            CloseAllTunnels();
        }
    }
}

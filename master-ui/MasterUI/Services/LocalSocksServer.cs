using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MasterUI.Services
{
    public class LocalSocksServer
    {
        private readonly int _port;
        private readonly OrchestratorClient _orchestratorClient;
        private readonly string _siteId;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, TcpClient> _activeConnections = new();
        private readonly ConcurrentDictionary<string, UdpClient> _activeUdpRelays = new();
        private readonly ConcurrentDictionary<string, IPEndPoint> _udpClientEndPoints = new();

        public event Action<string, string, int>? OnTunnelCreated;
        public event Action<string, string>? OnTunnelClosed;
        public event Action<string>? OnLog;

        public LocalSocksServer(int port, OrchestratorClient orchestratorClient, string siteId)
        {
            _port = port;
            _orchestratorClient = orchestratorClient;
            _siteId = siteId;

            // Register socket event callbacks to relay incoming tunnel responses back to the local client sockets
            _orchestratorClient.OnTunnelOpened += HandleTunnelOpened;
            _orchestratorClient.OnTunnelData += HandleTunnelDataReceived;
            _orchestratorClient.OnTunnelClosed += HandleTunnelClosedByAgent;
            _orchestratorClient.OnTunnelUdp += HandleTunnelUdpReceived;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            Log($"[SOCKS5] Server started on port {_port}. Waiting for connections...");

            Task.Run(() => AcceptConnectionsAsync(_cts.Token));
        }

        public void Stop()
        {
            Log("[SOCKS5] Stopping server...");
            _cts?.Cancel();
            _listener?.Stop();

            foreach (var conn in _activeConnections.Values)
            {
                try { conn.Close(); } catch { }
            }
            _activeConnections.Clear();

            foreach (var udp in _activeUdpRelays.Values)
            {
                try { udp.Close(); } catch { }
            }
            _activeUdpRelays.Clear();
            _udpClientEndPoints.Clear();

            _orchestratorClient.OnTunnelOpened -= HandleTunnelOpened;
            _orchestratorClient.OnTunnelData -= HandleTunnelDataReceived;
            _orchestratorClient.OnTunnelClosed -= HandleTunnelClosedByAgent;
            _orchestratorClient.OnTunnelUdp -= HandleTunnelUdpReceived;
        }

        private async Task AcceptConnectionsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleClientAsync(client, token));
                }
                catch (Exception)
                {
                    break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    // 1. Handshake
                    var header = new byte[2];
                    if (await ReadBytesAsync(stream, header, 2, token) != 2) return;

                    if (header[0] != 0x05) // SOCKS5 Version
                    {
                        Log("[SOCKS5] Unsupported SOCKS version.");
                        return;
                    }

                    int methodCount = header[1];
                    var methods = new byte[methodCount];
                    if (await ReadBytesAsync(stream, methods, methodCount, token) != methodCount) return;

                    // Response: SOCKS5, No Authentication Required (0x00)
                    stream.Write(new byte[] { 0x05, 0x00 }, 0, 2);

                    // 2. Request
                    var requestHeader = new byte[4];
                    if (await ReadBytesAsync(stream, requestHeader, 4, token) != 4) return;

                    byte cmd = requestHeader[1];
                    byte atyp = requestHeader[3];

                    string targetHost = "";
                    int targetPort = 0;

                    if (atyp == 0x01) // IPv4
                    {
                        var ipBytes = new byte[4];
                        if (await ReadBytesAsync(stream, ipBytes, 4, token) != 4) return;
                        targetHost = new IPAddress(ipBytes).ToString();
                    }
                    else if (atyp == 0x03) // Domain name
                    {
                        var lenByte = new byte[1];
                        if (await ReadBytesAsync(stream, lenByte, 1, token) != 1) return;
                        int len = lenByte[0];
                        var hostBytes = new byte[len];
                        if (await ReadBytesAsync(stream, hostBytes, len, token) != len) return;
                        targetHost = Encoding.ASCII.GetString(hostBytes);
                    }
                    else
                    {
                        Log("[SOCKS5] Unsupported Address Type.");
                        return;
                    }

                    var portBytes = new byte[2];
                    if (await ReadBytesAsync(stream, portBytes, 2, token) != 2) return;
                    targetPort = (portBytes[0] << 8) + portBytes[1];

                    if (cmd == 0x01) // CONNECT
                    {
                        var connectionId = Guid.NewGuid().ToString();
                        _activeConnections[connectionId] = client;

                        Log($"[SOCKS5] Connection request to {targetHost}:{targetPort}. ConnectionId: {connectionId}");
                        OnTunnelCreated?.Invoke(connectionId, targetHost, targetPort);

                        // Tell Orchestrator to open connection at the Agent side
                        await _orchestratorClient.SendTunnelOpenAsync(_siteId, connectionId, targetHost, targetPort);

                        // Wait for agent acknowledgment (TunnelOpened event)
                        // We will check periodically for connection established
                        int timeoutMs = 5000;
                        int elapsed = 0;
                        bool connected = false;
                        
                        while (elapsed < timeoutMs)
                        {
                            if (client.Client == null || !client.Connected) break;
                            
                            // Check if the connection has been established (acknowledged by agent)
                            if (_orchestratorClient.IsConnectionConfirmed(connectionId, out var success, out var error))
                            {
                                if (success)
                                {
                                    connected = true;
                                    // Reply: Success, Bound to 127.0.0.1:0
                                    var reply = new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 };
                                    stream.Write(reply, 0, reply.Length);
                                }
                                else
                                {
                                    Log($"[SOCKS5] Agent failed to connect to {targetHost}:{targetPort}: {error}");
                                }
                                break;
                            }
                            await Task.Delay(100);
                            elapsed += 100;
                        }

                        if (!connected)
                        {
                            // Reply: Host Unreachable (0x04)
                            var reply = new byte[] { 0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                            try { stream.Write(reply, 0, reply.Length); } catch { }
                            CleanupConnection(connectionId);
                            return;
                        }

                        // Start piping data from local Client -> WebSocket Tunnel -> Agent -> PLC
                        var buffer = new byte[8192];
                        while (client.Connected && !token.IsCancellationRequested)
                        {
                            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                            if (bytesRead == 0) break; // EOF

                            byte[] data = new byte[bytesRead];
                            Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);

                            // Send data over WebSocket tunnel
                            await _orchestratorClient.SendTunnelDataAsync(_siteId, connectionId, data);
                        }

                        // Inform agent to close remote socket
                        await _orchestratorClient.SendTunnelCloseAsync(_siteId, connectionId);
                        CleanupConnection(connectionId);
                    }
                    else if (cmd == 0x03) // UDP ASSOCIATE
                    {
                        Log("[SOCKS5] UDP Associate request.");
                        
                        // Setup local UDP relay socket
                        var udpListener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                        var localPort = ((IPEndPoint)udpListener.Client.LocalEndPoint!).Port;
                        var connectionId = Guid.NewGuid().ToString();

                        _activeUdpRelays[connectionId] = udpListener;

                        // Reply: Success, Bound to 127.0.0.1:localPort
                        var reply = new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, (byte)(localPort >> 8), (byte)(localPort & 0xFF) };
                        stream.Write(reply, 0, reply.Length);

                        // Start UDP read loop
                        _ = Task.Run(() => StartUdpRelayReadAsync(connectionId, udpListener, token));

                        // SOCKS5 protocol requires keeping this TCP connection open as long as the UDP associate session is alive
                        var dummyBuffer = new byte[1];
                        try
                        {
                            while (client.Connected && !token.IsCancellationRequested)
                            {
                                // If ReadAsync returns 0, the TCP control connection is closed, meaning we should terminate the UDP session
                                if (await stream.ReadAsync(dummyBuffer, 0, 1, token) == 0) break;
                            }
                        }
                        catch { }

                        Log($"[SOCKS5] UDP Control socket closed. Cleaning up UDP relay {connectionId}");
                        CleanupUdpRelay(connectionId);
                    }
                    else
                    {
                        Log("[SOCKS5] Command not supported.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"[SOCKS5] Error handling client: {ex.Message}");
                }
            }
        }

        private async Task StartUdpRelayReadAsync(string connectionId, UdpClient udpListener, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await udpListener.ReceiveAsync(token);
                    
                    // SOCKS5 UDP Header:
                    // +----+------+------+----------+----------+----------+
                    // |RSV | FRAG | ATYP | DST.ADDR | DST.PORT |   DATA   |
                    // +----+------+------+----------+----------+----------+
                    // | 2  |  1   |  1   | Variable |    2     | Variable |
                    // +----+------+------+----------+----------+----------+
                    var data = result.Buffer;
                    if (data.Length < 10) continue;

                    byte frag = data[2];
                    if (frag != 0x00) continue; // Fragmented SOCKS5 UDP not supported

                    byte atyp = data[3];
                    string targetHost = "";
                    int targetPort = 0;
                    int headerSize = 0;

                    if (atyp == 0x01) // IPv4
                    {
                        var ipBytes = new byte[4];
                        Buffer.BlockCopy(data, 4, ipBytes, 0, 4);
                        targetHost = new IPAddress(ipBytes).ToString();
                        targetPort = (data[8] << 8) + data[9];
                        headerSize = 10;
                    }
                    else
                    {
                        continue; // Only IPv4 UDP tunneling supported
                    }

                    // Save client endpoint to reply back to them later
                    _udpClientEndPoints[connectionId] = result.RemoteEndPoint;

                    // Extract actual payload
                    int payloadSize = data.Length - headerSize;
                    byte[] payload = new byte[payloadSize];
                    Buffer.BlockCopy(data, headerSize, payload, 0, payloadSize);

                    // Send UDP packet over WebSocket tunnel
                    await _orchestratorClient.SendTunnelUdpAsync(_siteId, connectionId, targetHost, targetPort, payload);
                }
            }
            catch { }
            finally
            {
                CleanupUdpRelay(connectionId);
            }
        }

        private async Task<int> ReadBytesAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken token)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, totalRead, count - totalRead, token);
                if (read == 0) break;
                totalRead += read;
            }
            return totalRead;
        }

        #region Event Handlers from Agent

        private void HandleTunnelOpened(string connectionId, bool success, string? error)
        {
            // Handled by polling in HandleClientAsync using IsConnectionConfirmed
        }

        private void HandleTunnelDataReceived(string connectionId, byte[] chunk)
        {
            if (_activeConnections.TryGetValue(connectionId, out var client) && client.Connected)
            {
                try
                {
                    client.GetStream().Write(chunk, 0, chunk.Length);
                }
                catch (Exception ex)
                {
                    Log($"[SOCKS5] Error writing to client for connection {connectionId}: {ex.Message}");
                    CleanupConnection(connectionId);
                }
            }
        }

        private void HandleTunnelClosedByAgent(string connectionId)
        {
            Log($"[SOCKS5] Agent closed connection {connectionId}");
            CleanupConnection(connectionId);
        }

        private void HandleTunnelUdpReceived(string connectionId, string host, int port, byte[] chunk)
        {
            if (_activeUdpRelays.TryGetValue(connectionId, out var udpClient) && _udpClientEndPoints.TryGetValue(connectionId, out var clientEP))
            {
                try
                {
                    // Construct SOCKS5 UDP Header: RSV(2) = 00 00, FRAG(1) = 00, ATYP(1) = 01 (IPv4), IP(4), Port(2)
                    var ipBytes = IPAddress.Parse(host).GetAddressBytes();
                    var header = new byte[10 + chunk.Length];
                    header[0] = 0;
                    header[1] = 0;
                    header[2] = 0;
                    header[3] = 1; // IPv4
                    Buffer.BlockCopy(ipBytes, 0, header, 4, 4);
                    header[8] = (byte)(port >> 8);
                    header[9] = (byte)(port & 0xFF);
                    Buffer.BlockCopy(chunk, 0, header, 10, chunk.Length);

                    udpClient.Send(header, header.Length, clientEP);
                }
                catch (Exception ex)
                {
                    Log($"[SOCKS5] Error sending UDP reply: {ex.Message}");
                }
            }
        }

        #endregion

        private void CleanupConnection(string connectionId)
        {
            if (_activeConnections.TryRemove(connectionId, out var client))
            {
                try { client.Close(); } catch { }
                OnTunnelClosed?.Invoke(connectionId, "TCP");
            }
        }

        private void CleanupUdpRelay(string connectionId)
        {
            if (_activeUdpRelays.TryRemove(connectionId, out var udpClient))
            {
                try { udpClient.Close(); } catch { }
                OnTunnelClosed?.Invoke(connectionId, "UDP");
            }
            _udpClientEndPoints.TryRemove(connectionId, out _);
        }

        private void Log(string msg)
        {
            OnLog?.Invoke(msg);
        }
    }
}

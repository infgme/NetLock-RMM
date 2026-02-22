using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Global.Helper;

namespace NetLock_RMM_Relay_App.RelayClient
{
    /// <summary>
    /// Manages relay connection to backend server via TCP (Port 7443) and local port forwarding
    /// Each local connection establishes its own TCP tunnel to the backend
    /// </summary>
    public class RelayConnection
    {
        private const int MAX_RECONNECT_ATTEMPTS = 60;
        private const int RECONNECT_DELAY_MS = 2000;
        private const int KICK_STATUS_CHECK_INTERVAL_MS = 5000; // 5 seconds
        
        private readonly string _backendHost; // Relay TCP host
        private readonly int _relayPort; // Relay TCP port
        private readonly string _backendUrl; // Backend HTTP URL
        private readonly string _apiKey;
        private readonly string _hardwareId;
        private readonly bool _useRelayTls; // Enable/disable TLS for relay connections
        
        public string? SessionId { get; private set; }
        public int LocalPort { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsReconnecting { get; private set; }
        
        private TcpListener? _localListener;
        private CancellationTokenSource? _cancellationTokenSource;
        private int _activeConnectionCount = 0;
        private readonly object _connectionCountLock = new object();
        private bool _manualDisconnect = false;
        
        // E2EE: GLOBAL Admin Keypair (generated once on first connect, reused for all sessions)
        private static System.Security.Cryptography.RSA? _globalAdminPrivateKey;
        private static string? _globalAdminPublicKeyPem;
        private static readonly object _globalKeypairLock = new object();
        
        // E2EE Support (pro Connection)
        private System.Security.Cryptography.RSA? _agentPublicKey;
        private RelayEncryption? _relayEncryption;
        
        // Throughput tracking
        private long _totalBytesUpload = 0;
        private long _totalBytesDownload = 0;
        private long _bytesUploadLastSecond = 0;
        private long _bytesDownloadLastSecond = 0;
        private readonly object _throughputLock = new object();
        private System.Timers.Timer? _throughputTimer;
        
        // Ping tracking
        private long _currentPingMs = 0;
        private long _averagePingMs = 0;
        private readonly List<long> _pingHistory = new List<long>();
        private readonly object _pingLock = new object();
        private System.Timers.Timer? _pingTimer;
        
        // Kick status polling
        private System.Timers.Timer? _kickStatusTimer;
        
        // Public property to check if there are active local connections
        public bool HasActiveConnections
        {
            get
            {
                lock (_connectionCountLock)
                {
                    return _activeConnectionCount > 0;
                }
            }
        }
        
        public int ActiveConnectionCount
        {
            get
            {
                lock (_connectionCountLock)
                {
                    return _activeConnectionCount;
                }
            }
        }
        
        // Throughput properties (bytes per second)
        public long UploadBytesPerSecond
        {
            get
            {
                lock (_throughputLock)
                {
                    return _bytesUploadLastSecond;
                }
            }
        }
        
        public long DownloadBytesPerSecond
        {
            get
            {
                lock (_throughputLock)
                {
                    return _bytesDownloadLastSecond;
                }
            }
        }
        
        public long TotalBytesUpload
        {
            get
            {
                lock (_throughputLock)
                {
                    return _totalBytesUpload;
                }
            }
        }
        
        public long TotalBytesDownload
        {
            get
            {
                lock (_throughputLock)
                {
                    return _totalBytesDownload;
                }
            }
        }
        
        // Ping properties (milliseconds)
        public long CurrentPingMs
        {
            get
            {
                lock (_pingLock)
                {
                    return _currentPingMs;
                }
            }
        }
        
        public long AveragePingMs
        {
            get
            {
                lock (_pingLock)
                {
                    return _averagePingMs;
                }
            }
        }
        
        // Event that fires when the listener fails (e.g., port is no longer available)
        public event EventHandler<string>? ConnectionLost;
        public event EventHandler? Reconnected;
        public event EventHandler<int>? ActiveConnectionsChanged;
        public event EventHandler<(long upload, long download)>? ThroughputChanged;
        public event EventHandler<(long current, long average)>? PingChanged;
        public event EventHandler<KickEventArgs>? OnKicked;

        public RelayConnection(string backendUrl, string relayUrl, string apiKey, string hardwareId, bool useRelayTls = false)
        {
            // Store backendUrl WITHOUT trailing slash for HTTP requests
            _backendUrl = backendUrl.TrimEnd('/');
            
            // Parse relay URL to extract host and port for TCP connections
            // Expected format: "host:port" or "192.168.1.1:7443"
            string cleanRelayUrl = relayUrl
                .Replace("https://", "")
                .Replace("http://", "")
                .TrimEnd('/');
            
            var parts = cleanRelayUrl.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[1], out int port))
            {
                _backendHost = parts[0];
                _relayPort = port;
            }
            else if (parts.Length == 1)
            {
                // No port specified, use default
                _backendHost = parts[0];
                _relayPort = 7443;
            }
            else
            {
                throw new ArgumentException($"Invalid relay URL format: {relayUrl}. Expected format: 'host:port' (e.g., '192.168.1.1:7443')");
            }
            
            _apiKey = apiKey;
            _hardwareId = hardwareId;
            _useRelayTls = useRelayTls;
            
            Logging.Info("RelayConnection", "Constructor", 
                $"Initialized - Backend URL: {_backendUrl}, Relay: {_backendHost}:{_relayPort}, TLS: {_useRelayTls}");
        }

        /// <summary>
        /// Connects to a relay session and opens a local port for forwarding
        /// Each incoming local connection will establish its own TCP tunnel to backend
        /// </summary>
        public async Task<(bool success, string? error)> ConnectToSession(string sessionId, int localPort)
        {
            try
            {
                SessionId = sessionId;
                LocalPort = localPort;

                _cancellationTokenSource = new CancellationTokenSource();

                Console.WriteLine($"[RELAY APP] ========== Starting Connection Flow ==========");
                Console.WriteLine($"[RELAY APP] Session ID: {sessionId}");
                Console.WriteLine($"[RELAY APP] Local Port: {localPort}");
                
                Logging.Debug("RelayConnection", "ConnectToSession",
                    $"Setting up relay for session {sessionId}");

                // E2EE: Generate global Admin RSA-4096 keypair once (reused for all sessions)
                lock (_globalKeypairLock)
                {
                    if (_globalAdminPrivateKey == null || string.IsNullOrEmpty(_globalAdminPublicKeyPem))
                    {
                        // First connection - generate global keypair
                        Console.WriteLine($"[RELAY APP] Generating new Admin RSA-4096 keypair...");
                        
                        Logging.Info("RelayConnection", "ConnectToSession",
                            "Generating GLOBAL Admin RSA-4096 keypair for E2EE (once for entire app)...");
                        
                        var (adminRsa, adminPublicKeyPem) = RelayKeyGenerator.GenerateKeyPair();
                        _globalAdminPrivateKey = adminRsa;
                        _globalAdminPublicKeyPem = adminPublicKeyPem;
                        
                        Console.WriteLine($"[RELAY APP] Admin keypair generated (length: {adminPublicKeyPem.Length})");
                        Console.WriteLine($"[RELAY APP] Admin Public Key (first 100 chars): {adminPublicKeyPem.Substring(0, Math.Min(100, adminPublicKeyPem.Length))}");
                        Console.WriteLine($"[RELAY APP] This key will be reused for all sessions");
                        
                        Logging.Info("RelayConnection", "ConnectToSession",
                            "GLOBAL Admin keypair generated and will be reused for all sessions");
                        Logging.Info("RelayConnection", "ConnectToSession",
                            $"Admin Public Key length: {adminPublicKeyPem.Length} chars");
                    }
                    else
                    {
                        // Keypair already exists - reuse
                        Console.WriteLine($"[RELAY APP] Reusing GLOBAL Admin RSA keypair (same for all sessions)");
                        Console.WriteLine($"[RELAY APP] Admin Public Key (first 100 chars): {_globalAdminPublicKeyPem.Substring(0, Math.Min(100, _globalAdminPublicKeyPem.Length))}");
                        
                        Logging.Info("RelayConnection", "ConnectToSession",
                            "Reusing GLOBAL Admin RSA keypair (same for all sessions)");
                    }
                }

                // 2. HTTP announcement before TCP connection
                Console.WriteLine($"[RELAY APP] Announcing connection via HTTP...");
                
                var (announceSuccess, announceError) = await AnnounceConnectionAsync(sessionId, _globalAdminPublicKeyPem);
                
                if (!announceSuccess)
                {
                    Console.WriteLine($"[RELAY APP] Announce failed: {announceError}");
                    return (false, $"Failed to announce connection: {announceError}");
                }
                
                Console.WriteLine($"[RELAY APP] Connection announced - agent should be connecting now...");

                // Wait 2 seconds to give agent time to connect
                Console.WriteLine($"[RELAY APP] Waiting 2 seconds for agent to connect...");
                await Task.Delay(2000);

                // 4. Start local listener
                Console.WriteLine($"[RELAY APP] Starting local listener on port {localPort}...");
                
                _localListener = new TcpListener(System.Net.IPAddress.Loopback, LocalPort);
                _localListener.Start();

                Console.WriteLine($"[RELAY APP] [OK] Local listener started");
                
                Logging.Debug("RelayConnection", "ConnectToSession",
                    $"Started local listener on port {LocalPort}");

                // 5. Start accepting local connections - each will get its own backend connection
                Console.WriteLine($"[RELAY APP] Step 3: Accepting local connections...");
                
                _ = AcceptLocalConnectionsAsync(_cancellationTokenSource.Token);

                // Start throughput timer (updates every second)
                StartThroughputTimer();
                
                // Start ping timer (updates every 2 seconds)
                StartPingTimer();
                
                // Start kick status polling (checks every 5 seconds)
                StartKickStatusPolling();

                IsConnected = true;
                
                Console.WriteLine($"[RELAY APP] Connection flow complete - ready to relay!");
                Console.WriteLine($"[RELAY APP] ========================================");
                
                Logging.Debug("RelayConnection", "ConnectToSession",
                    $"Ready to accept connections for session {sessionId} on port {localPort}");
                
                return (true, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RELAY APP] Connection failed: {ex.Message}");
                Logging.Error("RelayConnection", "ConnectToSession", ex.ToString());
                return (false, ex.Message);
            }
        }
        
        /// <summary>
        /// Announces the Admin connection to the backend via HTTP BEFORE establishing TCP connection
        /// This ensures the Agent is notified and ready, preventing race conditions
        /// </summary>
        private async Task<(bool success, string error)> AnnounceConnectionAsync(
            string sessionId, 
            string adminPublicKeyPem)
        {
            try
            {
                Console.WriteLine($"[RELAY APP] Announcing connection to session {sessionId}...");
                
                Logging.Info("RelayConnection", "AnnounceConnectionAsync",
                    $"Announcing connection to session {sessionId}");
                
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10); // 10 second timeout
                
                // Request Body
                var announceRequest = new
                {
                    session_id = sessionId,
                    api_key = _apiKey,
                    hardware_id = _hardwareId,
                    admin_public_key = adminPublicKeyPem
                };
                
                string jsonBody = JsonSerializer.Serialize(announceRequest);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                
                // URL: https://your-backend.com/admin/relay/announce
                string announceUrl = $"{_backendUrl}/admin/relay/announce";
                
                Console.WriteLine($"[RELAY APP] Sending HTTP POST to {announceUrl}");
                Console.WriteLine($"[RELAY APP] Admin Public Key length: {adminPublicKeyPem.Length}");
                
                Logging.Debug("RelayConnection", "AnnounceConnectionAsync",
                    $"POST {announceUrl} with admin_public_key (length: {adminPublicKeyPem.Length})");
                
                var response = await httpClient.PostAsync(announceUrl, content);
                string responseBody = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[RELAY APP] Announce failed: {response.StatusCode} - {responseBody}");
                    
                    Logging.Error("RelayConnection", "AnnounceConnectionAsync",
                        $"HTTP {(int)response.StatusCode}: {responseBody}");
                    
                    return (false, $"HTTP {(int)response.StatusCode}: {responseBody}");
                }
                
                Console.WriteLine($"[RELAY APP] Connection announced successfully!");
                Console.WriteLine($"[RELAY APP] Response: {responseBody}");
                
                Logging.Info("RelayConnection", "AnnounceConnectionAsync",
                    $"Connection announced - agent should be connecting now");
                
                return (true, null);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RELAY APP] Announce failed: {ex.Message}");
                Logging.Error("RelayConnection", "AnnounceConnectionAsync", ex.ToString());
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Accepts local connections and forwards them through individual TCP relays
        /// </summary>
        private async Task AcceptLocalConnectionsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _localListener != null)
            {
                try
                {
                    var localClient = await _localListener.AcceptTcpClientAsync();
                    
                    int currentCount;
                    lock (_connectionCountLock)
                    {
                        _activeConnectionCount++;
                        currentCount = _activeConnectionCount;
                    }
                    
                    Logging.Debug("RelayConnection", "AcceptLocalConnectionsAsync",
                        $"Accepted local connection for session {SessionId} (active: {currentCount})");
                    
                    // Notify subscribers that connection count changed
                    ActiveConnectionsChanged?.Invoke(this, currentCount);
                    
                    // Handle this connection with its own TCP tunnel to backend
                    _ = HandleLocalConnectionAsync(localClient, cancellationToken);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
                {
                    // Listener was stopped
                    Logging.Debug("RelayConnection", "AcceptLocalConnectionsAsync",
                        "Listener stopped");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Listener was disposed
                    Logging.Debug("RelayConnection", "AcceptLocalConnectionsAsync",
                        "Listener disposed");
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Logging.Error("RelayConnection", "AcceptLocalConnectionsAsync", ex.ToString());
                        
                        // If this is a critical error and we're not manually disconnecting, trigger reconnect
                        if (!_manualDisconnect && IsConnected)
                        {
                            Logging.Error("RelayConnection", "AcceptLocalConnectionsAsync",
                                "Local listener failed, triggering reconnect");
                            ConnectionLost?.Invoke(this, $"Listener error: {ex.Message}");
                            IsConnected = false;
                        }
                        break;
                    }
                }
            }
            
            Logging.Debug("RelayConnection", "AcceptLocalConnectionsAsync",
                "Stopped accepting connections");
        }

        /// <summary>
        /// Handles a single local connection by establishing its own TCP tunnel to backend
        /// </summary>
        private async Task HandleLocalConnectionAsync(TcpClient localClient, CancellationToken cancellationToken)
        {
            TcpClient? backendClient = null;
            Stream? backendStream = null;
            
            try
            {
                // Disable Nagle's algorithm for low-latency communication (important for RDP)
                localClient.NoDelay = true;

                Logging.Debug("RelayConnection", "HandleLocalConnectionAsync",
                    $"Establishing new TCP tunnel to backend for session {SessionId}");

                // Establish new TCP connection to backend for this local connection
                backendClient = new TcpClient();
                backendClient.NoDelay = true;
                
                // TCP Optimizations for E2EE
                backendClient.ReceiveBufferSize = 131072; // 128KB
                backendClient.SendBufferSize = 131072; // 128KB
                
                await backendClient.ConnectAsync(_backendHost, _relayPort);
                
                // Enable TCP KeepAlive
                backendClient.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, 
                    System.Net.Sockets.SocketOptionName.KeepAlive, true);
                
                Logging.Debug("RelayConnection", "HandleLocalConnectionAsync",
                    $"TCP connected to backend {_backendHost}:{_relayPort}");

                // Establish TLS connection for transport security
                // Use the configured TLS setting from user preferences
                bool useTls = _useRelayTls;
                
                Stream networkStream;
                
                if (useTls)
                {
                    Console.WriteLine($"[RELAY TLS] Establishing TLS connection to {_backendHost}...");
                    
                    var sslStream = new SslStream(
                        backendClient.GetStream(),
                        false,
                        (sender, certificate, chain, errors) =>
                        {
                            // Validate server certificate
                            if (errors == SslPolicyErrors.None)
                            {
                                Console.WriteLine($"[RELAY TLS] Server certificate valid");
                                return true;
                            }
                            
                            // Do not allow self-signed certificates (common in internal networks)
                            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
                            {
                                Logging.Error("RelayConnection", "TLS Certificate Validation",
                                    "Certificate chain error - rejecting (probably self-signed)");
                                Console.WriteLine($"[RELAY TLS] Certificate chain error - rejecting. Probably self signed.");
                                return false;
                            }
                            
                            // Check for certificate name mismatch (IP vs hostname)
                            if (errors == SslPolicyErrors.RemoteCertificateNameMismatch)
                            {
                                Console.WriteLine($"[RELAY TLS] Certificate name mismatch (common with IP addresses) - accepting");
                                Logging.Debug("RelayConnection", "TLS Certificate Validation",
                                    "Accepting certificate despite name mismatch (IP-based connection)");
                                return true;
                            }
                            
                            Console.WriteLine($"[RELAY TLS] Certificate validation failed: {errors}");
                            Logging.Error("RelayConnection", "TLS Certificate Validation",
                                $"Certificate validation failed: {errors}");
                            return false;
                        },
                        null
                    );
                    
                    try
                    {
                        Console.WriteLine($"[RELAY TLS] Starting TLS handshake...");
                        await sslStream.AuthenticateAsClientAsync(
                            _backendHost,
                            null,
                            System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                            true
                        );
                        
                        Console.WriteLine($"[RELAY TLS] TLS connection established");
                        Console.WriteLine($"[RELAY TLS] Protocol: {sslStream.SslProtocol}");
                        Console.WriteLine($"[RELAY TLS] Cipher: {sslStream.CipherAlgorithm} ({sslStream.CipherStrength} bits)");
                        Console.WriteLine($"[RELAY TLS] Hash: {sslStream.HashAlgorithm} ({sslStream.HashStrength} bits)");
                        
                        Logging.Info("RelayConnection", "TLS Established",
                            $"Protocol: {sslStream.SslProtocol}, Cipher: {sslStream.CipherAlgorithm} ({sslStream.CipherStrength} bits)");
                        
                        networkStream = sslStream;
                        backendStream = sslStream;
                    }
                    catch (Exception tlsEx)
                    {
                        Console.WriteLine($"[RELAY TLS] TLS handshake failed: {tlsEx.Message}");
                        Logging.Error("RelayConnection", "TLS Handshake Failed", tlsEx.ToString());
                        
                        sslStream?.Dispose();
                        throw new Exception($"TLS handshake failed: {tlsEx.Message}", tlsEx);
                    }
                }
                else
                {
                    Console.WriteLine($"[RELAY] Using plaintext connection (HTTPS not configured)");
                    Logging.Debug("RelayConnection", "Connection Mode",
                        "Using plaintext connection - TLS disabled (backend URL does not use HTTPS)");
                    
                    networkStream = backendClient.GetStream();
                    backendStream = networkStream;
                }
                
                Logging.Debug("RelayConnection", "HandleLocalConnectionAsync",
                    $"Connected to backend {_backendHost}:{_relayPort} (TLS: {useTls})");

                // Ensure Admin Public Key is present (regenerate as fallback)
                string adminPublicKeyToSend = null;
                lock (_globalKeypairLock)
                {
                    if (_globalAdminPrivateKey == null || string.IsNullOrEmpty(_globalAdminPublicKeyPem))
                    {
                        // Not expected, but regenerate as fallback
                        Console.WriteLine($"[RELAY APP WARNING] Admin keypair missing in HandleLocalConnectionAsync - regenerating!");
                        Logging.Error("RelayConnection", "HandleLocalConnectionAsync",
                            "Admin keypair missing - regenerating (this should not happen!)");
                        
                        var (adminRsa, adminPublicKeyPem) = RelayKeyGenerator.GenerateKeyPair();
                        _globalAdminPrivateKey = adminRsa;
                        _globalAdminPublicKeyPem = adminPublicKeyPem;
                    }
                    
                    adminPublicKeyToSend = _globalAdminPublicKeyPem;
                }
                
                Console.WriteLine($"[RELAY APP] Sending Admin Public Key (length: {adminPublicKeyToSend?.Length ?? 0})");
                Console.WriteLine($"[RELAY APP] Admin Public Key (first 100 chars): {adminPublicKeyToSend?.Substring(0, Math.Min(100, adminPublicKeyToSend?.Length ?? 0))}");

                // Send authentication JSON
                var authData = new
                {
                    session_id = SessionId,
                    api_key = _apiKey,
                    hardware_id = _hardwareId,
                    admin_public_key = adminPublicKeyToSend // Send global Admin Public Key for E2EE (always on connect)
                };
                
                string authJson = JsonSerializer.Serialize(authData);
                byte[] authBytes = Encoding.UTF8.GetBytes(authJson);
                
                Logging.Debug("RelayConnection", "HandleLocalConnectionAsync",
                    $"Sending auth with GLOBAL Admin Public Key for session {SessionId} (length: {adminPublicKeyToSend?.Length ?? 0})");
                
                await backendStream.WriteAsync(authBytes, 0, authBytes.Length, cancellationToken);
                await backendStream.FlushAsync(cancellationToken);
                
                // Read authentication response
                byte[] responseBuffer = new byte[8192]; // Größer für Public Key
                int bytesRead = await backendStream.ReadAsync(responseBuffer, 0, 
                    responseBuffer.Length, cancellationToken);
                
                if (bytesRead == 0)
                {
                    Logging.Error("RelayConnection", "HandleLocalConnectionAsync",
                        "Backend closed connection during authentication");
                    return;
                }
                
                string responseJson = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);
                Logging.Debug("RelayConnection", "HandleLocalConnectionAsync",
                    $"Auth response: {responseJson}");
                
                var response = JsonSerializer.Deserialize<AuthResponse>(responseJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (response?.success != true)
                {
                    string errorMsg = response?.message ?? "Authentication failed";
                    Logging.Error("RelayConnection", "HandleLocalConnectionAsync", 
                        $"Auth failed: {errorMsg}");
                    return;
                }

                // E2EE using keys provided by the server
                RelayEncryption? encryption = null;
                
                Console.WriteLine($"[RELAY APP E2EE] Checking for E2EE initialization...");
                Console.WriteLine($"[RELAY APP E2EE] Agent Public Key present: {!string.IsNullOrEmpty(response.agent_public_key)}");
                Console.WriteLine($"[RELAY APP E2EE] Admin Private Key present: {_globalAdminPrivateKey != null}");
                
                if (!string.IsNullOrEmpty(response.agent_public_key) && _globalAdminPrivateKey != null)
                {
                    Console.WriteLine($"[RELAY APP E2EE] Agent Public Key length: {response.agent_public_key.Length}");
                    Console.WriteLine($"[RELAY APP E2EE] Agent Public Key (first 100 chars): {response.agent_public_key.Substring(0, Math.Min(100, response.agent_public_key.Length))}");
                    Console.WriteLine($"[RELAY APP E2EE] Admin Private Key size: {_globalAdminPrivateKey.KeySize} bits");
                    
                    Logging.Info("RelayConnection", "HandleLocalConnectionAsync",
                        $"Initializing E2EE with Agent Public Key from server (length: {response.agent_public_key.Length})");
                    
                    try
                    {
                        // Importiere Agent Public Key vom Server
                        var agentPublicKey = RelayKeyGenerator.ImportPublicKey(response.agent_public_key);
                        
                        Console.WriteLine($"[RELAY APP E2EE] Agent Public Key imported successfully ({agentPublicKey.KeySize} bits)");
                        
                        // Initialize E2EE: Admin encrypts for Agent, Admin decrypts from Agent
                        encryption = new RelayEncryption(_globalAdminPrivateKey, agentPublicKey);
                        
                        Console.WriteLine($"[RELAY APP E2EE] E2EE initialized successfully");
                        
                        Logging.Info("RelayConnection", "HandleLocalConnectionAsync",
                            "[OK] E2EE initialized successfully (Admin <-> Agent)");
                        Logging.Info("RelayConnection", "HandleLocalConnectionAsync",
                            $"Admin Private Key size: {_globalAdminPrivateKey.KeySize} bits");
                        Logging.Info("RelayConnection", "HandleLocalConnectionAsync",
                            $"Agent Public Key size: {agentPublicKey.KeySize} bits");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RELAY APP E2EE] Failed to initialize E2EE: {ex.Message}");
                        Logging.Error("RelayConnection", "HandleLocalConnectionAsync",
                            $"Failed to initialize E2EE: {ex.Message}");
                        Logging.Error("RelayConnection", "HandleLocalConnectionAsync",
                            $"Stack trace: {ex.StackTrace}");
                    }
                }
                else
                {
                    Console.WriteLine($"[RELAY APP E2EE] Warning: E2EE not initialized - missing keys");
                    Logging.Error("RelayConnection", "HandleLocalConnectionAsync",
                        "E2EE not initialized - missing keys");
                }

                Logging.Debug("RelayConnection", "HandleLocalConnectionAsync",
                    "Authentication successful, starting bidirectional relay");

                var localStream = localClient.GetStream();

                // Bidirectional relay through this TCP connection (mit E2EE falls verfügbar)
                var localToRelay = RelayLocalToRemoteAsync(localStream, backendStream, cancellationToken, encryption);
                var relayToLocal = RelayRemoteToLocalAsync(backendStream, localStream, cancellationToken, encryption);

                await Task.WhenAny(localToRelay, relayToLocal);
                
                Logging.Debug("RelayConnection", "HandleLocalConnectionAsync",
                    $"Connection closed for session {SessionId}");
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Logging.Error("RelayConnection", "HandleLocalConnectionAsync", ex.ToString());
                }
            }
            finally
            {
                // Cleanup this connection
                backendStream?.Dispose();
                backendClient?.Dispose();
                localClient?.Dispose();
                
                int currentCount;
                lock (_connectionCountLock)
                {
                    _activeConnectionCount--;
                    currentCount = _activeConnectionCount;
                }
                
                Logging.Debug("RelayConnection", "HandleLocalConnectionAsync",
                    $"Cleaned up connection resources (active: {currentCount})");
                
                // Notify subscribers that connection count changed
                ActiveConnectionsChanged?.Invoke(this, currentCount);
            }
        }
        
        private class AuthResponse
        {
            public bool success { get; set; }
            public string? message { get; set; }
            public string? agent_public_key { get; set; } // Agent Public Key for E2EE
        }

        /// <summary>
        /// Relays data from local TCP stream to remote TCP stream with Length-Prefix Protocol for E2EE
        /// </summary>
        private async Task RelayLocalToRemoteAsync(Stream localStream,
            Stream remoteStream, CancellationToken cancellationToken, RelayEncryption? encryption = null)
        {
            try
            {
                // IMPORTANT: Send encrypted Session-Key as FIRST packet (if E2EE)
                if (encryption != null)
                {
                    try
                    {
                        byte[] encryptedSessionKey = encryption.ExportEncryptedSessionKey();
                        
                        // Send as special packet: [Length:4][EncryptedSessionKey]
                        // Agent recognizes the first packet as Session-Key
                        byte[] lengthPrefix = BitConverter.GetBytes(encryptedSessionKey.Length);
                        await remoteStream.WriteAsync(lengthPrefix, 0, 4, cancellationToken);
                        await remoteStream.WriteAsync(encryptedSessionKey, 0, encryptedSessionKey.Length, cancellationToken);
                        await remoteStream.FlushAsync(cancellationToken);
                        
                        Console.WriteLine($"[RELAY APP E2EE] Session-Key sent to Agent ({encryptedSessionKey.Length} bytes, RSA-encrypted)");
                        Logging.Info("RelayConnection", "RelayLocalToRemoteAsync",
                            "Admin->Agent: Encrypted data sent (AES-256-GCM)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RELAY APP E2EE] Failed to send Session-Key: {ex.Message}");
                        Logging.Error("RelayConnection", "RelayLocalToRemoteAsync",
                            $"Failed to send Session-Key: {ex.Message}");
                        throw;
                    }
                }

                byte[] buffer = new byte[65536]; // 64KB buffer for better performance
                int bytesRead;

                while (!cancellationToken.IsCancellationRequested &&
                       (bytesRead = await localStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    // Encrypt data if E2EE is active
                    if (encryption != null)
                    {
                        try
                        {
                            // Create exact copy of read data
                            byte[] plaintext = new byte[bytesRead];
                            Buffer.BlockCopy(buffer, 0, plaintext, 0, bytesRead);
                            
                            // Encrypt with optimized AES-256-GCM (Format: [Nonce:12][Tag:16][Ciphertext])
                            byte[] ciphertext = encryption.Encrypt(plaintext);
                            
                            // Send with Length-Prefix (TCP needs this!): [Length:4][EncryptedData]
                            byte[] lengthPrefix = BitConverter.GetBytes(ciphertext.Length);
                            await remoteStream.WriteAsync(lengthPrefix, 0, 4, cancellationToken);
                            await remoteStream.WriteAsync(ciphertext, 0, ciphertext.Length, cancellationToken);
                            await remoteStream.FlushAsync(cancellationToken);

                            Logging.Info("RelayConnection", "RelayLocalToRemoteAsync",
                                "Admin->Agent: Encrypted data sent (AES-256-GCM)");
                        }
                        catch (Exception ex)
                        {
                            Logging.Error("RelayConnection", "RelayLocalToRemoteAsync",
                                $"ERROR Encryption failed: {ex.Message}\nStack: {ex.StackTrace}");
                            throw;
                        }
                    }
                    else
                    {
                        // No encryption - send directly
                        await remoteStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        await remoteStream.FlushAsync(cancellationToken);
                    }
                    
                    // Track upload bytes (original size)
                    lock (_throughputLock)
                    {
                        _totalBytesUpload += bytesRead;
                    }

                    Logging.Debug("RelayConnection", "RelayLocalToRemoteAsync",
                        $"Local->Remote: Sent {bytesRead} bytes{(encryption != null ? " (encrypted)" : "")}");
                }
                
                Logging.Debug("RelayConnection", "RelayLocalToRemoteAsync", 
                    "Local->Remote stream closed normally");
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Logging.Debug("RelayConnection", "RelayLocalToRemoteAsync", 
                        $"Local->Remote error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Relays data from remote TCP stream to local TCP stream with Length-Prefix Protocol for E2EE
        /// </summary>
        private async Task RelayRemoteToLocalAsync(Stream remoteStream,
            Stream localStream, CancellationToken cancellationToken, RelayEncryption? encryption = null)
        {
            try
            {
                byte[] lengthPrefix = new byte[4];

                while (!cancellationToken.IsCancellationRequested)
                {
                    // 🔐 Decrypt data if E2EE is active
                    if (encryption != null)
                    {
                        try
                        {
                            // Read Length-Prefix
                            int read = await ReadExactAsync(remoteStream, lengthPrefix, 0, 4, cancellationToken);
                            if (read == 0) break; // Stream ended
                            
                            int encryptedLength = BitConverter.ToInt32(lengthPrefix, 0);
                            
                            // Validation
                            if (encryptedLength <= 0 || encryptedLength > 1048576) // Max 1MB
                            {
                                Logging.Error("RelayConnection", "RelayRemoteToLocalAsync",
                                    $"Invalid encrypted length: {encryptedLength}");
                                throw new InvalidDataException($"Invalid encrypted packet length: {encryptedLength}");
                            }
                            
                            // Read the complete encrypted packet
                            byte[] ciphertext = new byte[encryptedLength];
                            await ReadExactAsync(remoteStream, ciphertext, 0, encryptedLength, cancellationToken);
                            
                            // Decrypt
                            byte[] plaintext = encryption.Decrypt(ciphertext);
                            
                            // Send decrypted plaintext
                            await localStream.WriteAsync(plaintext, 0, plaintext.Length, cancellationToken);
                            await localStream.FlushAsync(cancellationToken);
                            
                            // Track download bytes (decrypted size)
                            lock (_throughputLock)
                            {
                                _totalBytesDownload += plaintext.Length;
                            }

                            Logging.Debug("RelayConnection", "RelayRemoteToLocalAsync",
                                $"Remote->Local: Decrypted {encryptedLength} bytes -> {plaintext.Length} bytes");
                        }
                        catch (Exception ex)
                        {
                            Logging.Error("RelayConnection", "RelayRemoteToLocalAsync",
                                $"Decryption failed: {ex.Message}");
                            throw;
                        }
                    }
                    else
                    {
                        // No decryption - direct relay
                        byte[] buffer = new byte[65536];
                        int bytesRead = await remoteStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        if (bytesRead == 0) break;
                        
                        await localStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        await localStream.FlushAsync(cancellationToken);
                        
                        lock (_throughputLock)
                        {
                            _totalBytesDownload += bytesRead;
                        }

                        Logging.Debug("RelayConnection", "RelayRemoteToLocalAsync",
                            $"Remote->Local: Sent {bytesRead} bytes");
                    }
                }
                
                Logging.Debug("RelayConnection", "RelayRemoteToLocalAsync", 
                    "Remote->Local stream closed normally");
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Logging.Debug("RelayConnection", "RelayRemoteToLocalAsync", 
                        $"Remote->Local error: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Liest exakt die angegebene Anzahl Bytes aus einem Stream (für Length-Prefix-Protokoll)
        /// </summary>
        private async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken);
                if (read == 0) return totalRead; // Stream ended
                totalRead += read;
            }
            return totalRead;
        }

        /// <summary>
        /// Disconnects from the relay session and closes the local listener
        /// Active connections will be terminated
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                _manualDisconnect = true;
                
                // Notify server about disconnect (cleanup admin connection)
                if (!string.IsNullOrEmpty(SessionId))
                {
                    try
                    {
                        Logging.Info("RelayConnection", "DisconnectAsync", 
                            $"Notifying server about disconnect for session {SessionId}");
                        
                        var apiClient = new RelayApiClient(
                            $"http://{_backendHost}:7080", 
                            _apiKey, 
                            _hardwareId);
                        
                        var (success, error) = await apiClient.DisconnectFromSession(SessionId);
                        
                        if (success)
                        {
                            Logging.Info("RelayConnection", "DisconnectAsync", 
                                "[OK] Server notified about disconnect for session {SessionId}");
                        }
                        else
                        {
                            Logging.Error("RelayConnection", "DisconnectAsync", 
                                $"Failed to notify server about disconnect: {error}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logging.Error("RelayConnection", "DisconnectAsync", 
                            $"Error notifying server about disconnect: {ex.Message}");
                    }
                }
                
                _cancellationTokenSource?.Cancel();
                _localListener?.Stop();
                
                // Stop throughput timer
                StopThroughputTimer();
                
                // Stop ping timer
                StopPingTimer();
                
                // Stop kick status polling
                StopKickStatusPolling();
                
                // 🔐 Cleanup E2EE resources (nur lokale Referenzen, NICHT globale Keys!)
                _agentPublicKey?.Dispose();
                _agentPublicKey = null;
                _relayEncryption?.Dispose();
                _relayEncryption = null;
                
                // Important: Global Admin Keys MUST NOT be deleted - reused for all sessions
                // _globalAdminPrivateKey remains for future connections
                
                IsConnected = false;
                IsReconnecting = false;
                
                Logging.Debug("RelayConnection", "DisconnectAsync", 
                    $"Disconnected from session {SessionId}, stopped local listener on port {LocalPort}");
            }
            catch (Exception ex)
            {
                Logging.Error("RelayConnection", "DisconnectAsync", ex.ToString());
            }
        }
        
        /// <summary>
        /// Synchronous disconnect method (for backward compatibility)
        /// </summary>
        public void Disconnect()
        {
            // Call async version synchronously
            DisconnectAsync().GetAwaiter().GetResult();
        }
        
        /// <summary>
        /// Starts the throughput timer that calculates bytes/second
        /// </summary>
        private void StartThroughputTimer()
        {
            _throughputTimer = new System.Timers.Timer(1000); // Update every second
            _throughputTimer.Elapsed += (sender, e) =>
            {
                long uploadSpeed, downloadSpeed;
                
                lock (_throughputLock)
                {
                    // Calculate speed from total bytes difference
                    uploadSpeed = _totalBytesUpload - _bytesUploadLastSecond;
                    downloadSpeed = _totalBytesDownload - _bytesDownloadLastSecond;
                    
                    // Store for next calculation
                    _bytesUploadLastSecond = _totalBytesUpload;
                    _bytesDownloadLastSecond = _totalBytesDownload;
                }
                
                // Notify subscribers of throughput update
                ThroughputChanged?.Invoke(this, (uploadSpeed, downloadSpeed));
            };
            
            _throughputTimer.Start();
        }
        
        /// <summary>
        /// Stops the throughput timer
        /// </summary>
        private void StopThroughputTimer()
        {
            if (_throughputTimer != null)
            {
                _throughputTimer.Stop();
                _throughputTimer.Dispose();
                _throughputTimer = null;
            }
        }
        
        /// <summary>
        /// Starts the ping timer that measures latency
        /// </summary>
        private void StartPingTimer()
        {
            _pingTimer = new System.Timers.Timer(2000); // Ping every 2 seconds
            _pingTimer.Elapsed += async (sender, e) =>
            {
                await MeasurePingAsync();
            };
            
            _pingTimer.Start();
        }
        
        /// <summary>
        /// Stops the ping timer
        /// </summary>
        private void StopPingTimer()
        {
            if (_pingTimer != null)
            {
                _pingTimer.Stop();
                _pingTimer.Dispose();
                _pingTimer = null;
            }
        }
        
        /// <summary>
        /// Measures ping/latency to the backend server
        /// </summary>
        private async Task MeasurePingAsync()
        {
            if (!IsConnected || string.IsNullOrEmpty(SessionId))
                return;
            
            TcpClient? pingClient = null;
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                
                // Try to connect to backend
                pingClient = new TcpClient();
                await pingClient.ConnectAsync(_backendHost, _relayPort);
                
                stopwatch.Stop();
                long pingMs = stopwatch.ElapsedMilliseconds;
                
                lock (_pingLock)
                {
                    _currentPingMs = pingMs;
                    
                    // Keep history of last 10 pings for average
                    _pingHistory.Add(pingMs);
                    if (_pingHistory.Count > 10)
                        _pingHistory.RemoveAt(0);
                    
                    // Calculate average
                    _averagePingMs = _pingHistory.Count > 0 
                        ? (long)_pingHistory.Average() 
                        : pingMs;
                }
                
                // Notify subscribers
                PingChanged?.Invoke(this, (_currentPingMs, _averagePingMs));
                
                Logging.Debug("RelayConnection", "MeasurePingAsync",
                    $"Ping: {pingMs}ms, Average: {_averagePingMs}ms");
            }
            catch (Exception ex)
            {
                // Connection failed - set high ping
                lock (_pingLock)
                {
                    _currentPingMs = 9999;
                    _averagePingMs = 9999;
                }
                
                Logging.Debug("RelayConnection", "MeasurePingAsync",
                    $"Ping failed: {ex.Message}");
            }
            finally
            {
                pingClient?.Dispose();
            }
        }
        
        /// <summary>
        /// Attempts to reconnect to the relay session
        /// </summary>
        public async Task<(bool success, string? error)> ReconnectAsync()
        {
            if (IsReconnecting)
            {
                return (false, "Reconnect already in progress");
            }
            
            if (string.IsNullOrEmpty(SessionId) || LocalPort == 0)
            {
                return (false, "Invalid session data");
            }
            
            IsReconnecting = true;
            _manualDisconnect = false;
            
            Logging.Info("RelayConnection", "ReconnectAsync",
                $"Attempting to reconnect session {SessionId} on port {LocalPort}");
            
            for (int attempt = 1; attempt <= MAX_RECONNECT_ATTEMPTS; attempt++)
            {
                try
                {
                    Logging.Debug("RelayConnection", "ReconnectAsync",
                        $"Reconnect attempt {attempt}/{MAX_RECONNECT_ATTEMPTS}");
                    
                    // Clean up old resources
                    try
                    {
                        _cancellationTokenSource?.Cancel();
                        _localListener?.Stop();
                    }
                    catch { }
                    
                    // Wait before retry (except first attempt)
                    if (attempt > 1)
                    {
                        await Task.Delay(RECONNECT_DELAY_MS * attempt);
                    }
                    
                    // Try to reconnect
                    _cancellationTokenSource = new CancellationTokenSource();
                    
                    _localListener = new TcpListener(System.Net.IPAddress.Loopback, LocalPort);
                    _localListener.Start();
                    
                    Logging.Debug("RelayConnection", "ReconnectAsync",
                        $"Restarted local listener on port {LocalPort}");
                    
                    // Start accepting local connections
                    _ = AcceptLocalConnectionsAsync(_cancellationTokenSource.Token);
                    
                    IsConnected = true;
                    IsReconnecting = false;
                    
                    Logging.Info("RelayConnection", "ReconnectAsync",
                        $"Successfully reconnected session {SessionId} on attempt {attempt}");
                    
                    Reconnected?.Invoke(this, EventArgs.Empty);
                    
                    return (true, null);
                }
                catch (Exception ex)
                {
                    Logging.Error("RelayConnection", "ReconnectAsync",
                        $"Reconnect attempt {attempt} failed: {ex.Message}");
                    
                    if (attempt == MAX_RECONNECT_ATTEMPTS)
                    {
                        IsReconnecting = false;
                        IsConnected = false;
                        
                        string error = $"Failed to reconnect after {MAX_RECONNECT_ATTEMPTS} attempts: {ex.Message}";
                        Logging.Error("RelayConnection", "ReconnectAsync", error);
                        return (false, error);
                    }
                }
            }
            
            IsReconnecting = false;
            return (false, "Reconnect failed");
        }
        
        /// <summary>
        /// Starts the kick status polling timer
        /// </summary>
        private void StartKickStatusPolling()
        {
            _kickStatusTimer = new System.Timers.Timer(KICK_STATUS_CHECK_INTERVAL_MS);
            _kickStatusTimer.Elapsed += async (sender, e) =>
            {
                await CheckKickStatus();
            };
            
            _kickStatusTimer.Start();
            
            Logging.Debug("RelayConnection", "StartKickStatusPolling",
                $"Started kick status polling for session {SessionId}");
        }
        
        /// <summary>
        /// Stops the kick status polling timer
        /// </summary>
        private void StopKickStatusPolling()
        {
            if (_kickStatusTimer != null)
            {
                _kickStatusTimer.Stop();
                _kickStatusTimer.Dispose();
                _kickStatusTimer = null;
                
                Logging.Debug("RelayConnection", "StopKickStatusPolling",
                    "Stopped kick status polling");
            }
        }
        
        /// <summary>
        /// Checks if this session has been kicked by the server
        /// </summary>
        private async Task CheckKickStatus()
        {
            try
            {
                if (!IsConnected || string.IsNullOrEmpty(SessionId))
                    return;
                
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(3); // Quick timeout
                
                string url = $"{_backendUrl}/admin/relay/session/status";
                
                // Build JSON request with authentication
                var requestData = new
                {
                    session_id = SessionId,
                    api_key = _apiKey,
                    hardware_id = _hardwareId
                };
                
                string requestJson = JsonSerializer.Serialize(requestData);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                
                var response = await httpClient.PostAsync(url, content);
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Session doesn't exist (anymore) - disconnect
                    Logging.Error("RelayConnection", "CheckKickStatus",
                        "Session not found on server - disconnecting");
                    
                    await DisconnectAsync();
                    ConnectionLost?.Invoke(this, "Session no longer exists on server");
                    return;
                }
                
                if (!response.IsSuccessStatusCode)
                {
                    Logging.Debug("RelayConnection", "CheckKickStatus",
                        $"HTTP {(int)response.StatusCode}: Failed to check kick status");
                    return;
                }
                
                string responseJson = await response.Content.ReadAsStringAsync();
                var kickStatus = JsonSerializer.Deserialize<KickStatusResponse>(responseJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (kickStatus?.kicked == true)
                {
                    // KICKED! Disconnect and notify UI
                    Logging.Error("RelayConnection", "CheckKickStatus",
                        $"Session {SessionId} was kicked: {kickStatus.reason}");
                    
                    // Stop polling first
                    StopKickStatusPolling();
                    
                    // Trigger OnKicked event BEFORE disconnect (so UI can show proper message)
                    OnKicked?.Invoke(this, new KickEventArgs
                    {
                        SessionId = SessionId,
                        Reason = kickStatus.reason ?? "Unknown reason",
                        KickedAt = kickStatus.kicked_at,
                        KickedBy = kickStatus.kicked_by
                    });
                    
                    // Cleanup connection
                    await DisconnectAsync();
                }
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                // Network error - ignore, next poll attempt in 5 seconds
                Logging.Debug("RelayConnection", "CheckKickStatus",
                    $"Network error during kick status check: {ex.Message}");
            }
            catch (JsonException ex)
            {
                // Invalid JSON - log but continue
                Logging.Error("RelayConnection", "CheckKickStatus",
                    $"Failed to parse kick status response: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logging.Error("RelayConnection", "CheckKickStatus", ex.ToString());
            }
        }
        
        /// <summary>
        /// Response DTO for kick status check
        /// </summary>
        private class KickStatusResponse
        {
            public bool kicked { get; set; }
            public string? reason { get; set; }
            public DateTime? kicked_at { get; set; }
            public string? kicked_by { get; set; }
        }
    }
    
    /// <summary>
    /// Event arguments for OnKicked event
    /// </summary>
    public class KickEventArgs : EventArgs
    {
        public string SessionId { get; set; } = "";
        public string Reason { get; set; } = "";
        public DateTime? KickedAt { get; set; }
        public string? KickedBy { get; set; }
    }
}


using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NetLock_RMM_Server.SignalR;
using System.Text.Json;
using MySqlConnector;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NetLock_RMM_Server.Configuration;

namespace NetLock_RMM_Server.Relay
{
    /// <summary>
    /// Relay Server for port forwarding between admin and target clients
    /// ALL connections (Admin & Target) run over Port 7443
    /// </summary>
    public class RelayServer
    {
        private static readonly Lazy<RelayServer> _instance = new Lazy<RelayServer>(() => new RelayServer());
        public static RelayServer Instance => _instance.Value;
        
        private readonly ConcurrentDictionary<string, RelaySession> _activeSessions = new();
        private TcpListener _mainListener; // One listener for Port Server.relay_port
        private CancellationTokenSource _listenerCts;
        private readonly ConcurrentDictionary<string, PendingConnection> _pendingConnections = new();
        private readonly ConcurrentDictionary<string, TargetTunnel> _targetTunnels = new();
        
        // Single-Admin-Policy: Tracking of current admin per device
        private readonly ConcurrentDictionary<string, AdminDeviceBinding> _adminDeviceBindings = new();
        
        // Kicked Sessions: Session-ID → Kick-Info (queried by Relay App)
        private readonly ConcurrentDictionary<string, KickInfo> _kickedSessions = new();
        
        // IP Whitelist for admin connections
        private List<string> _relayAllowedIps = new List<string>();
        
        // TLS Configuration
        private X509Certificate2? _serverCertificate;
        private bool _tlsEnabled = false;

        private RelayServer() { }
        
        /// <summary>
        /// Loads the TLS certificate from configuration (appsettings.json)
        /// </summary>
        public void LoadTlsCertificate(string certPath, string certPassword)
        {
            try
            {
                if (string.IsNullOrEmpty(certPath))
                {
                    Logging.Handler.Warning("RelayServer", "LoadTlsCertificate", "No certificate path configured - TLS disabled");
                    _tlsEnabled = false;
                    return;
                }
                
                if (!File.Exists(certPath))
                {
                    Logging.Handler.Error("RelayServer", "LoadTlsCertificate", $"Certificate file not found: {certPath}");
                    _tlsEnabled = false;
                    return;
                }
                
                // Load certificate (PFX/PKCS12 Format)
                _serverCertificate = string.IsNullOrEmpty(certPassword)
                    ? new X509Certificate2(certPath)
                    : new X509Certificate2(certPath, certPassword);
                    
                _tlsEnabled = true;

                Logging.Handler.Info("RelayServer", "LoadTlsCertificate", "TLS certificate loaded successfully");
                Logging.Handler.Debug("RelayServer", "LoadTlsCertificate", $"Subject: {_serverCertificate.Subject}");
                Logging.Handler.Debug("RelayServer", "LoadTlsCertificate", $"Issuer: {_serverCertificate.Issuer}");
                Logging.Handler.Debug("RelayServer", "LoadTlsCertificate", $"Valid from: {_serverCertificate.NotBefore:yyyy-MM-dd}");
                Logging.Handler.Debug("RelayServer", "LoadTlsCertificate", $"Valid until: {_serverCertificate.NotAfter:yyyy-MM-dd}");
                Logging.Handler.Debug("RelayServer", "LoadTlsCertificate", $"Has private key: {_serverCertificate.HasPrivateKey}");
                
                // Warn if certificate expires soon
                var daysUntilExpiry = (_serverCertificate.NotAfter - DateTime.Now).Days;
                if (daysUntilExpiry < 30)
                {
                    Logging.Handler.Warning("RelayServer", "LoadTlsCertificate", $"Certificate expires in {daysUntilExpiry} days - renewal recommended");
                }
                
                Logging.Handler.Debug("RelayServer", "LoadTlsCertificate", 
                    $"TLS certificate loaded: {_serverCertificate.Subject}, expires: {_serverCertificate.NotAfter:yyyy-MM-dd}");
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "LoadTlsCertificate", $"Failed to load certificate: {ex}");
                _tlsEnabled = false;
            }
        }
        
        /// <summary>
        /// Admin binding to a target device (for Single-Admin-Policy)
        /// </summary>
        private class AdminDeviceBinding
        {
            public int AdminId { get; set; }
            public string ApiKey { get; set; }
            public string HardwareId { get; set; }
            public DateTime ConnectedAt { get; set; }
            public List<string> SessionIds { get; set; } = new(); // All sessions of this admin to this device
        }
        
        /// <summary>
        /// Kick information for a session (queried by Relay App via HTTP)
        /// </summary>
        public class KickInfo
        {
            public string SessionId { get; set; }
            public string Reason { get; set; }
            public DateTime KickedAt { get; set; }
            public string KickedBy { get; set; } // API-Key or "server"
        }
        
        /// <summary>
        /// Starts the main Relay listener on custom port
        /// </summary>
        public async Task<bool> StartRelayListener(bool useTls = false, string certPath = null, string certPassword = null)
        {
            try
            {
                if (_mainListener != null)
                {
                    Logging.Handler.Debug("RelayServer", "StartRelayListener", $"Listener already running on port {Server.relay_port}");
                    return true;
                }

                // Store TLS configuration
                Configuration.Server.relay_use_tls = useTls;
                Configuration.Server.relay_cert_path = certPath;
                Configuration.Server.relay_cert_password = certPassword;
                
                // Load TLS certificate if enabled
                if (useTls && !string.IsNullOrEmpty(certPath))
                {
                    LoadTlsCertificate(certPath, certPassword);
                    
                    if (!_tlsEnabled)
                    {
                        Logging.Handler.Error("RelayServer", "StartRelayListener", "Failed to load TLS certificate - cannot start with TLS");
                        return false;
                    }
                }

                _mainListener = new TcpListener(IPAddress.Any, Server.relay_port);
                _mainListener.Start();
                
                _listenerCts = new CancellationTokenSource();
                
                _ = Task.Run(async () => await HandleIncomingConnections(_listenerCts.Token), _listenerCts.Token);
                
                if (useTls && _tlsEnabled)
                {
                    Logging.Handler.Info("RelayServer", "StartRelayListener", $"Started on port {Server.relay_port} with TLS");
                }
                else
                {
                    Logging.Handler.Info("RelayServer", "StartRelayListener", $"Started on port {Server.relay_port} (plaintext - reverse proxy mode)");
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "StartRelayListener", $"Failed to start on port {Server.relay_port}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Handles all incoming connections on Port 7443
        /// </summary>
        private async Task HandleIncomingConnections(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _mainListener.AcceptTcpClientAsync();
                    var remoteEndpoint = (IPEndPoint)client.Client.RemoteEndPoint;

                    Logging.Handler.Debug("RelayServer", "HandleIncomingConnections", $"New connection from {remoteEndpoint?.Address}");

                    // Start handler for this connection (with TLS wrapping if enabled)
                    _ = Task.Run(async () => await HandleClientConnection(client, cancellationToken), 
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Logging.Handler.Error("RelayServer", "HandleIncomingConnections", $"Listener error: {ex}");
                }
            }
        }
        
        /// <summary>
        /// Sets the IP Whitelist for admin connections
        /// </summary>
        public void SetAllowedIps(List<string> allowedIps)
        {
            _relayAllowedIps = allowedIps ?? new List<string>();
            Logging.Handler.Debug("RelayServer", "SetAllowedIps", 
                $"Relay IP Whitelist updated: {(_relayAllowedIps.Count > 0 ? string.Join(", ", _relayAllowedIps) : "ALL IPs allowed")}");
        }


        /// <summary>
        /// Waits for Target connection
        /// </summary>
        private class PendingConnection
        {
            public TcpClient AdminClient { get; set; }
            public string SessionId { get; set; }
            public DateTime CreatedAt { get; set; }
            public TaskCompletionSource<TcpClient> TargetClientTcs { get; set; } = new();
        }

        /// <summary>
        /// Target Tunnel - Authenticated target ready for admin connections
        /// </summary>
        private class TargetTunnel
        {
            public string SessionId { get; set; }
            public TcpClient TargetClient { get; set; }
            public Stream TargetStream { get; set; } // Stream instead of NetworkStream for TLS support
            public DateTime CreatedAt { get; set; }
            public bool IsInUse { get; set; }
        }

        /// <summary>
        /// Active Relay Session
        /// </summary>
        public class RelaySession
        {
            public string SessionId { get; set; }
            public string TargetDeviceId { get; set; }
            public int TargetPort { get; set; }
            public string Protocol { get; set; } // "TCP" or "UDP"
            public DateTime CreatedAt { get; set; }
            public bool IsActive { get; set; }
            public string TargetConnectionId { get; set; }
            public int ActiveConnections { get; set; }
            
            // E2EE: Public Keys for agent and admin
            public string? AgentPublicKey { get; set; }  // Agent's RSA Public Key (PEM)
            public string? AdminPublicKey { get; set; }  // Admin's RSA Public Key (PEM)
            public DateTime? AgentConnectedAt { get; set; }
            public DateTime? AdminConnectedAt { get; set; }
        }

        /// <summary>
        /// Creates a new Relay Session (only with Target Device ID)
        /// All sessions use Port 7443
        /// </summary>
        public async Task<(bool success, string sessionId, int relayPort, string relayServer, string error)> CreateRelaySession(
            string targetDeviceId,
            int targetPort,
            string protocol = "TCP")
        {
            try
            {
                string sessionId = Guid.NewGuid().ToString();
                string relayServer = Configuration.Server.public_override_url;

                var session = new RelaySession
                {
                    SessionId = sessionId,
                    TargetDeviceId = targetDeviceId,
                    TargetPort = targetPort,
                    Protocol = protocol.ToUpper(),
                    CreatedAt = DateTime.UtcNow,
                    IsActive = false,
                    ActiveConnections = 0
                };

                if (!_activeSessions.TryAdd(sessionId, session))
                    return (false, null, 0, null, "Failed to create session");

                // Notify target client via SignalR
                await NotifyTargetClient(session);

                // Log Session Creation in Audit
                await LogSessionCreation(session);

                Logging.Handler.Debug("RelayServer", "CreateRelaySession", 
                    $"Created session {sessionId} on port {Server.relay_port} for {protocol}");

                return (true, sessionId, Server.relay_port, relayServer, null);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "CreateRelaySession", ex.ToString());
                return (false, null, 0, null, ex.Message);
            }
        }

        /// <summary>
        /// Checks if a session is active
        /// </summary>
        public bool IsSessionActive(string sessionId)
        {
            return _activeSessions.ContainsKey(sessionId);
        }

        /// <summary>
        /// Restores a persistent session from the database
        /// All sessions use Port 7443
        /// </summary>
        public async Task<(bool success, string sessionId, int relayPort, string relayServer, string error)> RestorePersistentSession(
            string sessionId,
            int targetDeviceId,
            int targetPort,
            string protocol,
            int? preferredPort = null)
        {
            try
            {
                // Check if session already exists
                if (_activeSessions.ContainsKey(sessionId))
                {
                    var existingSession = _activeSessions[sessionId];
                    return (true, sessionId, Server.relay_port, Configuration.Server.public_override_url, null);
                }

                string relayServer = Configuration.Server.public_override_url;

                var session = new RelaySession
                {
                    SessionId = sessionId,
                    TargetDeviceId = targetDeviceId.ToString(),
                    TargetPort = targetPort,
                    Protocol = protocol.ToUpper(),
                    CreatedAt = DateTime.UtcNow,
                    IsActive = false,
                    ActiveConnections = 0
                };

                if (!_activeSessions.TryAdd(sessionId, session))
                    return (false, null, 0, null, "Failed to create session");

                // Notify target client via SignalR
                await NotifyTargetClient(session);

                Logging.Handler.Debug("RelayServer", "RestorePersistentSession", 
                    $"Restored session {sessionId} on port {Server.relay_port} for {protocol}");

                return (true, sessionId, Server.relay_port, relayServer, null);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "RestorePersistentSession", ex.ToString());
                return (false, null, 0, null, ex.Message);
            }
        }

        /// <summary>
        /// Restores a persistent session with access_key (for Background Monitor)
        /// All sessions use Port 7443
        /// Checks if session enabled=1 before restoring
        /// </summary>
        public async Task<(bool success, string sessionId, string relayServer, string error)> RestorePersistentSessionByAccessKey(string sessionId, string accessKey, int targetPort, string protocol, int? preferredPort = null)
        {
            try
            {
                Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", $"Restoring persistent session {sessionId} with access_key {accessKey}");
                
                // IMPORTANT: Check if session exists in DB and enabled=1
                bool isEnabledInDb = false;
                try
                {
                    using var conn = new MySqlConnection(Configuration.MySQL.Connection_String);
                    await conn.OpenAsync();
                    
                    using var cmd = new MySqlCommand(
                        "SELECT enabled FROM relay_sessions WHERE session_id = @sessionId;", conn);
                    cmd.Parameters.AddWithValue("@sessionId", sessionId);
                    
                    var result = await cmd.ExecuteScalarAsync();
                    if (result != null)
                    {
                        int enabledValue = Convert.ToInt32(result);
                        isEnabledInDb = enabledValue == 1;
                        Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", $"Session {sessionId} in DB: enabled={enabledValue}");
                    }
                    else
                    {
                        // Session not in DB = In-Memory Session (allowed)
                        Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", $"Session {sessionId} not in DB - treating as in-memory session (allowed)");
                        isEnabledInDb = true;
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Error("RelayServer", "RestorePersistentSessionByAccessKey", $"Error checking enabled status: {ex}");
                    // On error: Allow In-Memory Sessions
                    isEnabledInDb = true;
                }
                
                if (!isEnabledInDb)
                {
                    Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", $"Session {sessionId} is DISABLED (enabled=0) - rejecting restore");
                    return (false, null, null, "Session is disabled");
                }
                
                // Check if session already exists
                if (_activeSessions.TryGetValue(sessionId, out var existingSession))
                {
                    Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", $"Session {sessionId} already exists on port {Server.relay_port}, IsActive={existingSession.IsActive}");
                    
                    // If session exists but is not active, update it and send command again
                    if (!existingSession.IsActive)
                    {
                        Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", "Session is inactive, updating and notifying target");
                        existingSession.TargetDeviceId = accessKey;
                        existingSession.TargetConnectionId = string.Empty;
                        
                        await NotifyTargetClient(existingSession);
                        Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", $"Re-sent notification to target for inactive session {sessionId}");
                    }
                    
                    return (true, sessionId, Configuration.Server.public_override_url, null);
                }

                // Session does not exist - create new
                Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", $"Session {sessionId} does not exist, creating new...");

                string relayServer = Configuration.Server.public_override_url;

                var session = new RelaySession
                {
                    SessionId = sessionId,
                    TargetDeviceId = accessKey,
                    TargetPort = targetPort,
                    Protocol = protocol.ToUpper(),
                    CreatedAt = DateTime.UtcNow,
                    IsActive = false,
                    ActiveConnections = 0
                };

                if (!_activeSessions.TryAdd(sessionId, session))
                {
                    Logging.Handler.Error("RelayServer", "RestorePersistentSessionByAccessKey", $"Failed to add session {sessionId} to active sessions");
                    return (false, null, null, "Failed to create session");
                }

                Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", "Session created, notifying target...");

                // Notify target client via SignalR
                await NotifyTargetClient(session);

                Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", 
                    $"Restored session {sessionId} on port {Server.relay_port} for {protocol}");

                Logging.Handler.Debug("RelayServer", "RestorePersistentSessionByAccessKey", $"Session {sessionId} successfully restored");
                return (true, sessionId, relayServer, null);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "RestorePersistentSessionByAccessKey", ex.ToString());
                return (false, null, null, ex.Message);
            }
        }

        /// <summary>
        /// Distinguishes between Admin and Target connections based on the first received data
        /// </summary>
        private async Task HandleClientConnection(TcpClient client, CancellationToken cancellationToken)
        {
            Stream stream = null;
            
            try
            {
                // TLS Handshake (if enabled)
                if (Configuration.Server.relay_use_tls && _tlsEnabled && _serverCertificate != null)
                {
                    try
                    {
                        Logging.Handler.Debug("RelayServer", "HandleClientConnection", "Starting TLS handshake...");
                        
                        var sslStream = new SslStream(
                            client.GetStream(),
                            false, // leaveInnerStreamOpen
                            null, // userCertificateValidationCallback (null = not required for server)
                            null  // userCertificateSelectionCallback
                        );
                        
                        // Perform TLS handshake as server (use cached certificate)
                        await sslStream.AuthenticateAsServerAsync(
                            _serverCertificate,
                            clientCertificateRequired: false,
                            SslProtocols.Tls12 | SslProtocols.Tls13, // Modern TLS versions
                            checkCertificateRevocation: false
                        );
                        
                        stream = sslStream;
                        
                        Logging.Handler.Debug("RelayServer", "HandleClientConnection", $"TLS handshake successful: {sslStream.SslProtocol}, Cipher: {sslStream.CipherAlgorithm}");
                    }
                    catch (Exception tlsEx)
                    {
                        Logging.Handler.Error("RelayServer", "HandleClientConnection", $"TLS handshake failed: {tlsEx.Message}");
                        client.Close();
                        return;
                    }
                }
                else
                {
                    // Plaintext mode (reverse proxy expected or TLS not configured)
                    stream = client.GetStream();
                    
                    if (Configuration.Server.relay_use_tls && !_tlsEnabled)
                    {
                        Logging.Handler.Warning("RelayServer", "HandleClientConnection", "TLS requested but not available - using plaintext");
                    }
                }
                
                // Wait for first bytes
                byte[] buffer = new byte[8192]; // Larger buffer for TLS handshake + auth
                client.ReceiveTimeout = 10000; // 10 seconds timeout
                
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                
                if (bytesRead > 0)
                {
                    string firstData = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    
                    // Check if it is a Target-Auth-Request (JSON with session_id and device_identity)
                    if (firstData.Contains("\"session_id\"") && firstData.Contains("\"device_identity\""))
                    {
                        // This is a Target authentication
                        await HandleTargetAuthentication(client, firstData, stream, cancellationToken);
                    }
                    // Check if it is an Admin-Auth-Request (JSON with session_id, api_key and hardware_id)
                    else if (firstData.Contains("\"session_id\"") && firstData.Contains("\"api_key\"") && firstData.Contains("\"hardware_id\""))
                    {
                        // This is an Admin authentication
                        await HandleAdminAuthentication(client, firstData, stream, cancellationToken);
                    }
                    else
                    {
                        Logging.Handler.Debug("RelayServer", "HandleClientConnection", "Unknown connection type - closing");
                        client.Close();
                    }
                }
                else
                {
                    client.Close();
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "HandleClientConnection", ex.ToString());
                stream?.Dispose();
                client?.Close();
            }
        }

        /// <summary>
        /// Handles Admin authentication with API-Key and Hardware-ID
        /// </summary>
        private async Task HandleAdminAuthentication(TcpClient adminClient, string authData, Stream stream, CancellationToken cancellationToken)
        {
            try
            {
                var remoteEndpoint = (IPEndPoint)adminClient.Client.RemoteEndPoint;
                string adminIp = remoteEndpoint?.Address.ToString() ?? "Unknown";
                
                Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"New admin connection from {adminIp}");
                
                // IP Whitelist Check - check FIRST before using more resources
                if (_relayAllowedIps != null && _relayAllowedIps.Count > 0)
                {
                    if (!_relayAllowedIps.Contains(adminIp))
                    {
                        Logging.Handler.Warning("RelayServer", "HandleAdminAuthentication", $"IP {adminIp} not whitelisted");
                        await SendAuthResponse(stream, false, "IP not whitelisted");
                        adminClient.Close();
                        return;
                    }
                    Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"IP {adminIp} is whitelisted");
                }
                
                // Parse Auth data
                var authDoc = JsonDocument.Parse(authData);
                
                // Validate that all required fields are present and not empty
                if (!authDoc.RootElement.TryGetProperty("session_id", out var sessionIdElement) ||
                    !authDoc.RootElement.TryGetProperty("api_key", out var apiKeyElement) ||
                    !authDoc.RootElement.TryGetProperty("hardware_id", out var hardwareIdElement))
                {
                    await SendAuthResponse(stream, false, "Missing required fields");
                    adminClient.Close();
                    return;
                }
                
                string sessionId = sessionIdElement.GetString();
                string apiKey = apiKeyElement.GetString();
                string hardwareId = hardwareIdElement.GetString();
                
                // Validate that no values are null or empty
                if (string.IsNullOrWhiteSpace(sessionId) || 
                    string.IsNullOrWhiteSpace(apiKey) || 
                    string.IsNullOrWhiteSpace(hardwareId))
                {
                    await SendAuthResponse(stream, false, "Invalid field values");
                    adminClient.Close();
                    return;
                }
                
                Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Session: {sessionId}, API Key: {apiKey}");
                
                // Verify API-Key and Hardware-ID
                string dbHardwareId = null;
                using (var conn = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String))
                {
                    await conn.OpenAsync();
                    
                    using var cmd = new MySqlConnector.MySqlCommand("SELECT hardware_id FROM relay_admins WHERE api_key = @apiKey;", conn);
                    cmd.Parameters.AddWithValue("@apiKey", apiKey);
                    
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            dbHardwareId = reader.IsDBNull(0) ? null : reader.GetString(0);
                        }
                    }
                }
                
                if (dbHardwareId == null)
                {
                    await SendAuthResponse(stream, false, "Invalid API key");
                    adminClient.Close();
                    return;
                }
                
                if (dbHardwareId != hardwareId)
                {
                    await SendAuthResponse(stream, false, "Hardware ID mismatch");
                    adminClient.Close();
                    return;
                }
                
                // Find Session
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    await SendAuthResponse(stream, false, "Session not found");
                    adminClient.Close();
                    return;
                }

                Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", "Admin auth successful, session found");
                
                // Additional security: Check if session exists in DB and enabled=1
                try
                {
                    using var enabledConn = new MySqlConnection(Configuration.MySQL.Connection_String);
                    await enabledConn.OpenAsync();

                    using var enabledCmd = new MySqlCommand(
                        "SELECT enabled FROM relay_sessions WHERE session_id = @sessionId;", enabledConn);
                    enabledCmd.Parameters.AddWithValue("@sessionId", sessionId);

                    var result = await enabledCmd.ExecuteScalarAsync();
                    if (result != null)
                    {
                        int enabledValue = Convert.ToInt32(result);
                        if (enabledValue != 1)
                        {
                            Logging.Handler.Warning("RelayServer", "HandleAdminAuthentication", $"Session {sessionId} is DISABLED (enabled={enabledValue})");
                            await SendAuthResponse(stream, false, "Session is disabled");
                            adminClient.Close();
                            return;
                        }

                        Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Session {sessionId} is enabled (enabled=1)");
                    }
                    else
                    {
                        // Session not in DB = In-Memory Session (allowed)
                        Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Session {sessionId} not in DB - treating as in-memory session (allowed)");
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Warning("RelayServer", "HandleAdminAuthentication", $"Could not check enabled status: {ex.Message}");
                }

                // Single-Admin-Policy: Check if another admin already is connected to this device
                string targetDeviceId = session.TargetDeviceId;
                
                if (_adminDeviceBindings.TryGetValue(targetDeviceId, out var existingBinding))
                {
                    // An admin is already connected to this device
                    // Check if it is the same admin (api_key + hardware_id)
                    if (existingBinding.ApiKey != apiKey || existingBinding.HardwareId != hardwareId)
                    {
                        // ANOTHER admin tries to connect → Kick the old admin COMPLETELY!
                        Logging.Handler.Info("RelayServer", "HandleAdminAuthentication", $"New admin detected for device {targetDeviceId}");
                        
                        // 1. Kick ALL sessions of the old admin for this device
                        foreach (var oldSessionId in existingBinding.SessionIds.ToList())
                        {
                            // Register kick (queried by Relay App via HTTP)
                            _kickedSessions.TryAdd(oldSessionId, new KickInfo
                            {
                                SessionId = oldSessionId,
                                Reason = "new_admin_connected",
                                KickedAt = DateTime.UtcNow,
                                KickedBy = apiKey // New admin connecting
                            });
                            Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Registered kick notification for {oldSessionId}");
                            
                            // Remove and close Target-Tunnel COMPLETELY
                            if (_targetTunnels.TryRemove(oldSessionId, out var oldTunnel))
                            {
                                try
                                {
                                    oldTunnel.TargetClient?.Close();
                                    oldTunnel.TargetClient?.Dispose();
                                    oldTunnel.TargetStream?.Close();
                                    oldTunnel.TargetStream?.Dispose();
                                }
                                catch (Exception ex)
                                {
                                    Logging.Handler.Warning("RelayServer", "HandleAdminAuthentication", $"Error closing target tunnel: {ex.Message}");
                                }
                                Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Closed target tunnel for {oldSessionId}");
                            }
                            
                            // Remove Pending Connections COMPLETELY
                            if (_pendingConnections.TryRemove(oldSessionId, out var oldPending))
                            {
                                try
                                {
                                    oldPending.AdminClient?.Close();
                                    oldPending.AdminClient?.Dispose();
                                    oldPending.TargetClientTcs?.TrySetCanceled(); // Cancel waiting admin
                                }
                                catch (Exception ex)
                                {
                                    Logging.Handler.Warning("RelayServer", "HandleAdminAuthentication", $"Error closing pending connection: {ex.Message}");
                                }
                                Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Closed pending connection for {oldSessionId}");
                            }
                            
                            // Reset session status (stays in DB!)
                            if (_activeSessions.TryGetValue(oldSessionId, out var oldSession))
                            {
                                oldSession.IsActive = false;
                                oldSession.ActiveConnections = 0;
                                oldSession.AdminPublicKey = null; // Old Admin Key invalid
                                oldSession.AdminConnectedAt = null;
                                Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Reset session state for {oldSessionId}");
                            }
                        }
                        
                        // 2. Remove old admin binding COMPLETELY
                        _adminDeviceBindings.TryRemove(targetDeviceId, out _);
                        Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", "Previous admin completely kicked");
                        
                        // 3. Create NEW binding for the new admin
                        var newBinding = new AdminDeviceBinding
                        {
                            ApiKey = apiKey,
                            HardwareId = hardwareId,
                            ConnectedAt = DateTime.UtcNow,
                            SessionIds = new List<string> { sessionId }
                        };
                        
                        _adminDeviceBindings.TryAdd(targetDeviceId, newBinding);
                        Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", "New admin binding created");
                    }
                    else
                    {
                        // SAME admin reconnecting (new session to same device)
                        Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Same admin connecting to session {sessionId}");
                        
                        if (!existingBinding.SessionIds.Contains(sessionId))
                        {
                            existingBinding.SessionIds.Add(sessionId);
                            Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Added session {sessionId} to existing binding");
                        }
                    }
                }
                else
                {
                    // First admin for this device
                    var newBinding = new AdminDeviceBinding
                    {
                        ApiKey = apiKey,
                        HardwareId = hardwareId,
                        ConnectedAt = DateTime.UtcNow,
                        SessionIds = new List<string> { sessionId }
                    };
                    
                    _adminDeviceBindings.TryAdd(targetDeviceId, newBinding);
                    Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"First admin connection to device {targetDeviceId}");
                }

                // E2EE: Extract Admin Public Key (if available)
                string? adminPublicKey = null;
                if (authDoc.RootElement.TryGetProperty("admin_public_key", out var adminPubKeyElement))
                {
                    adminPublicKey = adminPubKeyElement.GetString();
                    Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Admin Public Key received (length: {adminPublicKey?.Length ?? 0})");
                }

                // E2EE: Set/update Admin Public Key ALWAYS when available (also on reconnect!)
                if (!string.IsNullOrEmpty(adminPublicKey))
                {
                    bool isNewKey = string.IsNullOrEmpty(session.AdminPublicKey) || session.AdminPublicKey != adminPublicKey;
                    
                    session.AdminPublicKey = adminPublicKey;
                    session.AdminConnectedAt = DateTime.UtcNow;
                    
                    if (isNewKey)
                    {
                        Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Stored/Updated Admin Public Key for session {sessionId}");
                    }
                    else
                    {
                        Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Admin Public Key unchanged (reconnect with same key) for session {sessionId}");
                    }

                    // If agent already connected, CLOSE old tunnel and force agent to re-auth
                    if (_targetTunnels.TryGetValue(sessionId, out var existingTunnel) &&
                        !string.IsNullOrEmpty(session.AgentPublicKey))
                    {
                        Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", "Agent tunnel exists - forcing agent to re-authenticate to get Admin Public Key");
                        
                        try
                        {
                            // Close old tunnel - agent will automatically reconnect
                            existingTunnel.TargetClient?.Close();
                            existingTunnel.TargetStream?.Close();
                            _targetTunnels.TryRemove(sessionId, out _);
                            Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", "Closed agent tunnel so agent can reconnect and receive Admin Public Key in auth response");
                        }
                        catch (Exception ex)
                        {
                            Logging.Handler.Warning("RelayServer", "HandleAdminAuthentication", $"Error closing agent tunnel: {ex.Message}");
                        }
                    }
                    else
                    {
                        Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", "No agent tunnel yet - admin_public_key will be sent when agent connects");
                    }
                }
                
                // Send success response with Agent Public Key (from memory)
                var authResponse = new
                {
                    success = true,
                    message = "Authenticated",
                    agent_public_key = session.AgentPublicKey // Can be null if agent not connected yet
                };

                await SendAuthResponse(stream, authResponse);
                Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", $"Auth response sent (agent_public_key included: {session.AgentPublicKey != null})");
                
                // Create Audit Entry
                int auditId = await CreateAuditEntry(session, adminIp);

                // Notify agent on-demand that admin is connected
                Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", "Notifying agent to connect on-demand");
                await NotifyTargetClient(session);
                
                // Check if target tunnel already exists
                if (_targetTunnels.TryGetValue(session.SessionId, out var targetTunnel) && !targetTunnel.IsInUse)
                {
                    Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", "Target ready - starting relay");
                    targetTunnel.IsInUse = true;
                    session.IsActive = true;
                    session.ActiveConnections++;
                    
                    await RelayBidirectional(adminClient, stream, targetTunnel.TargetClient, targetTunnel.TargetStream, session, auditId, cancellationToken);
                    
                    _targetTunnels.TryRemove(session.SessionId, out _);
                }
                else
                {
                    Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", "Waiting for target to connect");
                    
                    var pending = new PendingConnection
                    {
                        AdminClient = adminClient,
                        SessionId = session.SessionId,
                        CreatedAt = DateTime.UtcNow
                    };

                    _pendingConnections.TryAdd(session.SessionId, pending);

                    var timeoutTask = Task.Delay(30000, cancellationToken);
                    var targetTask = pending.TargetClientTcs.Task;

                    var completedTask = await Task.WhenAny(targetTask, timeoutTask);

                    if (completedTask == targetTask && targetTask.IsCompletedSuccessfully)
                    {
                        var targetClient = targetTask.Result;
                        session.IsActive = true;
                        session.ActiveConnections++;

                        Logging.Handler.Debug("RelayServer", "HandleAdminAuthentication", "Target connected - starting relay");
                        
                        // Get target stream from tunnel
                        if (_targetTunnels.TryGetValue(session.SessionId, out var tunnel))
                        {
                            await RelayBidirectional(adminClient, stream, targetClient, tunnel.TargetStream, session, auditId, cancellationToken);
                            _targetTunnels.TryRemove(session.SessionId, out _);
                        }
                        else
                        {
                            Logging.Handler.Error("RelayServer", "HandleAdminAuthentication", "Target tunnel not found after pending resolved");
                            await UpdateAuditEntry(auditId, DateTime.UtcNow, "failed", "Target tunnel not found");
                            adminClient.Close();
                        }
                    }
                    else
                    {
                        Logging.Handler.Warning("RelayServer", "HandleAdminAuthentication", "Timeout waiting for target");
                        await UpdateAuditEntry(auditId, DateTime.UtcNow, "failed", "Timeout waiting for target");
                        adminClient.Close();
                    }

                    _pendingConnections.TryRemove(session.SessionId, out _);
                }
            }
            catch (JsonException jsonEx)
            {
                // JSON Parse Error
                await SendAuthResponse(stream, false, "Invalid request format");
                Logging.Handler.Warning("RelayServer", "HandleAdminAuthentication",
                    $"JSON parse error: {jsonEx.Message}");
                adminClient?.Close();
            }
            catch (Exception ex)
            {
                // General Error
                await SendAuthResponse(stream, false, "Authentication failed");
                Logging.Handler.Error("RelayServer", "HandleAdminAuthentication", ex.ToString());
                adminClient?.Close();
            }
        }

        /// <summary>
        /// Handles Target authentication
        /// </summary>
        private async Task HandleTargetAuthentication(TcpClient targetClient, string authData, Stream stream, CancellationToken cancellationToken)
        {
            try
            {
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Target auth request received");
                // Auth data contains sensitive device_identity - DO NOT LOG
                
                // Parse Auth data
                var authDoc = JsonDocument.Parse(authData);
                
                // Validate that all required fields vorhanden sind
                if (!authDoc.RootElement.TryGetProperty("session_id", out var sessionIdElement) ||
                    !authDoc.RootElement.TryGetProperty("device_identity", out var deviceIdentity))
                {
                    await SendAuthResponse(stream, false, "Missing required fields");
                    targetClient.Close();
                    return;
                }
                
                string sessionId = sessionIdElement.GetString();
                
                // Validate that no values are null or empty
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    await SendAuthResponse(stream, false, "Invalid session_id");
                    targetClient.Close();
                    return;
                }
                
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Session ID: {sessionId}");
                // Device Identity JSON contains sensitive data - DO NOT LOG in production

                // Verify Device Identity
                string deviceIdentityJson = JsonSerializer.Serialize(new { device_identity = deviceIdentity });

                // Simulate IP address (Target connects from agent)
                string ipAddress = ((IPEndPoint)targetClient.Client.RemoteEndPoint).Address.ToString();
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Target IP Address: {ipAddress}");
                
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Calling Verify_Device");
                string deviceStatus = await Agent.Windows.Authentification.Verify_Device(
                    deviceIdentityJson, ipAddress, false);
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Verify_Device returned: '{deviceStatus}'");

                if (deviceStatus != "authorized" && deviceStatus != "synced" && deviceStatus != "not_synced")
                {
                    Logging.Handler.Warning("RelayServer", "HandleTargetAuthentication", $"Device not authorized: {deviceStatus}");
                    await SendAuthResponse(stream, false, "Not authorized");
                    targetClient.Close();
                    return;
                }

                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Device status OK: '{deviceStatus}'");
                
                // Get session
                if (!_activeSessions.TryGetValue(sessionId, out var session))
                {
                    Logging.Handler.Warning("RelayServer", "HandleTargetAuthentication", $"Session {sessionId} not found in active sessions");
                    await SendAuthResponse(stream, false, "Session not found");
                    targetClient.Close();
                    return;
                }

                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Session found. Target Device ID in session: {session.TargetDeviceId}");
                
                // Additional security: Check if session exists in DB and enabled=1
                try
                {
                    using var conn = new MySqlConnection(Configuration.MySQL.Connection_String);
                    await conn.OpenAsync();
                    
                    using var cmd = new MySqlCommand(
                        "SELECT enabled FROM relay_sessions WHERE session_id = @sessionId;", conn);
                    cmd.Parameters.AddWithValue("@sessionId", sessionId);
                    
                    var result = await cmd.ExecuteScalarAsync();
                    if (result != null)
                    {
                        int enabledValue = Convert.ToInt32(result);
                        if (enabledValue != 1)
                        {
                            Logging.Handler.Warning("RelayServer", "HandleTargetAuthentication", $"Session {sessionId} is DISABLED (enabled={enabledValue})");
                            await SendAuthResponse(stream, false, "Session is disabled");
                            targetClient.Close();
                            return;
                        }
                        Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Session {sessionId} is enabled (enabled=1)");
                    }
                    else
                    {
                        // Session not in DB = In-Memory Session (allowed)
                        Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Session {sessionId} not in DB - treating as in-memory session (allowed)");
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Warning("RelayServer", "HandleTargetAuthentication", $"Could not check enabled status: {ex.Message}");
                }

                // Additionally: Check if Device ID matches
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Checking Device ID match");
                
                string accessKey = deviceIdentity.TryGetProperty("access_key", out var accessKeyElement)
                    ? accessKeyElement.GetString() ?? ""
                    : "";
                string deviceName = deviceIdentity.TryGetProperty("device_name", out var deviceNameElement)
                    ? deviceNameElement.GetString() ?? ""
                    : "";
                string hwid = deviceIdentity.TryGetProperty("hwid", out var hwidElement) 
                    ? hwidElement.GetString() ?? "" 
                    : "";
                
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"access_key from device: '{accessKey}'");
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"device_name from device: '{deviceName}'");
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"hwid from device: '{hwid}'");
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Expected target_device_id: '{session.TargetDeviceId}'");
                    
                // Match against target_device_id (can be access_key, hwid or device_name)
                bool deviceIdMatch = accessKey == session.TargetDeviceId ||
                                     hwid == session.TargetDeviceId ||
                                     deviceName == session.TargetDeviceId;
                
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Match result: {deviceIdMatch}");
                
                if (!deviceIdMatch)
                {
                    Logging.Handler.Warning("RelayServer", "HandleTargetAuthentication", $"Device ID mismatch. Expected: {session.TargetDeviceId}, Got: access_key={accessKey}, hwid={hwid}, device_name={deviceName}");
                    await SendAuthResponse(stream, false, "Device ID mismatch");
                    targetClient.Close();
                    return;
                }

                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Target authenticated for session {sessionId}");

                // E2EE: Extract Agent Public Key (if available)
                string? agentPublicKey = null;
                if (authDoc.RootElement.TryGetProperty("agent_public_key", out var agentPubKeyElement))
                {
                    agentPublicKey = agentPubKeyElement.GetString();
                    Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Agent Public Key received (length: {agentPublicKey?.Length ?? 0})");
                }

                // E2EE: ALWAYS update when agent reconnects (important after agent restart!)
                if (!string.IsNullOrEmpty(agentPublicKey))
                {
                    // Check if public key has changed
                    bool keyChanged = !string.IsNullOrEmpty(session.AgentPublicKey) && session.AgentPublicKey != agentPublicKey;
                    
                    if (keyChanged)
                    {
                        Logging.Handler.Info("RelayServer", "HandleTargetAuthentication", "Agent Public Key changed - agent restart detected");
                        Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Old key (truncated): {session.AgentPublicKey?.Substring(0, Math.Min(50, session.AgentPublicKey.Length))}");
                        Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"New key (truncated): {agentPublicKey.Substring(0, Math.Min(50, agentPublicKey.Length))}");
                         
                        // IMPORTANT: Close all existing target tunnels and pending connections!
                        // This forces the Relay App, to establish a completely new connection and generate a NEW Session-Key!
                        Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Cleaning up old tunnels/connections due to agent key change");
                        
                        // 1. Close target tunnel
                        if (_targetTunnels.TryRemove(sessionId, out var oldTunnel))
                        {
                            try
                            {
                                oldTunnel.TargetClient?.Close();
                                oldTunnel.TargetStream?.Close();
                                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Closed old target tunnel");
                            }
                            catch (Exception cleanupEx)
                            {
                                Logging.Handler.Warning("RelayServer", "HandleTargetAuthentication", $"Error closing old tunnel: {cleanupEx.Message}");
                            }
                        }
                        
                        // 2. Close pending connections (if admin is waiting)
                        if (_pendingConnections.TryRemove(sessionId, out var oldPending))
                        {
                            try
                            {
                                oldPending.AdminClient?.Close();
                                oldPending.TargetClientTcs?.TrySetCanceled();
                                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Closed old pending connection");
                            }
                            catch (Exception cleanupEx)
                            {
                                Logging.Handler.Warning("RelayServer", "HandleTargetAuthentication", $"Error closing pending connection: {cleanupEx.Message}");
                            }
                        }
                        
                        Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Cleanup complete - relay app will establish new connection with correct key");
                    }
                    else if (string.IsNullOrEmpty(session.AgentPublicKey))
                    {
                        Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"First-time storing Agent Public Key for session {sessionId}");
                    }
                    else
                    {
                        Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Refreshing Agent Public Key for session {sessionId} (same key)");
                    }
                    
                    // ALWAYS update
                    session.AgentPublicKey = agentPublicKey;
                    session.AgentConnectedAt = DateTime.UtcNow;
                    Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", $"Agent Public Key updated (truncated): {agentPublicKey.Substring(0, Math.Min(100, agentPublicKey.Length))}");
                }
                else
                {
                    Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "No Agent Public Key received - E2EE not available");
                }

                // Send success response WITHOUT Admin Public Key
                // IMPORTANT: Admin Public Key is ONLY sent via SignalR Command (NotifyTargetClient)!
                
                var authResponse = new
                {
                    success = true,
                    message = "Authenticated"
                    // NO admin_public_key here - sent via SignalR!
                };

                await SendAuthResponse(stream, authResponse);
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Auth response sent to target: SUCCESS (NO admin_public_key - will be sent via SignalR)");

                // Register target tunnel for session
                var targetTunnel = new TargetTunnel
                {
                    SessionId = sessionId,
                    TargetClient = targetClient,
                    TargetStream = stream,
                    CreatedAt = DateTime.UtcNow,
                    IsInUse = false
                };
                
                _targetTunnels.TryAdd(sessionId, targetTunnel);
                Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Target tunnel registered and ready for admin connections");
                
                // NOTE: IsActive will be set to true only when actual relay starts (admin connects)
                // session.IsActive remains false until bidirectional relay begins
                session.TargetConnectionId = session.TargetConnectionId; // Keep connection ID
                
                // Check if admin is already waiting
                if (_pendingConnections.TryGetValue(sessionId, out var pending))
                {
                    Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Found pending admin connection - assigning target tunnel");
                    
                    targetTunnel.IsInUse = true;
                    pending.TargetClientTcs.SetResult(targetClient);
                    Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "Target tunnel assigned to waiting admin");
                }
                else
                {
                    Logging.Handler.Debug("RelayServer", "HandleTargetAuthentication", "No pending admin yet - tunnel ready and waiting");
                }

            }
            catch (JsonException jsonEx)
            {
                // JSON parsing error
                await SendAuthResponse(stream, false, "Invalid request format");
                Logging.Handler.Warning("RelayServer", "HandleTargetAuthentication", $"JSON parse error: {jsonEx.Message}");
                targetClient?.Close();
            }
            catch (Exception ex)
            {
                // General Error
                await SendAuthResponse(stream, false, "Authentication failed");
                Logging.Handler.Error("RelayServer", "HandleTargetAuthentication", ex.ToString());
                targetClient?.Close();
            }
        }

        /// <summary>
        /// Sends auth response to target (simple)
        /// </summary>
        private async Task SendAuthResponse(Stream stream, bool success, string message)
        {
            var response = new
            {
                success = success,
                message = message
            };

            string responseJson = JsonSerializer.Serialize(response) + "\n";
            byte[] responseBytes = System.Text.Encoding.UTF8.GetBytes(responseJson);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
            await stream.FlushAsync();
        }

        /// <summary>
        /// Sends auth response to target (with Public Key for E2EE)
        /// </summary>
        private async Task SendAuthResponse(Stream stream, object response)
        {
            string responseJson = JsonSerializer.Serialize(response) + "\n";
            byte[] responseBytes = System.Text.Encoding.UTF8.GetBytes(responseJson);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
            await stream.FlushAsync();
        }

        /// <summary>
        /// Bidirectional relay between Admin and Target
        /// </summary>
        private async Task RelayBidirectional(TcpClient adminClient, Stream adminStream, 
            TcpClient targetClient, Stream targetStream, 
            RelaySession session, int auditId, CancellationToken cancellationToken)
        {
            DateTime connectionStart = DateTime.UtcNow;
            try
            {

                var adminToTarget = RelayStreamAsync(adminStream, targetStream, 
                    $"Session {session.SessionId} Admin->Target", cancellationToken);
                var targetToAdmin = RelayStreamAsync(targetStream, adminStream, 
                    $"Session {session.SessionId} Target->Admin", cancellationToken);

                await Task.WhenAny(adminToTarget, targetToAdmin);

                Logging.Handler.Debug("RelayServer", "RelayBidirectional", 
                    $"Relay ended for session {session.SessionId}");
                
                // Update audit entry: connection normally closed
                await UpdateAuditEntry(auditId, connectionStart, "closed", "Normal disconnect");
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "RelayBidirectional", ex.ToString());
                
                // Update audit entry: connection closed with error
                await UpdateAuditEntry(auditId, connectionStart, "failed", $"Error: {ex.Message}");
            }
            finally
            {
                Logging.Handler.Debug("RelayServer", "RelayBidirectional", $"Cleaning up relay for session {session.SessionId}");

                // IMPORTANT: Close clients at the end!
                try
                {
                    adminClient?.Close();
                }
                catch (Exception ex)
                {
                    Logging.Handler.Warning("RelayServer", "RelayBidirectional", $"Error closing admin client: {ex.Message}");
                }

                try
                {
                    targetClient?.Close();
                }
                catch (Exception ex)
                {
                    Logging.Handler.Warning("RelayServer", "RelayBidirectional", $"Error closing target client: {ex.Message}");
                }

                // Reduce connections
                session.ActiveConnections = Math.Max(0, session.ActiveConnections - 1);

                if (session.ActiveConnections <= 0)
                {
                    session.IsActive = false;
                    session.AdminPublicKey = null; // Delete Admin Public Key
                    session.AdminConnectedAt = null;
                    Logging.Handler.Debug("RelayServer", "RelayBidirectional", $"Session marked inactive and AdminPublicKey cleared for {session.SessionId}");
                }

                // Clean up admin binding
                if (_adminDeviceBindings.TryGetValue(session.TargetDeviceId, out var binding))
                {
                    binding.SessionIds.Remove(session.SessionId);
                    Logging.Handler.Debug("RelayServer", "RelayBidirectional", "Removed session from binding");

                    if (binding.SessionIds.Count == 0)
                    {
                        _adminDeviceBindings.TryRemove(session.TargetDeviceId, out _);
                        Logging.Handler.Debug("RelayServer", "RelayBidirectional", $"Removed admin binding (no sessions left) for device {session.TargetDeviceId}");
                    }
                }

                // Remove target tunnel (Agent can reconnect)
                if (_targetTunnels.TryRemove(session.SessionId, out var tunnel))
                {
                    try
                    {
                        tunnel.TargetClient?.Close();
                        tunnel.TargetStream?.Close();
                        Logging.Handler.Debug("RelayServer", "RelayBidirectional", $"Removed target tunnel for session {session.SessionId}");
                    }
                    catch (Exception ex)
                    {
                        Logging.Handler.Warning("RelayServer", "RelayBidirectional", $"Warning while removing tunnel: {ex.Message}");
                    }
                }

                // Clean up pending connections
                _pendingConnections.TryRemove(session.SessionId, out _);

                Logging.Handler.Debug("RelayServer", "RelayBidirectional", "Cleanup complete - ready for reconnect");
            }
        }

        /// <summary>
        /// Forwards data between two streams
        /// </summary>
        private async Task RelayStreamAsync(Stream source, Stream destination, 
            string direction, CancellationToken cancellationToken)
        {
            try
            {
                byte[] buffer = new byte[8192];
                int bytesRead;

                while (!cancellationToken.IsCancellationRequested && 
                       (bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Logging.Handler.Debug("RelayServer", "RelayStreamAsync", 
                        $"{direction}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Starts UDP Listener for a port
        /// </summary>
        /*private async Task<bool> StartUdpListener(int port, string protocol)
        {
            try
            {
                if (_listeners.ContainsKey(port))
                    return true;

                var udpClient = new UdpClient(port);
                var cts = new CancellationTokenSource();

                var relayListener = new RelayListener
                {
                    Port = port,
                    UdpClient = udpClient,
                    Protocol = protocol,
                    CancellationTokenSource = cts
                };

                _listeners.TryAdd(port, relayListener);

                _ = Task.Run(async () => await HandleUdpPackets(relayListener, cts.Token), cts.Token);

                Logging.Handler.Debug("RelayServer", "StartUdpListener",
                    $"UDP Listener started on port {port}");

                return true;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "StartUdpListener", ex.ToString());
                return false;
            }
        }*/

        /// <summary>
        /// Handles UDP packets (simplified - without auth)
        /// </summary>
        /*private async Task HandleUdpPackets(RelayListener listener, CancellationToken cancellationToken)
        {
            var sessions = new ConcurrentDictionary<IPEndPoint, string>();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var result = await listener.UdpClient.ReceiveAsync();

                    // For UDP: Simple relay without authentication
                    // TODO: Implement UDP auth if needed

                    Logging.Handler.Debug("RelayServer", "HandleUdpPackets",
                        $"UDP packet from {result.RemoteEndPoint}");
                }
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    Logging.Handler.Error("RelayServer", "HandleUdpPackets", ex.ToString());
            }
        }*/

        /// <summary>
        /// Notifies target client via SignalR about new session
        /// </summary>
        private async Task NotifyTargetClient(RelaySession session)
        {
            try
            {
                Logging.Handler.Debug("RelayServer", "NotifyTargetClient", $"NotifyTargetClient called for session {session.SessionId}");
                Logging.Handler.Debug("RelayServer", "NotifyTargetClient", $"Looking for connection with TargetDeviceId: {session.TargetDeviceId}");
                
                // Get Connection ID for Target Device
                var targetConnectionId = GetConnectionIdByDeviceId(session.TargetDeviceId);
                
                if (string.IsNullOrEmpty(targetConnectionId))
                {
                    Logging.Handler.Warning("RelayServer", "NotifyTargetClient", $"Could not find connection ID for device {session.TargetDeviceId}");
                 
                    return;
                }

                Logging.Handler.Debug("RelayServer", "NotifyTargetClient", $"Found target connection ID: {targetConnectionId}");
                session.TargetConnectionId = targetConnectionId;

                var hubContext = CommandHubSingleton.Instance.HubContext;
                
                // Build relay details as JSON for command field (including Admin Public Key)
                Logging.Handler.Debug("RelayServer", "NotifyTargetClient", "Preparing SignalR command for target");
                Logging.Handler.Debug("RelayServer", "NotifyTargetClient", $"Admin Public Key available: {!string.IsNullOrEmpty(session.AdminPublicKey)}");
                if (!string.IsNullOrEmpty(session.AdminPublicKey))
                {
                    Logging.Handler.Debug("RelayServer", "NotifyTargetClient", $"Admin Public Key length: {session.AdminPublicKey.Length}");
                    Logging.Handler.Debug("RelayServer", "NotifyTargetClient", $"Admin Public Key (truncated): {session.AdminPublicKey.Substring(0, Math.Min(100, session.AdminPublicKey.Length))}");
                }

                var relayDetails = new
                {
                    session_id = session.SessionId,
                    //relay_server = Configuration.Server.public_override_url,
                    local_port = session.TargetPort,
                    protocol = session.Protocol,
                    public_key = Configuration.Server.relay_public_key_pem,
                    fingerprint = Configuration.Server.relay_public_key_fingerprint,
                    admin_public_key = session.AdminPublicKey // Admin Public Key is ONLY sent here!
                };

                Logging.Handler.Debug("RelayServer", "NotifyTargetClient", $"Sending relay details with server public key (fingerprint: {Configuration.Server.relay_public_key_fingerprint})");
                Logging.Handler.Debug("RelayServer", "NotifyTargetClient", $"Admin Public Key included in command: {!string.IsNullOrEmpty(session.AdminPublicKey)}");
                
                // Use Command class with type=14 for Relay
                var targetCommand = new CommandHub.Command
                {
                    type = 14, // Relay Connection Request
                    wait_response = false,
                    command = JsonSerializer.Serialize(relayDetails)
                };

                Logging.Handler.Debug("RelayServer", "NotifyTargetClient", $"Sending command to client {targetConnectionId}");

                await hubContext.Clients.Client(targetConnectionId)
                    .SendCoreAsync("SendMessageToClient", new object[] { JsonSerializer.Serialize(targetCommand) });

                Logging.Handler.Info("RelayServer", "NotifyTargetClient", $"Successfully sent SignalR Command to agent for session {session.SessionId}");
                Logging.Handler.Debug("RelayServer", "NotifyTargetClient", $"Admin Public Key included in command: {!string.IsNullOrEmpty(session.AdminPublicKey)}");
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "NotifyTargetClient", ex.ToString());
            }
        }

        /// <summary>
        /// Closes a Relay session
        /// </summary>
        public async Task<bool> CloseSession(string sessionId)
        {
            try
            {
                if (!_activeSessions.TryRemove(sessionId, out var session))
                {
                    Logging.Handler.Warning("RelayServer", "CloseSession", 
                        $"Session {sessionId} not found");
                    return false;
                }

                session.IsActive = false;

                // Cleanup target tunnel if available
                if (_targetTunnels.TryRemove(sessionId, out var targetTunnel))
                {
                    targetTunnel.TargetClient?.Close();
                    Logging.Handler.Debug("RelayServer", "CloseSession", 
                        $"Closed target tunnel for session {sessionId}");
                }
                
                // Cleanup pending connections if available
                if (_pendingConnections.TryRemove(sessionId, out var pending))
                {
                    pending.AdminClient?.Close();
                    Logging.Handler.Debug("RelayServer", "CloseSession", 
                        $"Closed pending admin connection for session {sessionId}");
                }

                // Single-Admin-Policy: Remove session from AdminDeviceBinding
                if (_adminDeviceBindings.TryGetValue(session.TargetDeviceId, out var binding))
                {
                    binding.SessionIds.Remove(sessionId);
                    Logging.Handler.Debug("RelayServer", "CloseSession", $"Removed session {sessionId} from admin binding (CloseSession)");
                    
                    // If no more sessions exist for this admin + device, remove complete binding
                    if (binding.SessionIds.Count == 0)
                    {
                        _adminDeviceBindings.TryRemove(session.TargetDeviceId, out _);
                        Logging.Handler.Debug("RelayServer", "CloseSession", $"Removed admin binding for device {session.TargetDeviceId} (CloseSession, no sessions left)");
                    }
                }

                // Notify target client
                await NotifySessionClosed(session);


                Logging.Handler.Debug("RelayServer", "CloseSession", 
                    $"Closed session {sessionId}");

                return true;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "CloseSession", ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Cleans up admin connection properly (without deleting session from DB!)
        /// </summary>
        public async Task<bool> CleanupAdminConnection(string sessionId, string targetDeviceId, string apiKey, string hardwareId)
        {
            try
            {
                Logging.Handler.Debug("RelayServer", "CleanupAdminConnection", $"Starting cleanup for session {sessionId}");

                // Remove session from admin-device-binding
                if (_adminDeviceBindings.TryGetValue(targetDeviceId, out var binding))
                {
                    binding.SessionIds.Remove(sessionId);
                    Logging.Handler.Debug("RelayServer", "CleanupAdminConnection", "Removed session from admin binding");

                    if (binding.SessionIds.Count == 0)
                    {
                        _adminDeviceBindings.TryRemove(targetDeviceId, out _);
                        Logging.Handler.Debug("RelayServer", "CleanupAdminConnection", "Removed admin binding (no sessions left)");
                    }
                }

                // Remove target tunnel and close connection
                if (_targetTunnels.TryRemove(sessionId, out var tunnel))
                {
                    try
                    {
                        tunnel.TargetClient?.Close();
                        tunnel.TargetStream?.Close();
                        tunnel.IsInUse = false;
                        Logging.Handler.Debug("RelayServer", "CleanupAdminConnection", "Closed target tunnel");
                    }
                    catch (Exception ex)
                    {
                        Logging.Handler.Warning("RelayServer", "CleanupAdminConnection", $"Error closing tunnel: {ex.Message}");
                    }
                }

                // Remove pending connections
                if (_pendingConnections.TryRemove(sessionId, out var pending))
                {
                    try
                    {
                        pending.AdminClient?.Close();
                        pending.TargetClientTcs.TrySetCanceled();
                        Logging.Handler.Debug("RelayServer", "CleanupAdminConnection", "Removed pending connection");
                    }
                    catch (Exception ex)
                    {
                        Logging.Handler.Warning("RelayServer", "CleanupAdminConnection", $"Error closing pending: {ex.Message}");
                    }
                }

                // Reset session status (NOT delete!)
                if (_activeSessions.TryGetValue(sessionId, out var session))
                {
                    session.IsActive = false;
                    session.ActiveConnections = 0;
                    session.AdminPublicKey = null; // IMPORTANT: Delete Admin Public Key!
                    session.AdminConnectedAt = null;
                    Logging.Handler.Debug("RelayServer", "CleanupAdminConnection", "Reset session status (kept in memory)");
                }

                Logging.Handler.Debug("RelayServer", "CleanupAdminConnection", $"Cleanup complete for session {sessionId}");
                return true;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "CleanupAdminConnection", ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Cleans up only the target tunnel for a session (without closing the session)
        /// Called when target device disconnects
        /// </summary>
        public void CleanupTargetTunnelForSession(string sessionId)
        {
            try
            {
                // Remove target tunnel if available
                if (_targetTunnels.TryRemove(sessionId, out var targetTunnel))
                {
                    try
                    {
                        targetTunnel.TargetClient?.Close();
                        targetTunnel.TargetStream?.Close();
                    }
                    catch (Exception ex)
                    {
                        Logging.Handler.Warning("RelayServer", "CleanupTargetTunnelForSession", $"Error closing target tunnel: {ex.Message}");
                    }
                    
                    Logging.Handler.Debug("RelayServer", "CleanupTargetTunnelForSession", $"Removed target tunnel for session {sessionId}");
                }
                
                // Update session status: Target is disconnected
                if (_activeSessions.TryGetValue(sessionId, out var session))
                {
                    session.TargetConnectionId = string.Empty; // Target must reconnect
                    session.IsActive = false; // Mark as inactive
                    Logging.Handler.Debug("RelayServer", "CleanupTargetTunnelForSession", $"Session {sessionId} marked as inactive (waiting for target reconnect)");
                }
                
                // Session stays in memory and in DB - Target just needs to reconnect
                // The background monitor or CheckAndNotifyPendingRelaySessions
                // will send the command again when the device reconnects
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "CleanupTargetTunnelForSession", ex.ToString());
            }
        }

        /// <summary>
        /// Notifies target about closed session
        /// </summary>
        private async Task NotifySessionClosed(RelaySession session)
        {
            try
            {
                if (string.IsNullOrEmpty(session.TargetConnectionId))
                    return;

                // Build close details as JSON
                var closeDetails = new
                {
                    session_id = session.SessionId
                };
                
                // Use Command class with type=16 for Relay Close
                var closeCommand = new CommandHub.Command
                {
                    type = 16, // Relay Close Connection
                    wait_response = false,
                    command = JsonSerializer.Serialize(closeDetails)
                };

                string commandJson = JsonSerializer.Serialize(closeCommand);
                var hubContext = CommandHubSingleton.Instance.HubContext;

                await hubContext.Clients.Client(session.TargetConnectionId)
                    .SendCoreAsync("SendMessageToClient", new object[] { commandJson });
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "NotifySessionClosed", ex.ToString());
            }
        }

        /// <summary>
        /// Gets connection ID for a device from CommandHubSingleton
        /// Prioritizes access_key as primary identifier
        /// </summary>
        private string GetConnectionIdByDeviceId(string deviceId)
        {
            var connections = CommandHubSingleton.Instance._clientConnections;
            
            foreach (var kvp in connections)
            {
                try
                {
                    var identityJson = kvp.Value;
                    using var document = JsonDocument.Parse(identityJson);
                    
                    if (document.RootElement.TryGetProperty("device_identity", out var deviceIdentity))
                    {
                        // Priority 1: access_key (primary authentication key)
                        if (deviceIdentity.TryGetProperty("access_key", out var accessKey) && 
                            accessKey.GetString() == deviceId)
                        {
                            return kvp.Key;
                        }
                        
                        // Priority 2: hwid (hardware ID)
                        if (deviceIdentity.TryGetProperty("hwid", out var hwid) && 
                            hwid.GetString() == deviceId)
                        {
                            return kvp.Key;
                        }
                        
                        // Priority 3: device_name (fallback)
                        if (deviceIdentity.TryGetProperty("device_name", out var deviceName) && 
                            deviceName.GetString() == deviceId)
                        {
                            return kvp.Key;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Debug("RelayServer", "GetConnectionIdByDeviceId", 
                        $"Failed to parse identity for connection {kvp.Key}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Logs the creation of a session (without admin connection)
        /// </summary>
        private async Task LogSessionCreation(RelaySession session)
        {
            try
            {
                Logging.Handler.Debug("RelayServer", "LogSessionCreation", $"Logging session creation {session.SessionId}");
                
                // Get target_device_id
                int? targetDeviceId = null;
                
                // Try from TargetDeviceId (access_key) to determine
                if (!string.IsNullOrEmpty(session.TargetDeviceId))
                {
                    try
                    {
                        Logging.Handler.Debug("RelayServer", "LogSessionCreation", $"Resolving device_id from access_key: {session.TargetDeviceId}");
                        
                        using var conn = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String);
                        await conn.OpenAsync();
                        
                        using var cmd = new MySqlConnector.MySqlCommand(
                            "SELECT id FROM devices WHERE access_key = @accessKey;", conn);
                        cmd.Parameters.AddWithValue("@accessKey", session.TargetDeviceId);
                        
                        var result = await cmd.ExecuteScalarAsync();
                        if (result != null)
                        {
                            targetDeviceId = Convert.ToInt32(result);
                            Logging.Handler.Debug("RelayServer", "LogSessionCreation", $"Resolved to device_id: {targetDeviceId}");
                        }
                        else
                        {
                            Logging.Handler.Debug("RelayServer", "LogSessionCreation", "Could not resolve device_id (access_key not found in DB)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logging.Handler.Warning("RelayServer", "LogSessionCreation", $"Error resolving device_id: {ex.Message}");
                    }
                }
                else
                {
                    Logging.Handler.Debug("RelayServer", "LogSessionCreation", "No TargetDeviceId provided, device_id will be NULL");
                }
                
                // INSERT audit entry with status='created'
                using var insertConn = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String);
                await insertConn.OpenAsync();
                
                using var insertCmd = new MySqlConnector.MySqlCommand(@"
                    INSERT INTO relay_sessions_audit 
                    (session_id, admin_ip, target_device_id, target_port, protocol, connection_started, status, disconnect_reason)
                    VALUES 
                    (@sessionId, NULL, @targetDeviceId, @targetPort, @protocol, NOW(), 'created', 'Session created, waiting for admin connection');", insertConn);
                
                insertCmd.Parameters.AddWithValue("@sessionId", session.SessionId);
                insertCmd.Parameters.AddWithValue("@targetDeviceId", targetDeviceId.HasValue ? (object)targetDeviceId.Value : DBNull.Value);
                insertCmd.Parameters.AddWithValue("@targetPort", session.TargetPort);
                insertCmd.Parameters.AddWithValue("@protocol", session.Protocol);
                
                Logging.Handler.Debug("RelayServer", "LogSessionCreation", "Executing INSERT into relay_sessions_audit");
                await insertCmd.ExecuteNonQueryAsync();
                
                Logging.Handler.Info("RelayServer", "LogSessionCreation", $"Session creation logged for {session.SessionId}");
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "LogSessionCreation", ex.ToString());
            }
        }

        /// <summary>
        /// Creates an audit entry for a new admin connection
        /// </summary>
        private async Task<int> CreateAuditEntry(RelaySession session, string adminIp)
        {
            try
            {
                // Get target_device_id from DB (if session is persistent)
                int? targetDeviceId = null;
                
                try
                {
                    // Attempt 1: Get from DB (for persistent sessions)
                    using var conn1 = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String);
                    await conn1.OpenAsync();
                    
                    using var cmd1 = new MySqlConnector.MySqlCommand(
                        "SELECT target_device_id FROM relay_sessions WHERE session_id = @sessionId;", conn1);
                    cmd1.Parameters.AddWithValue("@sessionId", session.SessionId);
                    
                    var deviceIdResult = await cmd1.ExecuteScalarAsync();
                    if (deviceIdResult != null && deviceIdResult != DBNull.Value)
                    {
                        targetDeviceId = Convert.ToInt32(deviceIdResult);
                        Logging.Handler.Debug("RelayServer", "CreateAuditEntry", $"Found target_device_id from DB: {targetDeviceId}");
                    }
                }
                catch
                {
                    // Session not in DB (temporary session)
                }
                
                // If not found in DB, try from TargetDeviceId (access_key) to determine
                if (!targetDeviceId.HasValue && !string.IsNullOrEmpty(session.TargetDeviceId))
                {
                    try
                    {
                        using var conn2 = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String);
                        await conn2.OpenAsync();
                        
                        using var cmd2 = new MySqlConnector.MySqlCommand(
                            "SELECT id FROM devices WHERE access_key = @accessKey;", conn2);
                        cmd2.Parameters.AddWithValue("@accessKey", session.TargetDeviceId);
                        
                        var result = await cmd2.ExecuteScalarAsync();
                        if (result != null)
                        {
                            targetDeviceId = Convert.ToInt32(result);
                            Logging.Handler.Debug("RelayServer", "CreateAuditEntry", $"Resolved device_id from access_key: {targetDeviceId}");
                        }
                        else
                        {
                            Logging.Handler.Debug("RelayServer", "CreateAuditEntry", $"Could not resolve device_id from access_key: {session.TargetDeviceId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logging.Handler.Warning("RelayServer", "CreateAuditEntry", $"Error resolving device_id from access_key: {ex.Message}");
                    }
                }
                
                // Check if 'created' audit entry already exists
                int? existingAuditId = null;
                try
                {
                    using var conn3 = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String);
                    await conn3.OpenAsync();
                    
                    using var cmd3 = new MySqlConnector.MySqlCommand(
                        "SELECT id FROM relay_sessions_audit WHERE session_id = @sessionId AND status = 'created' ORDER BY id DESC LIMIT 1;", conn3);
                    cmd3.Parameters.AddWithValue("@sessionId", session.SessionId);
                    
                    var result = await cmd3.ExecuteScalarAsync();
                    if (result != null)
                        existingAuditId = Convert.ToInt32(result);
                }
                catch
                {
                    // No existing entry
                }
                
                int auditId;
                
                using var updateConn = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String);
                await updateConn.OpenAsync();
                
                if (existingAuditId.HasValue)
                {
                    // Update existing 'created' entry to 'active'
                    auditId = existingAuditId.Value;
                    
                    using var updateCmd = new MySqlConnector.MySqlCommand(@"
                        UPDATE relay_sessions_audit 
                        SET 
                            status = 'active',
                            admin_ip = @adminIp,
                            connection_started = NOW(),
                            disconnect_reason = NULL
                        WHERE id = @auditId;", updateConn);
                    
                    updateCmd.Parameters.AddWithValue("@adminIp", adminIp);
                    updateCmd.Parameters.AddWithValue("@auditId", auditId);
                    
                    await updateCmd.ExecuteNonQueryAsync();
                    
                    Logging.Handler.Debug("RelayServer", "CreateAuditEntry", $"Updated existing audit entry {auditId} from 'created' to 'active', Admin IP: {adminIp}");
                }
                else
                {
                    // INSERT new audit entry (if session was created without LogSessionCreation)
                    using var insertCmd = new MySqlConnector.MySqlCommand(@"
                        INSERT INTO relay_sessions_audit 
                        (session_id, admin_ip, target_device_id, target_port, protocol, connection_started, status)
                        VALUES 
                        (@sessionId, @adminIp, @targetDeviceId, @targetPort, @protocol, NOW(), 'active');
                        SELECT LAST_INSERT_ID();", updateConn);
                    
                    insertCmd.Parameters.AddWithValue("@sessionId", session.SessionId);
                    insertCmd.Parameters.AddWithValue("@adminIp", adminIp);
                    insertCmd.Parameters.AddWithValue("@targetDeviceId", targetDeviceId.HasValue ? (object)targetDeviceId.Value : DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@targetPort", session.TargetPort);
                    insertCmd.Parameters.AddWithValue("@protocol", session.Protocol);
                    
                    var result = await insertCmd.ExecuteScalarAsync();
                    auditId = result != null ? Convert.ToInt32(result) : 0;
                    
                    Logging.Handler.Debug("RelayServer", "CreateAuditEntry", $"Created new audit entry {auditId} for session {session.SessionId}, Admin IP: {adminIp}");
                }
                
                Logging.Handler.Debug("RelayServer", "CreateAuditEntry", $"Audit entry {auditId} for session {session.SessionId}");
                
                return auditId;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "CreateAuditEntry", ex.ToString());
                return 0; // Fallback on error
            }
        }

        /// <summary>
        /// Updates an audit entry when the connection ends
        /// </summary>
        private async Task UpdateAuditEntry(int auditId, DateTime connectionStart, string status, string disconnectReason)
        {
            try
            {
                if (auditId == 0)
                {
                    Logging.Handler.Debug("RelayServer", "UpdateAuditEntry", "Skipping update - invalid audit ID");
                    return;
                }
                
                DateTime connectionEnd = DateTime.UtcNow;
                int durationSeconds = (int)(connectionEnd - connectionStart).TotalSeconds;
                
                using var connection = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String);
                await connection.OpenAsync();
                
                using var cmd = new MySqlConnector.MySqlCommand(@"
                    UPDATE relay_sessions_audit 
                    SET 
                        connection_ended = NOW(),
                        duration_seconds = @durationSeconds,
                        status = @status,
                        disconnect_reason = @disconnectReason
                    WHERE id = @auditId;", connection);
                
                cmd.Parameters.AddWithValue("@durationSeconds", durationSeconds);
                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@disconnectReason", disconnectReason ?? "");
                cmd.Parameters.AddWithValue("@auditId", auditId);
                
                await cmd.ExecuteNonQueryAsync();
                
                Logging.Handler.Debug("RelayServer", "UpdateAuditEntry", $"Updated audit entry {auditId}: status={status}, duration={durationSeconds}s");
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayServer", "UpdateAuditEntry", ex.ToString());
            }
        }

        /// <summary>
        /// Returns all active sessions
        /// </summary>
        public List<RelaySession> GetActiveSessions()
        {
            return _activeSessions.Values.ToList();
        }

        /// <summary>
        /// Returns a specific session
        /// </summary>
        public RelaySession GetSession(string sessionId)
        {
            _activeSessions.TryGetValue(sessionId, out var session);
            return session;
        }
        
        /// <summary>
        /// Checks if a session was kicked (for Relay App status polling)
        /// Returns kick info if kicked, otherwise null
        /// </summary>
        public KickInfo GetKickStatus(string sessionId)
        {
            _kickedSessions.TryGetValue(sessionId, out var kickInfo);
            return kickInfo;
        }
        
        /// <summary>
        /// Removes a kick notification (after Relay App has retrieved it)
        /// </summary>
        public void ClearKickStatus(string sessionId)
        {
            _kickedSessions.TryRemove(sessionId, out _);
            Logging.Handler.Debug("RelayServer", "ClearKickStatus", $"Cleared kick status for session {sessionId}");
        }
    }
}


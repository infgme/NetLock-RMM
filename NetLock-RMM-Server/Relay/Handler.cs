using Microsoft.AspNetCore.Http;
using System.Text.Json;
using MySqlConnector;
using NetLock_RMM_Server.Configuration;
using NetLock_RMM_Server.MySQL;

namespace NetLock_RMM_Server.Relay
{
    /// <summary>
    /// Handler for Relay endpoints
    /// </summary>
    public static class Handler
    {
        /// <summary>
        /// Helper class for admin information
        /// </summary>
        private class AdminInfo
        {
            public int Id { get; set; }
            public string ApiKey { get; set; }
            public string HardwareId { get; set; }
        }
        
        /// <summary>
        /// Verifies admin via API key and hardware ID
        /// If hardware_id is not set in DB, it will be set automatically (first binding)
        /// Falls back to files_api_key if relay_admins check fails
        /// </summary>
        private static async Task<AdminInfo?> VerifyAdmin(string apiKey, string hardwareId)
        {
            try
            {
                // AUTH CHECK 1: Try relay_admins table
                using var conn = new MySqlConnection(Configuration.MySQL.Connection_String);
                await conn.OpenAsync();
                
                using var cmd = new MySqlCommand(
                    "SELECT id, hardware_id FROM relay_admins WHERE api_key = @apiKey;", conn);
                cmd.Parameters.AddWithValue("@apiKey", apiKey);
                
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    int adminId = reader.GetInt32("id");
                    string dbHardwareId = reader.IsDBNull(reader.GetOrdinal("hardware_id")) 
                        ? null 
                        : reader.GetString("hardware_id");
                    
                    await reader.CloseAsync();
                    
                    // Case 1: Hardware ID not set yet - set it now (first binding)
                    if (string.IsNullOrEmpty(dbHardwareId))
                    {
                        using var updateCmd = new MySqlCommand(
                            "UPDATE relay_admins SET hardware_id = @hardwareId WHERE id = @id;", conn);
                        updateCmd.Parameters.AddWithValue("@hardwareId", hardwareId);
                        updateCmd.Parameters.AddWithValue("@id", adminId);
                        await updateCmd.ExecuteNonQueryAsync();
                        
                        Logging.Handler.Debug("RelayHandler", "VerifyAdmin",
                            $"Hardware ID '{hardwareId}' automatically set for admin ID {adminId}");
                        
                        return new AdminInfo
                        {
                            Id = adminId,
                            ApiKey = apiKey,
                            HardwareId = hardwareId
                        };
                    }
                    
                    // Case 2: Hardware ID matches - OK
                    if (dbHardwareId == hardwareId)
                    {
                        return new AdminInfo
                        {
                            Id = adminId,
                            ApiKey = apiKey,
                            HardwareId = dbHardwareId
                        };
                    }
                    
                    // Case 3: Hardware ID mismatch - REJECT
                    Logging.Handler.Warning("RelayHandler", "VerifyAdmin",
                        $"Hardware ID mismatch for admin ID {adminId}. Expected: '{dbHardwareId}', Got: '{hardwareId}'");
                    
                    return null;
                }
                
                // AUTH CHECK 2: If relay_admins fails, check files_api_key as fallback
                string filesApiKey = await MySQL.Handler.Quick_Reader(
                    "SELECT files_api_key FROM settings LIMIT 1;", 
                    "files_api_key");
                
                if (!string.IsNullOrEmpty(filesApiKey) && filesApiKey == apiKey)
                {
                    Logging.Handler.Debug("RelayHandler", "VerifyAdmin",
                        "Authenticated via files_api_key fallback");
                    
                    // Return special AdminInfo for files_api_key (ID = -1 to indicate fallback)
                    return new AdminInfo
                    {
                        Id = -1,
                        ApiKey = apiKey,
                        HardwareId = hardwareId
                    };
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "VerifyAdmin", ex.ToString());
                return null;
            }
        }
        
        /// <summary>
        /// Creates a new relay session
        /// POST /admin/relay/create
        /// </summary>
        public static async Task CreateRelaySession(HttpContext context)
        {
            try
            {
                // AUTH: Verify admin credentials (relay_admins + files_api_key fallback)
                string apiKey = context.Request.Headers["X-Api-Key"].ToString();
                string hardwareId = context.Request.Headers["X-Hardware-Id"].ToString();
                
                var adminInfo = await VerifyAdmin(apiKey, hardwareId);

                if (adminInfo == null)
                {
                    // Fallback to files_api_key if relay_admins check fails
                    string filesApiKey = await MySQL.Handler.Quick_Reader(
                        "SELECT files_api_key FROM settings LIMIT 1;", 
                        "files_api_key");

                    if (string.IsNullOrEmpty(filesApiKey) || filesApiKey != apiKey)
                    {
                        Logging.Handler.Warning("RelayHandler", "ListRelaySessions", "Authorization failed - invalid credentials");
                        context.Response.StatusCode = 403;
                        await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid API key or credentials" });
                        return;
                    }
                    
                    Logging.Handler.Debug("RelayHandler", "ListRelaySessions", "Authorized via files_api_key fallback");
                }
                else
                {
                    Logging.Handler.Debug("RelayHandler", "ListRelaySessions", $"Authorized via relay_admins (Admin ID: {adminInfo.Id})");
                    
                    // Update last_used timestamp only if authenticated via relay_admins
                    await UpdateAdminLastUsed(adminInfo.Id);
                }
                
                string requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
                var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBody);

                if (request == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid request body" });
                    return;
                }

                // Parse Request - only target_device_id is required
                // target_device_id can be: access_key (recommended), hwid, or device_name
                string targetDeviceId = request.ContainsKey("target_device_id") 
                    ? request["target_device_id"].GetString() 
                    : null;
                    
                int targetPort = request.ContainsKey("target_port") 
                    ? request["target_port"].GetInt32() 
                    : 0;
                    
                string protocol = request.ContainsKey("protocol") 
                    ? request["protocol"].GetString() 
                    : "TCP";
                
                // Parameters for DB storage (optional - if not specified, only in-memory storage)
                bool saveToDb = request.ContainsKey("save_to_db") ? request["save_to_db"].GetBoolean() : false;
                string description = request.ContainsKey("description") ? request["description"].GetString() : "";
                string createdBy = request.ContainsKey("created_by") ? request["created_by"].GetString() : "unknown";

                // Validation
                if (string.IsNullOrEmpty(targetDeviceId) || targetPort <= 0)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        success = false, 
                        error = "Missing required parameters: target_device_id, target_port" 
                    });
                    return;
                }

                // Create session
                var result = await RelayServer.Instance.CreateRelaySession(
                    targetDeviceId, 
                    targetPort, 
                    protocol ?? "TCP");

                if (result.success)
                {
                    // If the data is to be stored in a database, store it there (= persistently).
                    if (saveToDb)
                    {
                        try
                        {
                            // Retrieve device_id from access_key for database storage
                            using var deviceConn = new MySqlConnection(Configuration.MySQL.Connection_String);
                            await deviceConn.OpenAsync();
                            
                            using var deviceCmd = new MySqlCommand(
                                "SELECT id FROM devices WHERE access_key = @accessKey;", deviceConn);
                            deviceCmd.Parameters.AddWithValue("@accessKey", targetDeviceId);
                            
                            var deviceResult = await deviceCmd.ExecuteScalarAsync();
                            
                            if (deviceResult != null)
                            {
                                int deviceIdInt = Convert.ToInt32(deviceResult);
                                
                                using var insertCmd = new MySqlCommand(@"
                                    INSERT INTO relay_sessions 
                                    (session_id, target_device_id, target_port, protocol, enabled, created_at, created_by, description)
                                    VALUES 
                                    (@sessionId, @targetDeviceId, @targetPort, @protocol, 1, NOW(), @createdBy, @description)
                                    ON DUPLICATE KEY UPDATE 
                                    description = @description;", deviceConn);
                                
                                insertCmd.Parameters.AddWithValue("@sessionId", result.sessionId);
                                insertCmd.Parameters.AddWithValue("@targetDeviceId", deviceIdInt);
                                insertCmd.Parameters.AddWithValue("@targetPort", targetPort);
                                insertCmd.Parameters.AddWithValue("@protocol", protocol?.ToUpper() ?? "TCP");
                                insertCmd.Parameters.AddWithValue("@createdBy", createdBy);
                                insertCmd.Parameters.AddWithValue("@description", description);
                                
                                await insertCmd.ExecuteNonQueryAsync();
                                Logging.Handler.Debug("RelayHandler", "CreateRelaySession_SaveToDB", $"Saved persistent session {result.sessionId} to database");
                            }
                            else
                            {
                                Logging.Handler.Warning("RelayHandler", "CreateRelaySession_SaveToDB", $"Could not find device_id for access_key {targetDeviceId}, session not saved to DB");
                            }
                        }
                        catch (Exception dbEx)
                        {
                            Logging.Handler.Error("RelayHandler", "CreateRelaySession_SaveToDB", dbEx.ToString());
                        }
                    }
                    
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = true,
                        session_id = result.sessionId,
                        protocol = protocol?.ToUpper() ?? "TCP",
                        is_persistent = saveToDb,
                        message = $"Admin can now connect to {result.relayServer}:Server.relay_port with session_id {result.sessionId}"
                    });

                    Logging.Handler.Debug("RelayHandler", "CreateRelaySession", 
                        $"Created relay session {result.sessionId} on port {result.relayPort} (persistent={saveToDb})");
                }
                else
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        success = false, 
                        error = result.error
                    });

                    Logging.Handler.Error("RelayHandler", "CreateRelaySession", 
                        $"Failed to create session: {result.error}");
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "CreateRelaySession", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new 
                { 
                    success = false, 
                    error = "Internal server error" 
                });
            }
        }

        /// <summary>
        /// Closes a relay session
        /// POST /admin/relay/close
        /// </summary>
        public static async Task CloseRelaySession(HttpContext context)
        {
            try
            {
                // Verify admin credentials (relay_admins or files_api_key)
                string apiKey = context.Request.Headers["X-Api-Key"].ToString();
                string hardwareId = context.Request.Headers["X-Hardware-Id"].ToString();
                
                var adminInfo = await VerifyAdmin(apiKey, hardwareId);
                
                if (adminInfo == null)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid API key or credentials" });
                    return;
                }
                
                string sessionId = context.Request.Query["session_id"];

                if (string.IsNullOrEmpty(sessionId))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        success = false, 
                        error = "Missing session_id parameter" 
                    });
                    return;
                }

                bool success = await RelayServer.Instance.CloseSession(sessionId);

                if (success)
                {
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsJsonAsync(new { success = true });
                    
                    Logging.Handler.Debug("RelayHandler", "CloseRelaySession", 
                        $"Closed relay session {sessionId}");
                }
                else
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        success = false, 
                        error = "Session not found" 
                    });
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "CloseRelaySession", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new 
                { 
                    success = false, 
                    error = "Internal server error" 
                });
            }
        }

        /// <summary>
        /// Lists all relay sessions (persistent ones from DB + active ones in memory)
        /// GET /admin/relay/list
        /// </summary>
        public static async Task ListRelaySessions(HttpContext context)
        {
            try
            {
                // AUTH: Verify admin credentials (relay_admins + files_api_key fallback)
                string apiKey = context.Request.Headers["X-Api-Key"].ToString();
                string hardwareId = context.Request.Headers["X-Hardware-Id"].ToString();
                
                var adminInfo = await VerifyAdmin(apiKey, hardwareId);

                if (adminInfo == null)
                {
                    // Fallback to files_api_key if relay_admins check fails
                    string filesApiKey = await MySQL.Handler.Quick_Reader(
                        "SELECT files_api_key FROM settings LIMIT 1;", 
                        "files_api_key");

                    if (string.IsNullOrEmpty(filesApiKey) || filesApiKey != apiKey)
                    {
                        Logging.Handler.Warning("RelayHandler", "ListRelaySessions", "Authorization failed - invalid credentials");
                        context.Response.StatusCode = 403;
                        await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid API key or credentials" });
                        return;
                    }
                    
                    Logging.Handler.Debug("RelayHandler", "ListRelaySessions", "Authorized via files_api_key fallback");
                }
                else
                {
                    Logging.Handler.Debug("RelayHandler", "ListRelaySessions", $"Authorized via relay_admins (Admin ID: {adminInfo.Id})");
                    
                    // Update last_used timestamp only if authenticated via relay_admins
                    await UpdateAdminLastUsed(adminInfo.Id);
                }
                
                var sessionList = new List<object>();
                var dbSessionIds = new HashSet<string>(); // Track which sessions come from DB
                
                // Retrieve persistent sessions from the database
                using (var conn = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String))
                {
                    await conn.OpenAsync();
                    
                    string query = "SELECT * FROM relay_sessions ORDER BY created_at DESC;";
                    using var cmd = new MySqlConnector.MySqlCommand(query, conn);
                    using var reader = await cmd.ExecuteReaderAsync();
                    
                    while (await reader.ReadAsync())
                    {
                        string sessionId = reader["session_id"].ToString() ?? "";
                        dbSessionIds.Add(sessionId);
                        
                        int targetDeviceId = reader["target_device_id"] != DBNull.Value 
                            ? Convert.ToInt32(reader["target_device_id"]) 
                            : 0;
                        int targetPort = Convert.ToInt32(reader["target_port"]);
                        string protocol = reader["protocol"].ToString() ?? "TCP";
                        int enabled = Convert.ToInt32(reader["enabled"]);
                        DateTime createdAt = reader["created_at"] != DBNull.Value 
                            ? Convert.ToDateTime(reader["created_at"]) 
                            : DateTime.MinValue;
                        DateTime? lastConnected = reader["last_connected"] != DBNull.Value 
                            ? Convert.ToDateTime(reader["last_connected"]) 
                            : (DateTime?)null;
                        string createdBy = reader["created_by"]?.ToString();
                        string description = reader["description"]?.ToString();
                        
                        // Retrieve device names and other information from the devices table using JOINs
                        string deviceName = null;
                        string tenantName = null;
                        string locationName = null;
                        string groupName = null;
                        
                        if (targetDeviceId > 0)
                        {
                            using var deviceConn = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String);
                            await deviceConn.OpenAsync();
                            
                            using var deviceCmd = new MySqlConnector.MySqlCommand(@"
                                SELECT 
                                    d.device_name,
                                    t.name AS tenant_name,
                                    l.name AS location_name,
                                    g.name AS group_name
                                FROM devices d
                                LEFT JOIN tenants t ON d.tenant_id = t.id
                                LEFT JOIN locations l ON d.location_id = l.id
                                LEFT JOIN `groups` g ON d.group_id = g.id
                                WHERE d.id = @deviceId;", deviceConn);
                            deviceCmd.Parameters.AddWithValue("@deviceId", targetDeviceId);
                            
                            using var deviceReader = await deviceCmd.ExecuteReaderAsync();
                            if (await deviceReader.ReadAsync())
                            {
                                deviceName = deviceReader["device_name"] != DBNull.Value 
                                    ? deviceReader["device_name"].ToString() 
                                    : null;
                                tenantName = deviceReader["tenant_name"] != DBNull.Value 
                                    ? deviceReader["tenant_name"].ToString() 
                                    : null;
                                locationName = deviceReader["location_name"] != DBNull.Value 
                                    ? deviceReader["location_name"].ToString() 
                                    : null;
                                groupName = deviceReader["group_name"] != DBNull.Value 
                                    ? deviceReader["group_name"].ToString() 
                                    : null;
                            }
                        }
                        
                        // Retrieve session from memory if available
                        var activeSession = RelayServer.Instance.GetSession(sessionId);
                        
                        // `is_active` is based on the actual `Session.IsActive` status.
                        bool isActive = activeSession?.IsActive ?? false;
                        
                        // Check if the target device is connected via SignalR (ready for Relay)
                        bool isDeviceOnline = false;
                        if (targetDeviceId > 0)
                        {
                            // Retrieve device access_key from devices table
                            string targetAccessKey = await MySQL.Handler.Quick_Reader(
                                $"SELECT access_key FROM devices WHERE id = {targetDeviceId};",
                                "access_key");
                            
                            if (!string.IsNullOrEmpty(targetAccessKey))
                            {
                                // Check if the device is present in SignalR Connections.
                                var connections = SignalR.CommandHubSingleton.Instance._clientConnections;
                                foreach (var signalRConn in connections)
                                {
                                    try
                                    {
                                        using var doc = System.Text.Json.JsonDocument.Parse(signalRConn.Value);
                                        if (doc.RootElement.TryGetProperty("device_identity", out var deviceIdentity))
                                        {
                                            if (deviceIdentity.TryGetProperty("access_key", out var accessKeyElement) &&
                                                accessKeyElement.GetString() == targetAccessKey)
                                            {
                                                isDeviceOnline = true;
                                                break;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        
                        Logging.Handler.Debug("RelayHandler", "ListRelaySessions", $"DB Session {sessionId}: InMemory={activeSession != null}, IsActive={isActive}, DeviceOnline={isDeviceOnline}");
                        
                        // Determine display status for frontend (without icon)
                        string displayStatus;
                        
                        if (enabled == 0)
                        {
                            if (activeSession != null && isActive)
                            {
                                displayStatus = "Disconnecting";
                            }
                            else
                            {
                                displayStatus = "Disabled";
                            }
                        }
                        else if (activeSession != null && isActive)
                        {
                            displayStatus = "Active";
                        }
                        else
                        {
                            // Session enabled, but not active
                            if (isDeviceOnline)
                            {
                                displayStatus = "Ready to connect";
                            }
                            else
                            {
                                displayStatus = "Device offline";
                            }
                        }
                        
                        sessionList.Add(new
                        {
                            id = reader["id"],
                            session_id = sessionId,
                            target_device_id = targetDeviceId.ToString(),
                            target_device_name = deviceName,
                            tenant_name = tenantName,
                            location_name = locationName,
                            group_name = groupName,
                            target_port = targetPort,
                            protocol = protocol,
                            enabled = enabled == 1,
                            is_persistent = true,
                            is_active = isActive,
                            is_device_ready = isDeviceOnline, // Device is ready for Relay (SignalR connected)
                            active_connections = activeSession?.ActiveConnections ?? 0,
                            created_at = createdAt,
                            created_by = createdBy,
                            last_connected = lastConnected,
                            description = description,
                            display_status = displayStatus // Frontend-friendly status (without icon)
                        });
                    }
                }
                
                // Retrieve all memory sessions and add those that are NOT in the database.
                var allMemorySessions = RelayServer.Instance.GetActiveSessions();
                foreach (var memorySession in allMemorySessions)
                {
                    if (!dbSessionIds.Contains(memorySession.SessionId))
                    {
                        // Memory-only session (non-persistent)
                        Logging.Handler.Debug("RelayHandler", "ListRelaySessions", $"Memory-only Session {memorySession.SessionId}: IsActive={memorySession.IsActive}");
                        
                        // Try to retrieve device information from TargetDeviceId (can be access_key, hwid, or device_name)
                        string deviceName = null;
                        string tenantName = null;
                        string locationName = null;
                        string groupName = null;
                        
                        try
                        {
                            using var deviceConn = new MySqlConnector.MySqlConnection(Configuration.MySQL.Connection_String);
                            await deviceConn.OpenAsync();
                            
                            // First try using JOINs via access_key, hwid or device_name.
                            using var deviceCmd = new MySqlConnector.MySqlCommand(@"
                                SELECT 
                                    d.device_name,
                                    t.name AS tenant_name,
                                    l.name AS location_name,
                                    g.name AS group_name
                                FROM devices d
                                LEFT JOIN tenants t ON d.tenant_id = t.id
                                LEFT JOIN locations l ON d.location_id = l.id
                                LEFT JOIN `groups` g ON d.group_id = g.id
                                WHERE d.access_key = @identifier 
                                   OR d.hwid = @identifier 
                                   OR d.device_name = @identifier 
                                LIMIT 1;", 
                                deviceConn);
                            deviceCmd.Parameters.AddWithValue("@identifier", memorySession.TargetDeviceId);
                            
                            using var deviceReader = await deviceCmd.ExecuteReaderAsync();
                            if (await deviceReader.ReadAsync())
                            {
                                deviceName = deviceReader["device_name"] != DBNull.Value 
                                    ? deviceReader["device_name"].ToString() 
                                    : null;
                                tenantName = deviceReader["tenant_name"] != DBNull.Value 
                                    ? deviceReader["tenant_name"].ToString() 
                                    : null;
                                locationName = deviceReader["location_name"] != DBNull.Value 
                                    ? deviceReader["location_name"].ToString() 
                                    : null;
                                groupName = deviceReader["group_name"] != DBNull.Value 
                                    ? deviceReader["group_name"].ToString() 
                                    : null;
                            }
                        }
                        catch
                        {
                            // If the lookup fails, the values remain zero.
                        }
                        
                        // Determine display status for memory-only sessions (without icon)
                        string displayStatus;
                        
                        if (memorySession.IsActive)
                        {
                            displayStatus = "Active";
                        }
                        else
                        {
                            displayStatus = "Inactive";
                        }
                        
                        sessionList.Add(new
                        {
                            id = (int?)null, // no db id
                            session_id = memorySession.SessionId,
                            target_device_id = memorySession.TargetDeviceId,
                            target_device_name = deviceName,
                            tenant_name = tenantName,
                            location_name = locationName,
                            group_name = groupName,
                            target_port = memorySession.TargetPort,
                            protocol = memorySession.Protocol,
                            enabled = true, // Memory-Sessions are always enabled
                            is_persistent = false, // Memory-only = non-persistent
                            is_active = memorySession.IsActive,
                            is_device_ready = false, // Memory-Sessions do not track device readiness
                            active_connections = memorySession.ActiveConnections,
                            created_at = memorySession.CreatedAt,
                            created_by = (string?)null,
                            last_connected = (DateTime?)null,
                            connection_string = $"{Configuration.Server.public_override_url}:placeholder",
                            description = (string?)null,
                            display_status = displayStatus // Frontend-friendly status (without icon)
                        });
                    }
                }

                context.Response.StatusCode = 200;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = true,
                    count = sessionList.Count,
                    sessions = sessionList
                });
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "ListRelaySessions", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new 
                { 
                    success = false, 
                    error = "Internal server error" 
                });
            }
        }

        /// <summary>
        /// Gets information about a specific relay session
        /// GET /admin/relay/info
        /// </summary>
        public static async Task GetRelaySessionInfo(HttpContext context)
        {
            try
            {
                // AUTH: Verify admin credentials (relay_admins + files_api_key fallback)
                string apiKey = context.Request.Headers["X-Api-Key"].ToString();
                string hardwareId = context.Request.Headers["X-Hardware-Id"].ToString();
                
                var adminInfo = await VerifyAdmin(apiKey, hardwareId);
                
                if (adminInfo == null)
                {
                    Logging.Handler.Warning("RelayHandler", "GetRelaySessionInfo", "Authorization failed - invalid credentials");
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid API key or credentials" });
                    return;
                }
                
                Logging.Handler.Debug("RelayHandler", "GetRelaySessionInfo", $"Authorized (Admin ID: {adminInfo.Id})");
                
                string sessionId = context.Request.Query["session_id"];

                if (string.IsNullOrEmpty(sessionId))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        success = false, 
                        error = "Missing session_id parameter" 
                    });
                    return;
                }

                var session = RelayServer.Instance.GetSession(sessionId);

                if (session == null)
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        success = false, 
                        error = "Session not found" 
                    });
                    return;
                }

                context.Response.StatusCode = 200;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = true,
                    session = new
                    {
                        session_id = session.SessionId,
                        target_device_id = session.TargetDeviceId,
                        target_port = session.TargetPort,
                        protocol = session.Protocol,
                        is_active = session.IsActive,
                        active_connections = session.ActiveConnections,
                        created_at = session.CreatedAt,
                        target_connection_id = session.TargetConnectionId
                    }
                });
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "GetRelaySessionInfo", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new 
                { 
                    success = false, 
                    error = "Internal server error" 
                });
            }
        }
        
        /// <summary>
       /// Admin announces connection to a session (HTTP verification BEFORE TCP connection)
       /// POST /admin/relay/announce
       /// Body: { "session_id": "...", "api_key": "...", "hardware_id": "...", "admin_public_key": "..." }
       ///
       /// Flow:
       /// 1. Admin is authenticated via relay_admins (api_key + hardware_id)
       /// 2. Session existence is checked
       /// 3. Admin public key is stored
       /// 4. SignalR command (Type 14) is sent to the agent with admin_public_key
       /// 5. Agent connects to the relay server
       /// 6. Admin can then also establish the TCP connection
        /// </summary>
        public static async Task AnnounceAdminConnection(HttpContext context)
        {
            try
            {
                string requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
                var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBody);

                if (request == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid request body" });
                    return;
                }

                // Parse Request
                string sessionId = request.ContainsKey("session_id")
                    ? request["session_id"].GetString()
                    : null;
                    
                string apiKey = request.ContainsKey("api_key")
                    ? request["api_key"].GetString()
                    : null;
                    
                string hardwareId = request.ContainsKey("hardware_id")
                    ? request["hardware_id"].GetString()
                    : null;
                    
                string adminPublicKey = request.ContainsKey("admin_public_key")
                    ? request["admin_public_key"].GetString()
                    : null;

                // Validation
                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(apiKey) || 
                    string.IsNullOrEmpty(hardwareId) || string.IsNullOrEmpty(adminPublicKey))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "Missing required parameters: session_id, api_key, hardware_id, admin_public_key"
                    });
                    return;
                }

                Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", $"Admin announcing connection to session {sessionId}");
                Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", $"Admin Public Key length: {adminPublicKey.Length}");

                // AUTH: Verify admin credentials (relay_admins + files_api_key fallback)
                var adminInfo = await VerifyAdmin(apiKey, hardwareId);
                
                if (adminInfo == null)
                {
                    Logging.Handler.Warning("RelayHandler", "AnnounceAdminConnection", "Authorization failed - invalid credentials");
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid API key or credentials" });
                    return;
                }
                
                Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", $"Authorized (Admin ID: {adminInfo.Id})");

                // 2. Check if a session exists (DB or Memory)
                var session = RelayServer.Instance.GetSession(sessionId);
                bool sessionExists = false;
                string targetAccessKey = null;
                int targetPort = 0;
                string protocol = "TCP";
                
                if (session != null)
                {
                    // Session exists in memory
                    sessionExists = true;
                    targetAccessKey = session.TargetDeviceId;
                    targetPort = session.TargetPort;
                    protocol = session.Protocol;
                    Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", "Session found in memory");
                }
                else
                {
                    // Check database for persistent session
                    using var conn = new MySqlConnection(Configuration.MySQL.Connection_String);
                    await conn.OpenAsync();
                    
                    using var cmd = new MySqlCommand(
                        "SELECT target_device_id, target_port, protocol FROM relay_sessions WHERE session_id = @sessionId AND enabled = 1;", 
                        conn);
                    cmd.Parameters.AddWithValue("@sessionId", sessionId);
                    
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        int deviceId = reader.GetInt32("target_device_id");
                        targetPort = reader.GetInt32("target_port");
                        protocol = reader.GetString("protocol");
                        
                        // Get access_key for the device
                        using var deviceConn = new MySqlConnection(Configuration.MySQL.Connection_String);
                        await deviceConn.OpenAsync();
                        
                        using var deviceCmd = new MySqlCommand(
                            "SELECT access_key FROM devices WHERE id = @deviceId;", deviceConn);
                        deviceCmd.Parameters.AddWithValue("@deviceId", deviceId);
                        
                        var accessKeyResult = await deviceCmd.ExecuteScalarAsync();
                        if (accessKeyResult != null)
                        {
                            targetAccessKey = accessKeyResult.ToString();
                            sessionExists = true;
                            Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", "Session found in database");
                        }
                    }
                }

                if (!sessionExists || string.IsNullOrEmpty(targetAccessKey))
                {
                    Logging.Handler.Warning("RelayHandler", "AnnounceAdminConnection", "Session not found or invalid");
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "Session not found or not enabled"
                    });
                    return;
                }

                Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", $"Target device: {targetAccessKey}, Port: {targetPort}");

                // 3. Save Admin Public Key in Session (in-memory)
                if (session != null)
                {
                    session.AdminPublicKey = adminPublicKey;
                    Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", "Admin Public Key stored in existing session");
                }
                else
                {
                    // Session exists only in DB - restore it with EXISTING session_id
                    Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", $"Restoring persistent session {sessionId} from database");
                    
                    var result = await RelayServer.Instance.RestorePersistentSessionByAccessKey(
                        sessionId, // Use the EXISTING sessionId!
                        targetAccessKey,
                        targetPort,
                        protocol,
                        null);
                        
                    if (result.success)
                    {
                        session = RelayServer.Instance.GetSession(sessionId);
                        if (session != null)
                        {
                            session.AdminPublicKey = adminPublicKey;
                            Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", "Restored persistent session and stored Admin Public Key");
                        }
                        else
                        {
                            Logging.Handler.Warning("RelayHandler", "AnnounceAdminConnection", "Session restored but GetSession returned null");
                        }
                    }
                    else
                    {
                        Logging.Handler.Error("RelayHandler", "AnnounceAdminConnection", $"Failed to restore persistent session: {result.error}");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            success = false,
                            error = $"Failed to initialize session: {result.error}"
                        });
                        return;
                    }
                }

                // 4. Send SignalR Command (Type 14) to agent with admin_public_key
                try
                {
                    var hubContext = SignalR.CommandHubSingleton.Instance.HubContext;
                    
                    // Get Connection ID for Target Device
                    string targetConnectionId = GetConnectionIdByAccessKey(targetAccessKey);
                    
                    if (string.IsNullOrEmpty(targetConnectionId))
                    {
                        Logging.Handler.Warning("RelayHandler", "AnnounceAdminConnection", $"Target device not connected (access_key: {targetAccessKey})");
                        context.Response.StatusCode = 503;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            success = false,
                            error = "Target device is not connected"
                        });
                        return;
                    }

                    Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", $"Sending SignalR command to agent (connection: {targetConnectionId})");

                    // Build relay details with admin_public_key
                    var relayDetails = new
                    {
                        session_id = sessionId,
                        local_port = targetPort,
                        protocol = protocol,
                        public_key = Configuration.Server.relay_public_key_pem,
                        fingerprint = Configuration.Server.relay_public_key_fingerprint,
                        admin_public_key = adminPublicKey // Admin Public Key for E2EE!
                    };

                    var targetCommand = new SignalR.CommandHub.Command
                    {
                        type = 14, // Relay Connection Request
                        wait_response = false,
                        command = JsonSerializer.Serialize(relayDetails)
                    };

                    await hubContext.Clients.Client(targetConnectionId)
                        .SendCoreAsync("SendMessageToClient", new object[] { JsonSerializer.Serialize(targetCommand) });

                    Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection", "SignalR command sent to agent");
                    
                    // Update last_used for Admin
                    await UpdateAdminLastUsed(adminInfo.Id);

                    // Response
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = true,
                        session_id = sessionId,
                        message = "Agent notified - you can now connect via TCP to port Server.relay_port"
                    });

                    Logging.Handler.Debug("RelayHandler", "AnnounceAdminConnection",
                        $"Admin announced connection to session {sessionId}, agent notified");
                }
                catch (Exception signalREx)
                {
                    Logging.Handler.Error("RelayHandler", "AnnounceAdminConnection_SignalR", signalREx.ToString());
                    
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "Failed to notify agent"
                    });
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "AnnounceAdminConnection", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Internal server error"
                });
            }
        }

        /// <summary>
        /// Helper method: Retrieve connection ID for a device using the access_key
        /// </summary>
        private static string GetConnectionIdByAccessKey(string accessKey)
        {
            var connections = SignalR.CommandHubSingleton.Instance._clientConnections;
            
            foreach (var kvp in connections)
            {
                try
                {
                    var identityJson = kvp.Value;
                    using var document = JsonDocument.Parse(identityJson);
                    
                    if (document.RootElement.TryGetProperty("device_identity", out var deviceIdentity))
                    {
                        if (deviceIdentity.TryGetProperty("access_key", out var keyElement) &&
                            keyElement.GetString() == accessKey)
                        {
                            return kvp.Key;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Debug("RelayHandler", "GetConnectionIdByAccessKey",
                        $"Failed to parse identity for connection {kvp.Key}: {ex.Message}");
                }
            }
            
            return null;
        }

        /// <summary>
        /// Updated last_used timestamp for admin
        /// </summary>
        private static async Task UpdateAdminLastUsed(int adminId)
        {
            try
            {
                using var conn = new MySqlConnection(Configuration.MySQL.Connection_String);
                await conn.OpenAsync();
                
                using var cmd = new MySqlCommand(
                    "UPDATE relay_admins SET last_used = NOW() WHERE id = @adminId;", conn);
                cmd.Parameters.AddWithValue("@adminId", adminId);
                
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "UpdateAdminLastUsed", ex.ToString());
            }
        }

        /// <summary>
        /// Registers/links an admin client for relay connections
        /// POST /admin/relay/register
        /// Body: { "api_key": "existing-uuid", "hardware_id": "xyz" }
        ///
        /// Logic:
        /// 1. API key MUST exist in the database (created beforehand)
        /// 2. If hardware_id in the database is NULL/empty -> set hardware_id (first association)
        /// 3. If hardware_id is set and matches -> OK
        /// 4. If hardware_id is set but does not match -> REJECT
        /// </summary>

        public static async Task RegisterRelayAdminClient(HttpContext context)
        {
            try
            {
                string requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
                var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBody);

                if (request == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid request body" });
                    return;
                }

                // Parse Request
                string apiKey = request.ContainsKey("api_key") ? request["api_key"].GetString() : null;

                string hardwareId = request.ContainsKey("hardware_id") ? request["hardware_id"].GetString() : null;

                // Validation
                if (string.IsNullOrEmpty(apiKey))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "api_key is required"
                    });
                    return;
                }

                if (string.IsNullOrEmpty(hardwareId))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "hardware_id is required"
                    });
                    return;
                }

                // Check if an API key exists and retrieve the associated hardware ID.
                using var checkConn = new MySqlConnection(Configuration.MySQL.Connection_String);
                await checkConn.OpenAsync();

                using var checkCmd = new MySqlCommand(
                    "SELECT id, hardware_id FROM relay_admins WHERE api_key = @apiKey;", checkConn);
                checkCmd.Parameters.AddWithValue("@apiKey", apiKey);

                int? adminRecordId = null;
                string existingHardwareId = null;
                
                using (var reader = await checkCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        adminRecordId = reader.GetInt32("id");
                        existingHardwareId = reader.IsDBNull(reader.GetOrdinal("hardware_id")) 
                            ? null 
                            : reader.GetString("hardware_id");
                    }
                }

                // API key does not exist
                if (!adminRecordId.HasValue)
                {
                    Logging.Handler.Warning("RelayHandler", "RegisterRelayAdminClient", $"Invalid API key: {apiKey}");
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "Invalid API key or hardware id"
                    });
                    return;
                }

                // Case 1: Hardware ID is not yet set -> Set it now (first link)
                if (string.IsNullOrEmpty(existingHardwareId))
                {
                    using var updateCmd = new MySqlCommand("UPDATE relay_admins SET hardware_id = @hardwareId WHERE id = @id;", checkConn);
                    updateCmd.Parameters.AddWithValue("@hardwareId", hardwareId);
                    updateCmd.Parameters.AddWithValue("@id", adminRecordId.Value);
                    await updateCmd.ExecuteNonQueryAsync();

                    Logging.Handler.Debug("RelayHandler", "RegisterRelayAdminClient", $"Linked hardware_id '{hardwareId}' to API key (ID: {adminRecordId})");

                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = true,
                        api_key = apiKey,
                        hardware_id = hardwareId,
                        message = "Hardware ID successfully linked to API key."
                    });

                    return;
                }

                // Case 2: Hardware ID matches -> OK
                if (existingHardwareId == hardwareId)
                {
                    Logging.Handler.Debug("RelayHandler", "RegisterRelayAdminClient", "Hardware ID matches for API key");
                    
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = true,
                        api_key = apiKey,
                        hardware_id = hardwareId,
                        message = "API key and hardware ID verified successfully."
                    });
                    return;
                }

                // Case 3: Hardware IDs differ -> REJECT
                Logging.Handler.Warning("RelayHandler", "RegisterRelayAdminClient",
                    $"Hardware ID mismatch for API key. Expected: '{existingHardwareId}', Got: '{hardwareId}'");
                
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Invalid API key or hardware id"
                });
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "RegisterRelayAdminClient", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Internal server error"
                });
            }
        }

        /// <summary>
        /// Cleanly disconnects an admin client from a session
        /// POST /admin/relay/disconnect
        /// Body: { "session_id": "...", "api_key": "...", "hardware_id": "..." }
        /// </summary>
        public static async Task DisconnectAdminFromSession(HttpContext context)
        {
            try
            {
                string requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
                var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBody);

                if (request == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid request body" });
                    return;
                }

                // Parse Request
                string sessionId = request.ContainsKey("session_id")
                    ? request["session_id"].GetString()
                    : null;

                string apiKey = request.ContainsKey("api_key")
                    ? request["api_key"].GetString()
                    : null;

                string hardwareId = request.ContainsKey("hardware_id")
                    ? request["hardware_id"].GetString()
                    : null;

                // Validation
                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(hardwareId))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "session_id, api_key and hardware_id are required"
                    });
                    return;
                }

                Logging.Handler.Debug("RelayHandler", "DisconnectAdminFromSession", $"Admin disconnect request for session {sessionId}");

                // Verify API key and hardware ID
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(hardwareId))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "Missing X-Api-Key or X-Hardware-Id header" });
                    return;
                }
                
                var adminInfo = await VerifyAdmin(apiKey, hardwareId);
                
                if (adminInfo == null)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid API key or credentials" });
                    return;
                }

                // Retrieve session from relay server
                var session = Relay.RelayServer.Instance.GetSession(sessionId);
                
                if (session == null)
                {
                    Logging.Handler.Warning("RelayHandler", "DisconnectAdminFromSession", $"Session {sessionId} not found");
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "Session not found"
                    });
                    return;
                }

                Logging.Handler.Debug("RelayHandler", "DisconnectAdminFromSession", $"Cleaning up session {sessionId} for device {session.TargetDeviceId}");

                // Perform cleanup on the relay server (without deleting the session from the database!).
                bool cleanupSuccess = await Relay.RelayServer.Instance.CleanupAdminConnection(sessionId, session.TargetDeviceId, apiKey, hardwareId);

                if (cleanupSuccess)
                {
                    Logging.Handler.Debug("RelayHandler", "DisconnectAdminFromSession", $"Successfully cleaned up session {sessionId}");
                    
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = true,
                        message = "Admin disconnected successfully"
                    });
                }
                else
                {
                    Logging.Handler.Warning("RelayHandler", "DisconnectAdminFromSession", $"Cleanup returned false for session {sessionId}");
                    
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "Cleanup failed"
                    });
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "DisconnectAdminFromSession", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Internal server error"
                });
            }
        }

        /// <summary>
        /// Returns the server's public RSA key.
        /// GET /relay/public-key
        /// Admin authentication required (relay_admins: api_key + hardware_id)
        /// Headers: X-Api-Key, X-Hardware-Id
        /// </summary>
        public static async Task GetRelayPublicKey(HttpContext context)
        {
            try
            {
                Logging.Handler.Debug("RelayHandler", "GetRelayPublicKey", "Public key requested");
                // AUTH CHECK: VerifyAdmin (relay_admins: api_key + hardware_id)
                string apiKey = context.Request.Headers["X-Api-Key"].ToString();
                string hardwareId = context.Request.Headers["X-Hardware-Id"].ToString();
                
                Logging.Handler.Debug("RelayHandler", "GetRelayPublicKey", $"X-Api-Key: {apiKey}");
                Logging.Handler.Debug("RelayHandler", "GetRelayPublicKey", $"X-Hardware-Id: {hardwareId}");
                
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(hardwareId))
                {
                    Logging.Handler.Warning("RelayHandler", "GetRelayPublicKey", "Missing authentication headers");
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        success = false, 
                        error = "Missing X-Api-Key or X-Hardware-Id header" 
                    });
                    return;
                }
                
                var adminInfo = await VerifyAdmin(apiKey, hardwareId);
                
                if (adminInfo == null)
                {
                    Logging.Handler.Warning("RelayHandler", "GetRelayPublicKey", "Invalid admin credentials");
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        success = false, 
                        error = "Invalid API key or hardware ID" 
                    });
                    return;
                }

                context.Response.StatusCode = 200;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = true,
                    public_key = Configuration.Server.relay_public_key_pem,
                    fingerprint = Configuration.Server.relay_public_key_fingerprint,
                    algorithm = "RSA-4096",
                    padding = "OAEP-SHA256"
                });

                Logging.Handler.Debug("RelayHandler", "GetRelayPublicKey", 
                    $"Public key requested by authenticated admin (ID: {adminInfo.Id})");
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "GetRelayPublicKey", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Failed to retrieve public key"
                });
            }
        }

        /// <summary>
        /// Rotate the RSA key pair manually (admin-only)
        /// POST /admin/relay/rotate-keys
        /// Requires Admin API key
        /// WARNING: Invalidates all active sessions!
        /// </summary>
        public static async Task RotateRelayKeys(HttpContext context)
        {
            try
            {
                // AUTH CHECK: files_api_key
                string adminApiKey = await NetLock_RMM_Server.MySQL.Handler.Quick_Reader(
                    "SELECT files_api_key FROM settings LIMIT 1;", "files_api_key");
                
                if (adminApiKey == null || context.Request.Headers["X-Api-Key"] != adminApiKey)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { success = false, error = "Invalid API key" });
                    return;
                }

                // Rotate Keys
                bool success = await RelayEncryption.RotateServerKeys();

                if (success)
                {
                    // Update cached public key and fingerprint
                    Configuration.Server.relay_public_key_pem = RelayEncryption.GetPublicKeyPem();
                    Configuration.Server.relay_public_key_fingerprint = RelayEncryption.GetPublicKeyFingerprint();
                    
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = true,
                        message = "RSA keypair rotated successfully",
                        new_fingerprint = Configuration.Server.relay_public_key_fingerprint
                    });

                    Logging.Handler.Warning("RelayHandler", "RotateRelayKeys", 
                        "RSA keys manually rotated - all active sessions invalidated - cache updated");
                }
                else
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "Key rotation failed"
                    });
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "RotateRelayKeys", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Internal server error"
                });
            }
        }
        
        /// <summary>
        /// Checks if a session has been kicked (status polling for Relay App)
        /// GET /admin/relay/session/{session_id}/status
        /// </summary>
        public static async Task GetSessionKickStatus(HttpContext context)
        {
            try
            {
                // Parse Request Body
                string requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
                var request = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBody);
                
                if (request == null)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        success = false, 
                        error = "Invalid request body" 
                    });
                    return;
                }
                
                // Parse Request
                string sessionId = request.ContainsKey("session_id")
                    ? request["session_id"].GetString()
                    : null;
                
                string apiKey = request.ContainsKey("api_key")
                    ? request["api_key"].GetString()
                    : null;
                
                string hardwareId = request.ContainsKey("hardware_id")
                    ? request["hardware_id"].GetString()
                    : null;
                
                // Validation
                if (string.IsNullOrEmpty(sessionId))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new 
                    { 
                        success = false, 
                        error = "session_id is required" 
                    });
                    return;
                }
                
                if (string.IsNullOrEmpty(apiKey))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "api_key is required"
                    });
                    return;
                }
                
                if (string.IsNullOrEmpty(hardwareId))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "hardware_id is required"
                    });
                    return;
                }
                
                // Verify admin via API key and hardware ID
                var adminInfo = await VerifyAdmin(apiKey, hardwareId);
                
                if (adminInfo == null)
                {
                    Logging.Handler.Warning("RelayHandler", "GetSessionKickStatus", $"Invalid credentials for session {sessionId}");
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        error = "Invalid credentials"
                    });
                    return;
                }
                
                Logging.Handler.Debug("RelayHandler", "GetSessionKickStatus", $"Admin authenticated (ID: {adminInfo.Id}), checking session {sessionId}");
                
                // Check if the session was kicked.
                var kickInfo = RelayServer.Instance.GetKickStatus(sessionId);
                
                if (kickInfo != null)
                {
                    // The session was kicked off!
                    Logging.Handler.Info("RelayHandler", "GetSessionKickStatus", $"Session {sessionId} was kicked: {kickInfo.Reason}");
                    
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        kicked = true,
                        reason = kickInfo.Reason,
                        kicked_at = kickInfo.KickedAt,
                        kicked_by = kickInfo.KickedBy
                    });
                    
                    // Optional: Remove kick status after retrieval (one-time notification)
                    RelayServer.Instance.ClearKickStatus(sessionId);
                    
                    Logging.Handler.Debug("RelayHandler", "GetSessionKickStatus",
                        $"Kick status delivered for session {sessionId}: {kickInfo.Reason}");
                }
                else
                {
                    // Session is OK (not kicked)
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        kicked = false
                    });
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayHandler", "GetSessionKickStatus", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Internal server error"
                });
            }
        }
    }
}


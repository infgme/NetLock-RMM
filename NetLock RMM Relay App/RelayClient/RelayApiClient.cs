using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Global.Helper;

namespace NetLock_RMM_Relay_App.RelayClient
{
    /// <summary>
    /// API client for communicating with relay backend
    /// </summary>
    public class RelayApiClient
    {
        private readonly string _backendUrl;
        private readonly string _apiKey;
        private readonly string _hardwareId;
        private readonly HttpClient _httpClient;

        public RelayApiClient(string backendUrl, string apiKey, string hardwareId)
        {
            _backendUrl = backendUrl.TrimEnd('/');
            _apiKey = apiKey;
            _hardwareId = hardwareId;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>
        /// Tests connection to backend server
        /// </summary>
        public async Task<(bool success, string? error)> TestConnection()
        {
            try
            {
                Logging.Debug("RelayApiClient", "TestConnection", 
                    $"Testing connection to: {_backendUrl}/test");
                
                var response = await _httpClient.GetAsync($"{_backendUrl}/test");
                
                Logging.Debug("RelayApiClient", "TestConnection", 
                    $"Response status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                    return (true, null);
                
                return (false, $"Server returned status code: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                Logging.Error("RelayApiClient", "TestConnection", ex.ToString());
                return (false, $"Connection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Registers the relay client with the backend
        /// </summary>
        public async Task<(bool success, string? error)> RegisterRelayClient()
        {
            try
            {
                Logging.Debug("RelayApiClient", "RegisterRelayClient", 
                    $"Registering at: {_backendUrl}/admin/relay/register");
                
                var request = new HttpRequestMessage(HttpMethod.Post, 
                    $"{_backendUrl}/admin/relay/register");
                
                var requestBody = new
                {
                    api_key = _apiKey,
                    hardware_id = _hardwareId
                };
                
                string requestJson = JsonSerializer.Serialize(requestBody);
                Logging.Debug("RelayApiClient", "RegisterRelayClient", 
                    $"Request body: {requestJson}");
                
                request.Content = new StringContent(
                    requestJson,
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                
                Logging.Debug("RelayApiClient", "RegisterRelayClient", 
                    $"Response status: {response.StatusCode}, body: {responseBody}");
                
                if (!response.IsSuccessStatusCode)
                {
                    Logging.Error("RelayApiClient", "RegisterRelayClient", 
                        $"HTTP {response.StatusCode}: {responseBody}");
                    
                    // Try to parse error message from JSON response
                    try
                    {
                        var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(responseBody, 
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        
                        if (errorResponse?.error != null)
                        {
                            return (false, errorResponse.error);
                        }
                    }
                    catch
                    {
                        // If JSON parsing fails, return raw error body
                    }
                    
                    // Return more specific error messages
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return (false, "Registration endpoint not found. Please ensure the backend server is running and accessible.");
                    }
                    
                    return (false, $"{response.StatusCode}: {responseBody}");
                }
                
                Logging.Debug("RelayApiClient", "RegisterRelayClient", "Registration successful");
                return (true, null);
            }
            catch (HttpRequestException ex)
            {
                Logging.Error("RelayApiClient", "RegisterRelayClient", ex.ToString());
                return (false, $"Network error: {ex.Message}. Please check if the backend URL is correct and the server is reachable.");
            }
            catch (Exception ex)
            {
                Logging.Error("RelayApiClient", "RegisterRelayClient", ex.ToString());
                return (false, $"Registration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves the server public key and fingerprint (for E2EE and TOFU)
        /// </summary>
        public async Task<(bool success, string? publicKey, string? fingerprint, string? error)> GetServerPublicKey()
        {
            try
            {
                Logging.Debug("RelayApiClient", "GetServerPublicKey", 
                    $"Requesting server public key from: {_backendUrl}/relay/public-key");
                
                // Create request with authentication
                var request = new HttpRequestMessage(HttpMethod.Get, 
                    $"{_backendUrl}/relay/public-key");
                
                request.Headers.Add("X-API-Key", _apiKey);
                request.Headers.Add("X-Hardware-ID", _hardwareId);
                
                var response = await _httpClient.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                
                Logging.Debug("RelayApiClient", "GetServerPublicKey", 
                    $"Response status: {response.StatusCode}");
                
                if (!response.IsSuccessStatusCode)
                {
                    Logging.Error("RelayApiClient", "GetServerPublicKey", 
                        $"HTTP {response.StatusCode}: {responseBody}");
                    return (false, null, null, $"Server returned status code: {response.StatusCode}");
                }
                
                var keyResponse = JsonSerializer.Deserialize<PublicKeyResponse>(responseBody, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (keyResponse == null || string.IsNullOrEmpty(keyResponse.public_key))
                {
                    return (false, null, null, "Invalid response from server");
                }
                
                Logging.Debug("RelayApiClient", "GetServerPublicKey", 
                    $"Received public key (length: {keyResponse.public_key.Length}), fingerprint: {keyResponse.fingerprint}");
                
                return (true, keyResponse.public_key, keyResponse.fingerprint, null);
            }
            catch (HttpRequestException ex)
            {
                Logging.Error("RelayApiClient", "GetServerPublicKey", ex.ToString());
                return (false, null, null, $"Network error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logging.Error("RelayApiClient", "GetServerPublicKey", ex.ToString());
                return (false, null, null, $"Failed to get server public key: {ex.Message}");
            }
        }

        /// <summary>
        /// Disconnects admin from a relay session
        /// </summary>
        public async Task<(bool success, string? error)> DisconnectFromSession(string sessionId)
        {
            try
            {
                Logging.Debug("RelayApiClient", "DisconnectFromSession", 
                    $"Disconnecting from session: {sessionId}");
                
                var request = new HttpRequestMessage(HttpMethod.Post, 
                    $"{_backendUrl}/admin/relay/disconnect");
                
                var requestBody = new
                {
                    session_id = sessionId,
                    api_key = _apiKey,
                    hardware_id = _hardwareId
                };
                
                string requestJson = JsonSerializer.Serialize(requestBody);
                Logging.Debug("RelayApiClient", "DisconnectFromSession", 
                    $"Request body: {requestJson}");
                
                request.Content = new StringContent(
                    requestJson,
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                
                Logging.Debug("RelayApiClient", "DisconnectFromSession", 
                    $"Response status: {response.StatusCode}, body: {responseBody}");
                
                if (!response.IsSuccessStatusCode)
                {
                    Logging.Error("RelayApiClient", "DisconnectFromSession", 
                        $"HTTP {response.StatusCode}: {responseBody}");
                    
                    // Try to parse error message from JSON response
                    try
                    {
                        var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(responseBody, 
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        
                        if (errorResponse?.error != null)
                        {
                            return (false, errorResponse.error);
                        }
                    }
                    catch
                    {
                        // If JSON parsing fails, return raw error body
                    }
                    
                    return (false, $"{response.StatusCode}: {responseBody}");
                }
                
                Logging.Debug("RelayApiClient", "DisconnectFromSession", 
                    $"Successfully disconnected from session {sessionId}");
                return (true, null);
            }
            catch (HttpRequestException ex)
            {
                Logging.Error("RelayApiClient", "DisconnectFromSession", ex.ToString());
                return (false, $"Network error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logging.Error("RelayApiClient", "DisconnectFromSession", ex.ToString());
                return (false, $"Disconnect failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves list of available relay sessions
        /// </summary>
        public async Task<(bool success, List<RelaySessionInfo>? sessions, string? error)> GetAvailableSessions()
        {
            try
            {
                // Create request with authentication
                var request = new HttpRequestMessage(HttpMethod.Get, 
                    $"{_backendUrl}/admin/relay/list");
                
                request.Headers.Add("X-API-Key", _apiKey);
                request.Headers.Add("X-Hardware-ID", _hardwareId);
                
                Logging.Debug("RelayApiClient", "GetAvailableSessions", 
                    $"Requesting sessions from: {_backendUrl}/admin/relay/list");
                Logging.Debug("RelayApiClient", "GetAvailableSessions", 
                    $"API Key: {_apiKey.Substring(0, Math.Min(8, _apiKey.Length))}...");
                Logging.Debug("RelayApiClient", "GetAvailableSessions", 
                    $"Hardware ID: {_hardwareId.Substring(0, Math.Min(8, _hardwareId.Length))}...");
                
                var response = await _httpClient.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                
                Logging.Debug("RelayApiClient", "GetAvailableSessions", 
                    $"Response status: {response.StatusCode}, body length: {responseBody.Length}");
                
                if (!response.IsSuccessStatusCode)
                {
                    Logging.Error("RelayApiClient", "GetAvailableSessions", 
                        $"HTTP {response.StatusCode}: {responseBody}");
                    
                    // Handle specific error cases
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return (false, null, "Sessions endpoint not found. Please ensure the backend server supports relay sessions at /admin/relay/list");
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
                             response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        // Try to parse error message
                        try
                        {
                            var errorResponse = JsonSerializer.Deserialize<ErrorResponse>(responseBody, 
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            
                            if (errorResponse?.error != null)
                            {
                                return (false, null, errorResponse.error);
                            }
                        }
                        catch { }
                        
                        return (false, null, "Authentication failed. Please check your API key and ensure your hardware ID is registered.");
                    }
                    
                    return (false, null, $"{response.StatusCode}: {responseBody}");
                }
                
                Logging.Debug("RelayApiClient", "GetAvailableSessions", 
                    $"Response body: {responseBody}");
                
                var sessionsResponse = JsonSerializer.Deserialize<SessionsResponse>(responseBody, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                var sessions = sessionsResponse?.sessions ?? new List<RelaySessionInfo>();
                Logging.Debug("RelayApiClient", "GetAvailableSessions", 
                    $"Retrieved {sessions.Count} sessions");
                
                return (true, sessions, null);
            }
            catch (HttpRequestException ex)
            {
                Logging.Error("RelayApiClient", "GetAvailableSessions", ex.ToString());
                return (false, null, $"Network error: {ex.Message}. Please check if the backend URL is correct.");
            }
            catch (JsonException ex)
            {
                Logging.Error("RelayApiClient", "GetAvailableSessions", ex.ToString());
                return (false, null, $"Invalid response format from server: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logging.Error("RelayApiClient", "GetAvailableSessions", ex.ToString());
                return (false, null, $"Unexpected error: {ex.Message}");
            }
        }
        
        #region Response Models
        
        private class SessionsResponse
        {
            public List<RelaySessionInfo>? sessions { get; set; }
        }
        
        private class ErrorResponse
        {
            public bool success { get; set; }
            public string? error { get; set; }
        }
        
        private class PublicKeyResponse
        {
            public string public_key { get; set; } = "";
            public string fingerprint { get; set; } = "";
        }
        
        #endregion
    }
    
    /// <summary>
    /// Represents a relay session
    /// </summary>
    public class RelaySessionInfo
    {
        public string session_id { get; set; } = "";
        public string target_device_id { get; set; } = "";
        public string? target_device_name { get; set; }
        public int target_port { get; set; }
        public string protocol { get; set; } = "TCP";
        public DateTime created_at { get; set; }
        public bool is_active { get; set; }
        public bool enabled { get; set; } = true;
        public int active_connections { get; set; } = 0;
        
        // Organization information
        public string? tenant_name { get; set; }
        public string? location_name { get; set; }
        public string? group_name { get; set; }
        
        // Backend-provided status information (from /admin/relay/list)
        public string? display_status { get; set; }  // "Active", "Ready to connect", "Device offline", "Disabled", "Disconnecting"
        public bool is_device_ready { get; set; }     // true = Device is online and ready for relay (SignalR connected)
        
        // Display properties
        public string DisplayName => 
            $"{target_device_name ?? target_device_id} - {protocol}:{target_port}";
        
        public string DisplayStatus
        {
            get
            {
                // Use Backend display_status if available
                if (!string.IsNullOrEmpty(display_status))
                    return display_status;
                
                // Fallback to old logic (if Backend not updated)
                if (is_active && enabled)
                    return "Active";
                else if (!is_active && enabled)
                    return "Waiting for device";
                else if (is_active && !enabled)
                    return "Disabled - Disconnecting...";
                else
                    return "Disabled";
            }
        }
        
        public string OrganizationInfo
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(tenant_name))
                    parts.Add(tenant_name);
                if (!string.IsNullOrEmpty(location_name))
                    parts.Add(location_name);
                if (!string.IsNullOrEmpty(group_name))
                    parts.Add(group_name);
                
                return parts.Count > 0 ? string.Join(" / ", parts) : "";
            }
        }
    }
}


using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Global.Encryption;
using Global.Helper;

namespace NetLock_RMM_Relay_App.RelayClient
{
    /// <summary>
    /// Manages persistent port assignments for relay sessions
    /// Port mappings are encrypted and stored locally
    /// </summary>
    public class RelayPortManager
    {
        private static readonly string ConfigFile = Path.Combine(
            Application_Paths.application_data_directory,
            "relay_ports.dat"
        );

        private readonly string _encryptionKey;
        private Dictionary<string, int> _portMappings;

        public RelayPortManager(string apiKey)
        {
            _encryptionKey = apiKey;
            _portMappings = new Dictionary<string, int>();
            LoadPortMappings();
        }

        /// <summary>
        /// Gets or assigns a port for a session ID
        /// </summary>
        public int GetOrAssignPort(string sessionId)
        {
            try
            {
                // Check if we already have a port for this session
                if (_portMappings.TryGetValue(sessionId, out int existingPort))
                {
                    // Verify the port is still available
                    if (IsPortAvailable(existingPort))
                    {
                        Logging.Debug("RelayPortManager", "GetOrAssignPort",
                            $"Using saved port {existingPort} for session {sessionId}");
                        return existingPort;
                    }
                    else
                    {
                        Logging.Debug("RelayPortManager", "GetOrAssignPort",
                            $"Saved port {existingPort} is not available, finding new port");
                    }
                }

                // Find a new available port
                int newPort = FindAvailablePortForSession(sessionId);
                
                // Save the mapping
                _portMappings[sessionId] = newPort;
                SavePortMappings();
                
                Logging.Debug("RelayPortManager", "GetOrAssignPort",
                    $"Assigned new port {newPort} for session {sessionId}");
                
                return newPort;
            }
            catch (Exception ex)
            {
                Logging.Error("RelayPortManager", "GetOrAssignPort", ex.ToString());
                
                // Fallback to deterministic port generation
                return GenerateDeterministicPort(sessionId);
            }
        }

        /// <summary>
        /// Removes a port mapping for a session
        /// </summary>
        public void RemovePortMapping(string sessionId)
        {
            try
            {
                if (_portMappings.ContainsKey(sessionId))
                {
                    _portMappings.Remove(sessionId);
                    SavePortMappings();
                    
                    Logging.Debug("RelayPortManager", "RemovePortMapping",
                        $"Removed port mapping for session {sessionId}");
                }
            }
            catch (Exception ex)
            {
                Logging.Error("RelayPortManager", "RemovePortMapping", ex.ToString());
            }
        }

        /// <summary>
        /// Loads port mappings from encrypted config file
        /// </summary>
        private void LoadPortMappings()
        {
            try
            {
                if (!File.Exists(ConfigFile))
                {
                    Logging.Debug("RelayPortManager", "LoadPortMappings",
                        "No saved port mappings found");
                    return;
                }

                string encryptedData = File.ReadAllText(ConfigFile);
                
                if (string.IsNullOrWhiteSpace(encryptedData))
                {
                    Logging.Debug("RelayPortManager", "LoadPortMappings",
                        "Config file is empty");
                    return;
                }

                // Decrypt the data using API key
                string decryptedJson = String_Encryption.Decrypt(encryptedData, _encryptionKey);
                
                // Deserialize JSON
                _portMappings = JsonSerializer.Deserialize<Dictionary<string, int>>(decryptedJson)
                    ?? new Dictionary<string, int>();
                
                Logging.Debug("RelayPortManager", "LoadPortMappings",
                    $"Loaded {_portMappings.Count} port mapping(s)");
            }
            catch (Exception ex)
            {
                Logging.Error("RelayPortManager", "LoadPortMappings", 
                    $"Failed to load port mappings: {ex.Message}");
                
                // If decryption fails (e.g., different API key), start fresh
                _portMappings = new Dictionary<string, int>();
            }
        }

        /// <summary>
        /// Saves port mappings to encrypted config file
        /// </summary>
        private void SavePortMappings()
        {
            try
            {
                // Ensure directory exists
                string? directory = Path.GetDirectoryName(ConfigFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Serialize to JSON
                string json = JsonSerializer.Serialize(_portMappings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                // Encrypt with API key
                string encryptedData = String_Encryption.Encrypt(json, _encryptionKey);

                // Save to file
                File.WriteAllText(ConfigFile, encryptedData);
                
                Logging.Debug("RelayPortManager", "SavePortMappings",
                    $"Saved {_portMappings.Count} port mapping(s)");
            }
            catch (Exception ex)
            {
                Logging.Error("RelayPortManager", "SavePortMappings", ex.ToString());
            }
        }

        /// <summary>
        /// Generates a deterministic port number based on session ID
        /// Port range: 10000-65535 (avoiding well-known ports)
        /// </summary>
        private static int GenerateDeterministicPort(string sessionId)
        {
            // Use session ID hash to generate consistent port
            int hash = sessionId.GetHashCode();
            if (hash < 0) hash = -hash;
            
            // Map to port range 10000-65535 (55536 possible ports)
            int port = 10000 + (hash % 55536);
            
            return port;
        }

        /// <summary>
        /// Checks if a port is available
        /// </summary>
        private static bool IsPortAvailable(int port)
        {
            try
            {
                using var listener = new System.Net.Sockets.TcpListener(
                    System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Finds an available port, preferring the deterministic port for the session
        /// </summary>
        private static int FindAvailablePortForSession(string sessionId)
        {
            // Try the deterministic port first
            int preferredPort = GenerateDeterministicPort(sessionId);
            
            if (IsPortAvailable(preferredPort))
            {
                return preferredPort;
            }
            
            // If preferred port is taken, try nearby ports (±100 range)
            for (int offset = 1; offset <= 100; offset++)
            {
                int nextPort = preferredPort + offset;
                if (nextPort <= 65535 && IsPortAvailable(nextPort))
                {
                    return nextPort;
                }
                
                int prevPort = preferredPort - offset;
                if (prevPort >= 10000 && IsPortAvailable(prevPort))
                {
                    return prevPort;
                }
            }
            
            // Fallback: let OS assign a random port
            using var listener = new System.Net.Sockets.TcpListener(
                System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}


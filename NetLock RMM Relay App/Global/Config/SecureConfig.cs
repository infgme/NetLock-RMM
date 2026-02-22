using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Global.Helper;
using Global.Encryption;

namespace NetLock_RMM_Relay_App.Global.Config
{
    /// <summary>
    /// Secure configuration storage with encryption
    /// </summary>
    public class SecureConfig
    {
        private static readonly string ConfigPath = Path.Combine(
            Application_Paths.NetLockUserDir, 
            "config.enc");
        
        private static readonly string SessionStatePath = Path.Combine(
            Application_Paths.NetLockUserDir, 
            "session_states.enc");
        
        // Configuration data structure
        public class ConfigData
        {
            public string BackendUrl { get; set; } = ""; // HTTP API endpoint
            public string RelayUrl { get; set; } = ""; // TCP Relay endpoint (host:port)
            public string ApiKey { get; set; } = "";
            public string HardwareId { get; set; } = "";
            public string PasswordHash { get; set; } = "";
            public bool UsePasswordAuth { get; set; } = false;
            public bool IsLocked { get; set; } = false;
            public bool UseRelayTls { get; set; } = false; // Enable/disable TLS for relay connection
        }
        
        // Session state data
        public class SessionStateData
        {
            public Dictionary<string, SessionState> Sessions { get; set; } = new();
        }
        
        public class SessionState
        {
            public string SessionId { get; set; } = "";
            public bool AutoConnect { get; set; } = false;
            public int PreferredPort { get; set; } = 0;
            public DateTime LastConnected { get; set; }
        }
        
        /// <summary>
        /// Save encrypted configuration
        /// </summary>
        public static bool SaveConfig(ConfigData config, string password)
        {
            try
            {
                // Ensure directory exists
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);
                
                // Serialize to JSON
                string json = JsonSerializer.Serialize(config);
                
                // Encrypt with password
                string encrypted = String_Encryption.Encrypt(json, password);
                
                // Save to file
                File.WriteAllText(ConfigPath, encrypted);
                
                Logging.Debug("SecureConfig", "SaveConfig", "Configuration saved successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logging.Error("SecureConfig", "SaveConfig", ex.ToString());
                return false;
            }
        }
        
        /// <summary>
        /// Load and decrypt configuration
        /// </summary>
        public static (bool success, ConfigData? config, string error) LoadConfig(string password)
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    return (false, null, "No configuration found");
                }
                
                // Read encrypted file
                string encrypted = File.ReadAllText(ConfigPath);
                
                // Decrypt with password
                string decrypted = String_Encryption.Decrypt(encrypted, password);
                
                if (string.IsNullOrEmpty(decrypted))
                {
                    return (false, null, "Invalid password or corrupted configuration");
                }
                
                // Deserialize from JSON
                var config = JsonSerializer.Deserialize<ConfigData>(decrypted);
                
                if (config == null)
                {
                    return (false, null, "Failed to parse configuration");
                }
                
                Logging.Debug("SecureConfig", "LoadConfig", "Configuration loaded successfully");
                return (true, config, "");
            }
            catch (Exception ex)
            {
                Logging.Error("SecureConfig", "LoadConfig", ex.ToString());
                return (false, null, ex.Message);
            }
        }
        
        /// <summary>
        /// Check if configuration exists
        /// </summary>
        public static bool ConfigExists()
        {
            return File.Exists(ConfigPath);
        }
        
        /// <summary>
        /// Save session states
        /// </summary>
        public static bool SaveSessionStates(SessionStateData states, string password)
        {
            try
            {
                var dir = Path.GetDirectoryName(SessionStatePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);
                
                string json = JsonSerializer.Serialize(states);
                string encrypted = String_Encryption.Encrypt(json, password);
                File.WriteAllText(SessionStatePath, encrypted);
                
                Logging.Debug("SecureConfig", "SaveSessionStates", 
                    $"Saved {states.Sessions.Count} session states");
                return true;
            }
            catch (Exception ex)
            {
                Logging.Error("SecureConfig", "SaveSessionStates", ex.ToString());
                return false;
            }
        }
        
        /// <summary>
        /// Load session states
        /// </summary>
        public static (bool success, SessionStateData? states) LoadSessionStates(string password)
        {
            try
            {
                if (!File.Exists(SessionStatePath))
                {
                    return (true, new SessionStateData());
                }
                
                string encrypted = File.ReadAllText(SessionStatePath);
                string decrypted = String_Encryption.Decrypt(encrypted, password);
                
                if (string.IsNullOrEmpty(decrypted))
                {
                    return (false, null);
                }
                
                var states = JsonSerializer.Deserialize<SessionStateData>(decrypted);
                
                Logging.Debug("SecureConfig", "LoadSessionStates", 
                    $"Loaded {states?.Sessions.Count ?? 0} session states");
                
                return (true, states ?? new SessionStateData());
            }
            catch (Exception ex)
            {
                Logging.Error("SecureConfig", "LoadSessionStates", ex.ToString());
                return (false, null);
            }
        }
        
        /// <summary>
        /// Hash password for storage
        /// </summary>
        public static string HashPassword(string password)
        {
            using var sha256 = SHA256.Create();
            byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(bytes);
        }
        
        /// <summary>
        /// Verify password against hash
        /// </summary>
        public static bool VerifyPassword(string password, string hash)
        {
            string computed = HashPassword(password);
            return computed == hash;
        }
        
        /// <summary>
        /// Delete configuration
        /// </summary>
        public static void DeleteConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    File.Delete(ConfigPath);
                
                if (File.Exists(SessionStatePath))
                    File.Delete(SessionStatePath);
                
                Logging.Debug("SecureConfig", "DeleteConfig", "Configuration deleted");
            }
            catch (Exception ex)
            {
                Logging.Error("SecureConfig", "DeleteConfig", ex.ToString());
            }
        }
    }
}


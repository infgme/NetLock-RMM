using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Global.Helper;

namespace NetLock_RMM_Agent_Remote.Relay
{
    /// <summary>
    /// TOFU (Trust On First Use) Server Public Key Fingerprint Storage
    /// 
    /// Prevents MITM attacks through persistent storage of server fingerprints.
    /// On first contact, the fingerprint is stored and verified on every subsequent
    /// connection attempt. If it changes, the connection is rejected!
    /// 
    /// Security: Like SSH "known_hosts"
    /// </summary>
    public static class ServerTrustStore
    {
        private static readonly string _storePath = Application_Paths.relay_server_fingerprints_json;
        private static readonly object _lock = new object();

        /// <summary>
        /// Stored fingerprints: Key = Server URL, Value = Fingerprint
        /// </summary>
        private class FingerprintStore
        {
            public Dictionary<string, ServerFingerprint> Servers { get; set; } = new Dictionary<string, ServerFingerprint>();
        }

        public class ServerFingerprint
        {
            public string Fingerprint { get; set; } = string.Empty;
            public string PublicKey { get; set; } = string.Empty;
            public DateTime FirstSeen { get; set; }
            public DateTime LastVerified { get; set; }
            public int ConnectionCount { get; set; }
        }

        /// <summary>
        /// Verifies server fingerprint (TOFU)
        /// </summary>
        /// <param name="serverUrl">Server URL (e.g. "172.18.0.1:7443")</param>
        /// <param name="publicKey">Server public key (PEM)</param>
        /// <param name="fingerprint">Server fingerprint (SHA256)</param>
        /// <returns>True if trustworthy, False if MITM suspected</returns>
        public static bool VerifyServerFingerprint(string serverUrl, string publicKey, string fingerprint)
        {
            lock (_lock)
            {
                try
                {
                    // Load stored fingerprints
                    var store = LoadStore();

                    // Calculate fingerprint from public key (for validation)
                    string calculatedFingerprint = CalculateFingerprint(publicKey);
                    
                    // Check if calculated fingerprint matches transmitted one
                    if (!calculatedFingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[RELAY TOFU] WARNING: Fingerprint mismatch!");
                        Console.WriteLine($"[RELAY TOFU]   Calculated: {calculatedFingerprint}");
                        Console.WriteLine($"[RELAY TOFU]   Received:   {fingerprint}");
                        Logging.Debug("ServerTrustStore", "VerifyServerFingerprint", 
                            $"Fingerprint calculation mismatch for {serverUrl}");
                        return false;
                    }

                    // Is server already known?
                    if (store.Servers.TryGetValue(serverUrl, out var storedServer))
                    {
                        // TOFU check: Fingerprint must match!
                        if (!storedServer.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"[RELAY TOFU] SECURITY ALERT: Server fingerprint changed!");
                            Console.WriteLine($"[RELAY TOFU]   Server:     {serverUrl}");
                            Console.WriteLine($"[RELAY TOFU]   Expected:   {storedServer.Fingerprint}");
                            Console.WriteLine($"[RELAY TOFU]   Received:   {fingerprint}");
                            Console.WriteLine($"[RELAY TOFU]   First seen: {storedServer.FirstSeen}");
                            Console.WriteLine($"[RELAY TOFU]   Connections: {storedServer.ConnectionCount}");
                            Console.WriteLine($"[RELAY TOFU] POSSIBLE MITM ATTACK - CONNECTION REJECTED!");
                            
                            Logging.Error("ServerTrustStore", "VerifyServerFingerprint", 
                                $"MITM ALERT: Server {serverUrl} fingerprint changed from {storedServer.Fingerprint} to {fingerprint}");
                            
                            return false; // MITM suspected! Reject connection!
                        }

                        // Fingerprint matches - Update Last Verified
                        storedServer.LastVerified = DateTime.UtcNow;
                        storedServer.ConnectionCount++;
                        SaveStore(store);
                        
                        Console.WriteLine($"[RELAY TOFU] Server fingerprint verified (connection #{storedServer.ConnectionCount})");
                        return true;
                    }
                    else
                    {
                        // First contact - TOFU: Trust On First Use
                        Console.WriteLine($"[RELAY TOFU] First contact with server {serverUrl}");
                        Console.WriteLine($"[RELAY TOFU]   Fingerprint: {fingerprint}");
                        Console.WriteLine($"[RELAY TOFU]   Trusting and storing (TOFU)...");
                        
                        store.Servers[serverUrl] = new ServerFingerprint
                        {
                            Fingerprint = fingerprint,
                            PublicKey = publicKey,
                            FirstSeen = DateTime.UtcNow,
                            LastVerified = DateTime.UtcNow,
                            ConnectionCount = 1
                        };
                        
                        SaveStore(store);
                        
                        Console.WriteLine($"[RELAY TOFU] Server fingerprint stored for future verification");
                        Logging.Debug("ServerTrustStore", "VerifyServerFingerprint", 
                            $"First contact with {serverUrl} - fingerprint stored: {fingerprint}");
                        
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RELAY TOFU] Error verifying fingerprint: {ex.Message}");
                    Logging.Error("ServerTrustStore", "VerifyServerFingerprint", ex.ToString());
                    return false;
                }
            }
        }

        /// <summary>
        /// Calculates SHA256 fingerprint of a public key
        /// </summary>
        private static string CalculateFingerprint(string publicKeyPem)
        {
            using var sha256 = SHA256.Create();
            byte[] publicKeyBytes = Encoding.UTF8.GetBytes(publicKeyPem);
            byte[] hashBytes = sha256.ComputeHash(publicKeyBytes);
            
            return $"SHA256:{BitConverter.ToString(hashBytes).Replace("-", ":")}";
        }

        /// <summary>
        /// Loads stored fingerprints from encrypted JSON file
        /// Uses AES-256-CBC with PBKDF2 key derivation (Application_Settings.NetLock_Local_Encryption_Key)
        /// </summary>
        private static FingerprintStore LoadStore()
        {
            try
            {
                if (!File.Exists(_storePath))
                {
                    Console.WriteLine($"[RELAY TOFU] No fingerprint store found - creating new");
                    return new FingerprintStore();
                }

                // Read encrypted data
                string encryptedData = File.ReadAllText(_storePath);
                
                // Decrypt with local encryption key
                string decryptedJson = Global.Encryption.String_Encryption.Decrypt(
                    encryptedData, 
                    Application_Settings.NetLock_Local_Encryption_Key);
                
                var store = JsonSerializer.Deserialize<FingerprintStore>(decryptedJson);
                
                Console.WriteLine($"[RELAY TOFU] Loaded {store?.Servers.Count ?? 0} known servers (encrypted storage)");
                return store ?? new FingerprintStore();
            }
            catch (CryptographicException ex)
            {
                Console.WriteLine($"[RELAY TOFU] Warning: Failed to decrypt fingerprint store: {ex.Message}");
                Logging.Error("ServerTrustStore", "LoadStore", 
                    $"Decryption failed - file may be corrupted: {ex.Message}");
                return new FingerprintStore();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RELAY TOFU] Warning: Failed to load fingerprint store: {ex.Message}");
                Logging.Error("ServerTrustStore", "LoadStore", 
                    $"Failed to load store, creating new: {ex.Message}");
                return new FingerprintStore();
            }
        }

        /// <summary>
        /// Saves fingerprints encrypted as JSON
        /// Uses AES-256-CBC with PBKDF2 key derivation (Application_Settings.NetLock_Local_Encryption_Key)
        /// </summary>
        private static void SaveStore(FingerprintStore store)
        {
            try
            {
                // Create directory if it doesn't exist
                string? directory = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Serialize to JSON
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(store, options);
                
                // Encrypt with local encryption key
                string encryptedData = Global.Encryption.String_Encryption.Encrypt(
                    json, 
                    Application_Settings.NetLock_Local_Encryption_Key);
                
                // Write encrypted data
                File.WriteAllText(_storePath, encryptedData);
                
                Console.WriteLine($"[RELAY TOFU] Fingerprint store saved encrypted ({store.Servers.Count} servers)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RELAY TOFU] Warning: Failed to save fingerprint store: {ex.Message}");
                Logging.Error("ServerTrustStore", "SaveStore", ex.ToString());
            }
        }

        /// <summary>
        /// Removes stored fingerprint (for manual server key rotation)
        /// WARNING: Only use if server key was legitimately changed!
        /// </summary>
        public static bool RemoveServerFingerprint(string serverUrl)
        {
            lock (_lock)
            {
                try
                {
                    var store = LoadStore();
                    
                    if (store.Servers.Remove(serverUrl))
                    {
                        SaveStore(store);
                        Console.WriteLine($"[RELAY TOFU] Removed fingerprint for {serverUrl}");
                        Logging.Debug("ServerTrustStore", "RemoveServerFingerprint", 
                            $"Removed fingerprint for {serverUrl}");
                        return true;
                    }
                    
                    Console.WriteLine($"[RELAY TOFU] Server {serverUrl} not found in store");
                    return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RELAY TOFU] Error removing fingerprint: {ex.Message}");
                    Logging.Error("ServerTrustStore", "RemoveServerFingerprint", ex.ToString());
                    return false;
                }
            }
        }

        /// <summary>
        /// Returns all stored servers (for diagnostics)
        /// </summary>
        public static Dictionary<string, ServerFingerprint> GetAllServers()
        {
            lock (_lock)
            {
                var store = LoadStore();
                return new Dictionary<string, ServerFingerprint>(store.Servers);
            }
        }

        /// <summary>
        /// Deletes all stored fingerprints (for complete reset)
        /// WARNING: Only use in test environments!
        /// </summary>
        public static void ClearAllFingerprints()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_storePath))
                    {
                        File.Delete(_storePath);
                        Console.WriteLine($"[RELAY TOFU] All fingerprints cleared");
                        Logging.Debug("ServerTrustStore", "ClearAllFingerprints", 
                            "All server fingerprints cleared");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RELAY TOFU] Error clearing fingerprints: {ex.Message}");
                    Logging.Error("ServerTrustStore", "ClearAllFingerprints", ex.ToString());
                }
            }
        }
    }
}


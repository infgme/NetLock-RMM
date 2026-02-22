using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Global.Helper;
using Global.Encryption;

namespace NetLock_RMM_Relay_App.RelayClient
{
    /// <summary>
    /// Manages trusted server fingerprints (TOFU - Trust On First Use)
    /// Protects against MITM attacks by storing the first server public key
    /// </summary>
    public static class ServerTrustStore
    {
        private static readonly string TrustStoreFilePath = Path.Combine(
            Application_Paths.application_data_directory, 
            "server_trust_store.json");

        private class TrustStoreData
        {
            public Dictionary<string, ServerTrustEntry> Servers { get; set; } = new Dictionary<string, ServerTrustEntry>();
        }

        private class ServerTrustEntry
        {
            public string Fingerprint { get; set; } = string.Empty;
            public string PublicKey { get; set; } = string.Empty;
            public DateTime FirstSeen { get; set; }
            public DateTime LastVerified { get; set; }
            public bool TrustedByUser { get; set; } // Manually confirmed by user
        }

        /// <summary>
        /// Stores or verifies the server fingerprint (TOFU)
        /// </summary>
        /// <returns>
        /// (isNewServer, isValid, storedFingerprint)
        /// - isNewServer: true if server is seen for the first time
        /// - isValid: true if fingerprint matches or is new
        /// - storedFingerprint: The stored fingerprint (or null for new server)
        /// </returns>
        public static (bool isNewServer, bool isValid, string? storedFingerprint) VerifyOrStoreFingerprint(
            string serverUrl, 
            string publicKey, 
            string fingerprint)
        {
            try
            {
                var trustStore = LoadTrustStore();
                string serverKey = NormalizeServerUrl(serverUrl);

                if (trustStore.Servers.TryGetValue(serverKey, out var existingEntry))
                {
                    // Server is already known - verify fingerprint
                    bool isValid = existingEntry.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase);
                    
                    if (isValid)
                    {
                        // Update LastVerified
                        existingEntry.LastVerified = DateTime.UtcNow;
                        SaveTrustStore(trustStore);
                        
                        Logging.Debug("ServerTrustStore", "VerifyOrStoreFingerprint",
                            $"Server {serverUrl} verified successfully (TOFU)");
                        
                        return (false, true, existingEntry.Fingerprint);
                    }
                    else
                    {
                        // WARNING: Fingerprint has changed! Possible MITM attack!
                        Logging.Error("ServerTrustStore", "VerifyOrStoreFingerprint",
                            $"SERVER FINGERPRINT MISMATCH! Possible MITM attack!\n" +
                             $"Server: {serverUrl}\n" +
                             $"Expected: {existingEntry.Fingerprint}\n" +
                             $"Received: {fingerprint}");
                        
                        return (false, false, existingEntry.Fingerprint);
                    }
                }
                else
                {
                    // New server - store fingerprint (TOFU)
                    var newEntry = new ServerTrustEntry
                    {
                        Fingerprint = fingerprint,
                        PublicKey = publicKey,
                        FirstSeen = DateTime.UtcNow,
                        LastVerified = DateTime.UtcNow,
                        TrustedByUser = false // Set to true if user manually confirms
                    };
                    
                    trustStore.Servers[serverKey] = newEntry;
                    SaveTrustStore(trustStore);
                    
                    Logging.Info("ServerTrustStore", "VerifyOrStoreFingerprint",
                        $"New server {serverUrl} added to trust store (TOFU)\nFingerprint: {fingerprint}");
                    
                    return (true, true, null);
                }
            }
            catch (Exception ex)
            {
                Logging.Error("ServerTrustStore", "VerifyOrStoreFingerprint", ex.ToString());
                return (false, false, null);
            }
        }

        /// <summary>
        /// Marks a server as manually trusted by the user
        /// (e.g. after out-of-band verification of the fingerprint)
        /// </summary>
        public static bool SetServerTrustedByUser(string serverUrl, bool trusted = true)
        {
            try
            {
                var trustStore = LoadTrustStore();
                string serverKey = NormalizeServerUrl(serverUrl);

                if (trustStore.Servers.TryGetValue(serverKey, out var entry))
                {
                    entry.TrustedByUser = trusted;
                    SaveTrustStore(trustStore);
                    
                    Logging.Info("ServerTrustStore", "SetServerTrustedByUser",
                        $"Server {serverUrl} marked as {(trusted ? "trusted" : "not trusted")} by user");
                    
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Logging.Error("ServerTrustStore", "SetServerTrustedByUser", ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Updates the fingerprint of a server (e.g. after server certificate change)
        /// WARNING: This should only be done after careful out-of-band verification!
        /// </summary>
        public static bool UpdateServerFingerprint(string serverUrl, string newPublicKey, string newFingerprint)
        {
            try
            {
                var trustStore = LoadTrustStore();
                string serverKey = NormalizeServerUrl(serverUrl);

                var newEntry = new ServerTrustEntry
                {
                    Fingerprint = newFingerprint,
                    PublicKey = newPublicKey,
                    FirstSeen = DateTime.UtcNow, // Reset FirstSeen since new key
                    LastVerified = DateTime.UtcNow,
                    TrustedByUser = false // Must be confirmed again
                };
                
                trustStore.Servers[serverKey] = newEntry;
                SaveTrustStore(trustStore);
                
                Logging.Info("ServerTrustStore", "UpdateServerFingerprint",
                    $"Server {serverUrl} fingerprint updated\nNew fingerprint: {newFingerprint}");
                
                return true;
            }
            catch (Exception ex)
            {
                Logging.Error("ServerTrustStore", "UpdateServerFingerprint", ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Retrieves the stored public key of a server
        /// </summary>
        public static string? GetServerPublicKey(string serverUrl)
        {
            try
            {
                var trustStore = LoadTrustStore();
                string serverKey = NormalizeServerUrl(serverUrl);

                if (trustStore.Servers.TryGetValue(serverKey, out var entry))
                {
                    return entry.PublicKey;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logging.Error("ServerTrustStore", "GetServerPublicKey", ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Retrieves the stored fingerprint of a server
        /// </summary>
        public static string? GetServerFingerprint(string serverUrl)
        {
            try
            {
                var trustStore = LoadTrustStore();
                string serverKey = NormalizeServerUrl(serverUrl);

                if (trustStore.Servers.TryGetValue(serverKey, out var entry))
                {
                    return entry.Fingerprint;
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Logging.Error("ServerTrustStore", "GetServerFingerprint", ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Deletes all trusted servers (for reset)
        /// </summary>
        public static void ClearTrustStore()
        {
            try
            {
                if (File.Exists(TrustStoreFilePath))
                {
                    File.Delete(TrustStoreFilePath);
                    Logging.Info("ServerTrustStore", "ClearTrustStore", "Trust store cleared");
                }
            }
            catch (Exception ex)
            {
                Logging.Error("ServerTrustStore", "ClearTrustStore", ex.ToString());
            }
        }

        private static TrustStoreData LoadTrustStore()
        {
            try
            {
                if (File.Exists(TrustStoreFilePath))
                {
                    string fileContent = File.ReadAllText(TrustStoreFilePath);
                    
                    // Check if file is empty
                    if (string.IsNullOrWhiteSpace(fileContent))
                    {
                        Logging.Debug("ServerTrustStore", "LoadTrustStore", 
                            "Trust store file is empty, returning new data");
                        return new TrustStoreData();
                    }
                    
                    // Get user password from Application_Settings
                    string? userPassword = Application_Settings.Handler.GetEncryptionPassword();
                    
                    if (string.IsNullOrEmpty(userPassword))
                    {
                        Logging.Error("ServerTrustStore", "LoadTrustStore", 
                            "Cannot load trust store: User password not set");
                        return new TrustStoreData();
                    }
                    
                    try
                    {
                        // Try to decrypt the JSON
                        string json = String_Encryption.Decrypt(fileContent, userPassword);
                        
                        // Check if decrypted content is valid JSON
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            Logging.Info("ServerTrustStore", "LoadTrustStore", 
                                "Decrypted content is empty, returning new data");
                            return new TrustStoreData();
                        }
                        
                        var data = JsonSerializer.Deserialize<TrustStoreData>(json);
                        return data ?? new TrustStoreData();
                    }
                    catch (Exception decryptEx)
                    {
                        Logging.Error("ServerTrustStore", "LoadTrustStore", 
                            $"Failed to decrypt trust store (possibly corrupted or wrong password): {decryptEx.Message}");
                        
                        // Return empty data - file will be re-encrypted on next save
                        return new TrustStoreData();
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("ServerTrustStore", "LoadTrustStore", ex.ToString());
            }
            
            return new TrustStoreData();
        }

        private static void SaveTrustStore(TrustStoreData data)
        {
            try
            {
                // Get user password from Application_Settings
                string? userPassword = Application_Settings.Handler.GetEncryptionPassword();
                
                if (string.IsNullOrEmpty(userPassword))
                {
                    Logging.Error("ServerTrustStore", "SaveTrustStore", 
                        "Cannot save trust store: User password not set - trust store will not be persisted!");
                    // Don't throw - just log and return. This allows the app to continue
                    // but the trust store won't be persisted until password is set
                    return;
                }

                // Create directory if not present
                string? directory = Path.GetDirectoryName(TrustStoreFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Serialize to JSON
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                // Encrypt and save
                string encryptedJson = String_Encryption.Encrypt(json, userPassword);
                File.WriteAllText(TrustStoreFilePath, encryptedJson);
                
                Logging.Debug("ServerTrustStore", "SaveTrustStore", 
                    $"Trust store saved successfully (encrypted, {data.Servers.Count} servers)");
            }
            catch (Exception ex)
            {
                Logging.Error("ServerTrustStore", "SaveTrustStore", ex.ToString());
                // Don't re-throw - allow app to continue even if save fails
            }
        }

        private static string NormalizeServerUrl(string serverUrl)
        {
            // Remove http://, https://, trailing slashes
            return serverUrl
                .Replace("https://", "")
                .Replace("http://", "")
                .TrimEnd('/');
        }
    }
}

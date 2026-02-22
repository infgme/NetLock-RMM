using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Global.Encryption;
using Global.Helper;

namespace NetLock_RMM_Relay_App;

public class Application_Settings
{
    public static string version = "1.0.0";
    
    public static class Handler
    {
        private static readonly string SettingsFile = Path.Combine(
            Application_Paths.application_data_directory, 
            "settings.json"
        );
        
        private static Dictionary<string, string> _settings = new Dictionary<string, string>();
        
        // User's password for encryption (set after login)
        private static string? _userPassword = null;
        
        // Keys that should be encrypted in storage
        private static readonly HashSet<string> EncryptedKeys = new HashSet<string>
        {
            "relay_api_key",
            "relay_hardware_id"
        };
        
        /// <summary>
        /// Set the encryption password (must be called after login/setup)
        /// </summary>
        public static void SetEncryptionPassword(string password)
        {
            _userPassword = password;
            
            // Reload settings with the new password
            LoadSettings();
            
            Logging.Debug("Application_Settings", "SetEncryptionPassword", 
                "Encryption password set, settings reloaded");
        }
        
        /// <summary>
        /// Get the encryption password (for use by other components like ServerTrustStore)
        /// </summary>
        public static string? GetEncryptionPassword()
        {
            return _userPassword;
        }
        
        static Handler()
        {
            LoadSettings();
        }
        
        private static void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    var loadedSettings = JsonSerializer.Deserialize<Dictionary<string, string>>(json) 
                                ?? new Dictionary<string, string>();
                    
                    // Decrypt encrypted values when loading
                    _settings = new Dictionary<string, string>();
                    foreach (var kvp in loadedSettings)
                    {
                        if (EncryptedKeys.Contains(kvp.Key) && !string.IsNullOrEmpty(_userPassword))
                        {
                            try
                            {
                                // Decrypt the value with user's password
                                _settings[kvp.Key] = String_Encryption.Decrypt(kvp.Value, _userPassword);
                            }
                            catch
                            {
                                // If decryption fails, store as-is (might be unencrypted legacy value or wrong password)
                                _settings[kvp.Key] = kvp.Value;
                            }
                        }
                        else
                        {
                            // Store plaintext if no password is set yet or key is not encrypted
                            _settings[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
            catch
            {
                _settings = new Dictionary<string, string>();
            }
        }
        
        private static void SaveSettings()
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsFile);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                // Encrypt sensitive values before saving
                var settingsToSave = new Dictionary<string, string>();
                foreach (var kvp in _settings)
                {
                    if (EncryptedKeys.Contains(kvp.Key) && !string.IsNullOrEmpty(_userPassword))
                    {
                        try
                        {
                            // Encrypt the value with user's password
                            settingsToSave[kvp.Key] = String_Encryption.Encrypt(kvp.Value, _userPassword);
                        }
                        catch
                        {
                            // If encryption fails, skip this value (don't save plaintext)
                            continue;
                        }
                    }
                    else
                    {
                        // Save plaintext if no password is set yet or key is not encrypted
                        settingsToSave[kvp.Key] = kvp.Value;
                    }
                }
                
                var json = JsonSerializer.Serialize(settingsToSave, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                File.WriteAllText(SettingsFile, json);
            }
            catch (Exception ex)
            {
                Logging.Error("Application_Settings.Handler", "SaveSettings", ex.ToString());
            }
        }
        
        public static string? Get_Value(string key)
        {
            return _settings.ContainsKey(key) ? _settings[key] : null;
        }
        
        public static void Set_Value(string key, string value)
        {
            _settings[key] = value;
            SaveSettings();
        }
    }
}


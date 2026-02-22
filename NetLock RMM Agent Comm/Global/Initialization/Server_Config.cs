using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using System.Security.Principal;
using Global.Helper;
using Global.Encryption;
using NetLock_RMM_Agent_Comm;
using System.ComponentModel;
using System.Runtime.Intrinsics.Wasm;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Global.Initialization
{
    internal class Server_Config
    {
        // Cache for the decrypted JSON to avoid repeated decryption
        private static string _cachedDecryptedJson = null;
        private static DateTime _cacheTimestamp = DateTime.MinValue;
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Loads and decrypts the server config JSON. If the file is unencrypted, it will be converted and encrypted.
        /// </summary>
        private static string LoadAndDecryptConfig()
        {
            try
            {
                // Check cache validity
                if (_cachedDecryptedJson != null && (DateTime.Now - _cacheTimestamp) < CacheExpiration)
                {
                    return _cachedDecryptedJson;
                }

                // Check if file exists
                if (!File.Exists(Application_Paths.program_data_server_config_json))
                {
                    Logging.Error("Server_Config", "LoadAndDecryptConfig", "Server config file does not exist.");
                    return null;
                }

                string fileContent = File.ReadAllText(Application_Paths.program_data_server_config_json);
                
                // Check if the content is already encrypted (Base64 string without JSON structure)
                bool isEncrypted = !fileContent.TrimStart().StartsWith("{");
                
                if (isEncrypted)
                {
                    // File is encrypted, decrypt it
                    Logging.Debug("Server_Config", "LoadAndDecryptConfig", "Config file is encrypted. Decrypting...");
                    string decryptedJson = String_Encryption.Decrypt(fileContent, Application_Settings.NetLock_Local_Encryption_Key);
                    
                    // Update cache
                    _cachedDecryptedJson = decryptedJson;
                    _cacheTimestamp = DateTime.Now;
                    
                    return decryptedJson;
                }
                else
                {
                    // File is unencrypted (legacy format), convert it
                    Logging.Debug("Server_Config", "LoadAndDecryptConfig", "Config file is unencrypted. Converting to encrypted format...");
                    
                    // Validate that it's valid JSON
                    using (JsonDocument document = JsonDocument.Parse(fileContent))
                    {
                        // JSON is valid, encrypt and save it
                        string encryptedContent = String_Encryption.Encrypt(fileContent, Application_Settings.NetLock_Local_Encryption_Key);
                        File.WriteAllText(Application_Paths.program_data_server_config_json, encryptedContent);
                        
                        Logging.Debug("Server_Config", "LoadAndDecryptConfig", "Config file successfully encrypted and saved.");
                        
                        // Update cache
                        _cachedDecryptedJson = fileContent;
                        _cacheTimestamp = DateTime.Now;
                        
                        return fileContent;
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config", "LoadAndDecryptConfig", ex.ToString());
                return null;
            }
        }

        /// <summary>
        /// Saves the server config JSON in encrypted format.
        /// </summary>
        public static bool SaveEncryptedConfig(string jsonContent)
        {
            try
            {
                string encryptedContent = String_Encryption.Encrypt(jsonContent, Application_Settings.NetLock_Local_Encryption_Key);
                File.WriteAllText(Application_Paths.program_data_server_config_json, encryptedContent);
                
                // Update cache
                _cachedDecryptedJson = jsonContent;
                _cacheTimestamp = DateTime.Now;
                
                Logging.Debug("Server_Config", "SaveEncryptedConfig", "Config file successfully encrypted and saved.");
                return true;
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config", "SaveEncryptedConfig", ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Invalidates the cache, forcing a reload on next access.
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedDecryptedJson = null;
            _cacheTimestamp = DateTime.MinValue;
        }

        /// <summary>
        /// Saves the Health Agent server config JSON in encrypted format.
        /// </summary>
        public static bool SaveEncryptedHealthAgentConfig(string jsonContent)
        {
            try
            {
                //string encryptedContent = String_Encryption.Encrypt(jsonContent, Application_Settings.NetLock_Local_Encryption_Key);
                //File.WriteAllText(Application_Paths.program_data_health_agent_server_config, encryptedContent);
                
                // Older installations are not able to read the encrypted config. Thats why we still keep it unencrypted for now and will provide a upgrade script for the health agent later.
                File.WriteAllText(Application_Paths.program_data_health_agent_server_config, jsonContent);
                
                Logging.Debug("Server_Config", "SaveEncryptedHealthAgentConfig", "Health Agent config file successfully encrypted and saved.");
                return true;
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config", "SaveEncryptedHealthAgentConfig", ex.ToString());
                return false;
            }
        }
        
        public static string Ssl()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "Ssl", "Failed to load server config.");
                    return false.ToString();
                }

                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("ssl");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (ssl)", element.GetBoolean().ToString());

                    // if ssl http_https
                    if (element.GetBoolean())
                    {
                        return true.ToString();
                    }
                    else
                    {
                        return false.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (ssl)", ex.ToString());
                return false.ToString();
            }
        }

        public static string Package_Guid()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "Package_Guid", "Failed to load server config.");
                    return false.ToString();
                }

                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("package_guid");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (package_guid)", element.ToString());

                    return element.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (ssl)", ex.ToString());
                return false.ToString();
            }
        }

        public static string Communication_Servers()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "Communication_Servers", "Failed to load server config.");
                    return "error";
                }
                
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (server_config_json)", server_config_json);
                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("communication_servers");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (communication_servers)", element.ToString());
                    return element.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (communication_servers)", ex.ToString());
                return "error";
            }
        }

        public static string Remote_Servers()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "Remote_Servers", "Failed to load server config.");
                    return "error";
                }
                
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (server_config_json)", server_config_json);
                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("remote_servers");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (remote_servers)", element.ToString());
                    return element.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (remote_servers)", ex.ToString());
                return "error";
            }
        }

        public static string Update_Servers()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "Update_Servers", "Failed to load server config.");
                    return "error";
                }
                
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (server_config_json)", server_config_json);
                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("update_servers");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (update_servers)", element.ToString());
                    return element.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (update_servers)", ex.ToString());
                return "error";
            }
        }

        public static string Trust_Servers()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "Trust_Servers", "Failed to load server config.");
                    return "error";
                }
                
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (server_config_json)", server_config_json);
                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("trust_servers");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (trust_servers)", element.ToString());
                    return element.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (trust_servers)", ex.ToString());
                return "error";
            }
        }

        public static string File_Servers()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "File_Servers", "Failed to load server config.");
                    return "error";
                }
                
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (server_config_json)", server_config_json);
                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("file_servers");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (file_servers)", element.ToString());
                    return element.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (file_servers)", ex.ToString());
                return "error";
            }
        }
        
        public static string Relay_Servers()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "", "Failed to load server config.");
                    return "error";
                }
                
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (server_config_json)", server_config_json);
                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("relay_servers");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (relay_servers)", element.ToString());
                    return element.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (relay_servers)", ex.ToString());
                return "error";
            }
        }

        public static string Tenant_Guid()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "Tenant_Guid", "Failed to load server config.");
                    return "error";
                }
                
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (server_config_json)", server_config_json);
                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("tenant_guid");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (tenant_guid)", element.ToString());
                    return element.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (tenant_guid)", ex.ToString());
                return "error";
            }
        }

        public static string Location_Guid()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "Location_Guid", "Failed to load server config.");
                    return "error";
                }
                
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (server_config_json)", server_config_json);
                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("location_guid");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (location_guid)", element.ToString());
                    return element.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (location_guid)", ex.ToString());
                return "error";
            }
        }

        public static string Language()
        {
            try
            {
                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "Language", "Failed to load server config.");
                    return "error";
                }
                
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (server_config_json)", server_config_json);
                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("language");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (language)", element.ToString());
                    return element.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Server_Config_Handler.Load (language)", ex.ToString());
                return "error";
            }
        }

        public static bool Authorized()
        {
            try
            {
                string serverConfigJson = LoadAndDecryptConfig();
                
                if (serverConfigJson == null)
                {
                    Logging.Error("Server_Config_Handler", "Authorized", "Failed to load server config.");
                    return false;
                }
                
                Logging.Debug("Server_Config_Handler", "Authorized", serverConfigJson);

                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(serverConfigJson))
                {
                    if (document.RootElement.TryGetProperty("authorized", out JsonElement element))
                    {
                        bool isAuthorized = element.GetBoolean();
                        Logging.Debug("Server_Config_Handler", "Authorized (value)", isAuthorized.ToString());
                        return isAuthorized;
                    }
                    else
                    {
                        Logging.Error("Server_Config_Handler", "Authorized", "Key 'authorized' not found in JSON.");
                        return false; // Default to false if key is missing
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Server_Config_Handler", "Authorized", ex.ToString());
                return false; // Default to false if an error occurs
            }
        }


        public static string Access_Key()
        {
            try
            {
                string access_key = String.Empty;

                string server_config_json = LoadAndDecryptConfig();
                
                if (server_config_json == null)
                {
                    Logging.Error("Server_Config_Handler", "Access_Key", "Failed to load server config.");
                    return String.Empty;
                }
                
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (server_config_json)", server_config_json);
                // Parse the JSON
                using (JsonDocument document = JsonDocument.Parse(server_config_json))
                {
                    JsonElement element = document.RootElement.GetProperty("access_key");
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load (access_key)", element.ToString());
                    access_key = element.ToString();
                }
                
                // Check if the access key is valid
                if (String.IsNullOrEmpty(access_key))
                {
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load", "Access key is empty");

                    // Generate a new access key
                    access_key = Guid.NewGuid().ToString();

                    // Write the new access key to the server config file
                    // Create the JSON object
                    var jsonObject = new
                    {
                        ssl = Configuration.Agent.ssl,
                        package_guid = Configuration.Agent.package_guid,
                        communication_servers = Configuration.Agent.communication_servers,
                        remote_servers = Configuration.Agent.remote_servers,
                        update_servers = Configuration.Agent.update_servers,
                        trust_servers = Configuration.Agent.trust_servers,
                        file_servers = Configuration.Agent.file_servers,
                        relay_servers = Configuration.Agent.relay_servers,
                        tenant_guid = Configuration.Agent.tenant_guid,
                        location_guid = Configuration.Agent.location_guid,
                        language = Configuration.Agent.language,
                        access_key = access_key,
                        authorized = false,
                    };

                    // Convert the object into a JSON string
                    string json = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
                    Logging.Debug("Online_Mode.Handler.Update_Device_Information", "json", json);

                    // Write the new server config JSON to the file (encrypted)
                    SaveEncryptedConfig(json);
                }
                else
                {
                    Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load", "Access key is not empty");
                }

                return access_key;
            }
            catch (Exception ex)
            {
                Logging.Debug("Server_Config_Handler", "Server_Config_Handler.Load", ex.ToString());
                return String.Empty;
            }
        }
    }
}

using MySqlConnector;
using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using System;
using Microsoft.AspNetCore.Mvc.Formatters;
using System.Diagnostics;
using System.Reflection;

namespace NetLock_RMM_Server.Agent.Windows
{
    public class Version_Handler
    {
        public class Device_Identity_Entity
        {
            public string? agent_version { get; set; }
            public string? device_name { get; set; }
            public string? location_guid{ get; set; }
            public string? tenant_guid { get; set; }
            public string? access_key { get; set; }
            public string? hwid { get; set; }
            public string? platform { get; set; } = "Windows"; // Default because of version 2.0.0.0 and below. Needs to be removed in version 3.x and above
            public string? ip_address_internal { get; set; }
            public string? operating_system { get; set; }
            public string? domain { get; set; }
            public string? antivirus_solution { get; set; }
            public string? firewall_status { get; set; }
            public string? architecture { get; set; }
            public string? last_boot { get; set; }
            public string? timezone { get; set; }
            public string? cpu { get; set; }
            public string? cpu_usage { get; set; }
            public string? mainboard { get; set; }
            public string? gpu { get; set; }
            public string? ram { get; set; }
            public string? ram_usage { get; set; }
            public string? tpm { get; set; }
            public string? environment_variables { get; set; }
            public string? last_active_user { get; set; }
        }

        public class Root_Entity
        {
            public Device_Identity_Entity? device_identity { get; set; }
        }

        private static List<string> noAutoUpdateVersions = new List<string>
        {
            "2.5.0.6",
            "2.5.0.7",
            "2.5.1.6",
            "2.5.1.7",
            "2.5.1.8",
            "2.5.1.9",
            "2.5.2.0",
        };

        public static async Task<string> Check_Version(string json)
        {
            try
            {
                // Extract JSON
                Root_Entity rootData = JsonSerializer.Deserialize<Root_Entity>(json);
                Device_Identity_Entity device_identity = rootData.device_identity;

                // Log the communicated agent version
                string agent_version = device_identity.agent_version;
                Logging.Handler.Debug("Agent.Windows.Version_Handler.Check_Version", "Communicated agent version", agent_version);

                // Check version against the server's agent version
                if (agent_version == Configuration.Server.agent_version)
                {
                    return "identical"; // identical 
                }
                else // Check if update is needed
                {
                    // Check if the package on server exists
                    if (!File.Exists(Application_Paths.internal_packages_netlock_core_metadata_json_path))
                    {
                        Logging.Handler.Debug("Agent.Windows.Version_Handler.Check_Version", "Update package not found", "Something wrong?");
                        return "identical"; // Suppress updates if the package does not exist
                    }

                    // Check if currently more than X devices are waiting for an update
                    int maxPendingUpdates = Convert.ToInt32(await MySQL.Handler.Quick_Reader("SELECT * FROM settings;", "agent_updates_max_concurrent_updates"));
                    Logging.Handler.Debug("Agent.Windows.Version_Handler.Check_Version", "agent_updates_max_concurrent_updates", maxPendingUpdates.ToString());

                    // Count the number of devices that are currently waiting for an update
                    int pendingUpdatesCount = 0;
                    pendingUpdatesCount = Convert.ToInt32(await MySQL.Handler.Quick_Reader("SELECT COUNT(*) FROM devices WHERE update_pending = 1;", "COUNT(*)"));

                    // Check if the number of pending updates is less than the maximum allowed
                    if (pendingUpdatesCount >= maxPendingUpdates)
                    {
                        Logging.Handler.Debug("Agent.Windows.Version_Handler.Check_Version", "Pending updates count exceeds maximum allowed", $"Pending updates: {pendingUpdatesCount}, Max allowed: {maxPendingUpdates}");
                        return "identical"; // Return identical if the number of pending updates exceeds the maximum allowed to prevent flooding the update process
                    }

                    MySqlConnection conn = new MySqlConnection(Configuration.MySQL.Connection_String);

                    string query = "UPDATE devices SET update_pending = 1, update_started = @update_started WHERE access_key = @access_key;";

                    try
                    {
                        await conn.OpenAsync();

                        MySqlCommand cmd = new MySqlCommand(query, conn);
                        cmd.Parameters.AddWithValue("@access_key", device_identity.access_key);
                        cmd.Parameters.AddWithValue("@update_started", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        
                        Logging.Handler.Debug("Agent.Windows.Version_Handler", "MySQL_Prepared_Query", query);

                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        Logging.Handler.Error("Agent.Windows.Version_Handler", "MySQL_Query", ex.ToString());
                    }
                    finally
                    {
                        await conn.CloseAsync();
                    }

                    return "different";
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("Agent.Windows.Version_Handler.Check_Version", "General error", ex.ToString());
                return "Invalid request.";
            }
        }
    }
}
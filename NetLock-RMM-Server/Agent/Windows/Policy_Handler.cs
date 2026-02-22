﻿using MySqlConnector;
using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using System;
using System.Xml.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Text.Json;
using System.Security;

namespace NetLock_RMM_Server.Agent.Windows
{
    public class Policy_Handler
    {
        public class Device_Identity_Entity
        {
            public string? agent_version { get; set; }
            public string? device_name { get; set; }
            public string? location_guid { get; set; }
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

        public class Automation_Entity
        {
            public string? name { get; set; }
            public string? date { get; set; }
            public string? author { get; set; }
            public string? description { get; set; }
            public int? category { get; set; }
            public int? sub_category { get; set; }
            public int? condition { get; set; }
            public string? expected_result { get; set; }
            public string? trigger { get; set; }
        }

        public class Sensors_Entity
        {
            public string? id { get; set; }
            public string? name { get; set; }
            public string? date { get; set; }
            public string? last_run { get; set; }
            public string? author { get; set; }
            public string? description { get; set; }
            public string? platform { get; set; }
            public int? severity { get; set; }
            public int? category { get; set; }
            public int? sub_category { get; set; }
            public int? utilization_category { get; set; }
            public int? notification_treshold_count { get; set; }
            public int? notification_treshold_max { get; set; }
            public string? notification_history { get; set; }
            public int? action_treshold_count { get; set; }
            public int? action_treshold_max { get; set; }
            public string? action_history { get; set; }
            public bool? auto_reset { get; set; }
            public string? script { get; set; }
            public string? script_action { get; set; }
            public int? cpu_usage { get; set; }
            public string? process_name { get; set; }
            public int? ram_usage { get; set; }
            public int? disk_usage { get; set; }
            public int? disk_minimum_capacity { get; set; }
            public int? disk_category { get; set; }
            public string? disk_letters { get; set; }
            public bool? disk_include_network_disks { get; set; }
            public bool? disk_include_removable_disks { get; set; }
            public string? eventlog { get; set; }
            public string? eventlog_event_id { get; set; }
            public string? expected_result { get; set; }

            //service sensor
            public string? service_name { get; set; }
            public int? service_condition { get; set; }
            public int? service_action { get; set; }

            //ping sensor
            public string? ping_address { get; set; }
            public int? ping_timeout { get; set; }
            public int? ping_condition { get; set; }

            //time schedule
            public int? time_scheduler_type { get; set; }
            public int? time_scheduler_seconds { get; set; }
            public int? time_scheduler_minutes { get; set; }
            public int? time_scheduler_hours { get; set; }
            public string? time_scheduler_time { get; set; }
            public string? time_scheduler_date { get; set; }
            public bool? time_scheduler_monday { get; set; }
            public bool? time_scheduler_tuesday { get; set; }
            public bool? time_scheduler_wednesday { get; set; }
            public bool? time_scheduler_thursday { get; set; }
            public bool? time_scheduler_friday { get; set; }
            public bool? time_scheduler_saturday { get; set; }
            public bool? time_scheduler_sunday { get; set; }

            // NetLock notifications
            public bool? suppress_notification { get; set; }
            public bool? resolved_notification { get; set; }
            public bool? notifications_mail { get; set; }
            public bool? notifications_microsoft_teams { get; set; }
            public bool? notifications_telegram { get; set; }
            public bool? notifications_ntfy_sh { get; set; }
            public bool? notifications_webhook { get; set; }
        }

        public class Sensors_Device_Entity
        {
            public string? id { get; set; }
        }

        public class Jobs_Entity
        {
            public string? id { get; set; }
            public string? name { get; set; }
            public string? date { get; set; }
            public string? author { get; set; }
            public string? description { get; set; }
            public string? platform { get; set; }
            public string? type { get; set; }
            public string? script { get; set; }
            public int? timeout { get; set; }

            public int? time_scheduler_type { get; set; }
            public int? time_scheduler_seconds { get; set; }
            public int? time_scheduler_minutes { get; set; }
            public int? time_scheduler_hours { get; set; }
            public string? time_scheduler_time { get; set; }
            public string? time_scheduler_date { get; set; }
            public bool? time_scheduler_monday { get; set; }
            public bool? time_scheduler_tuesday { get; set; }
            public bool? time_scheduler_wednesday { get; set; }
            public bool? time_scheduler_thursday { get; set; }
            public bool? time_scheduler_friday { get; set; }
            public bool? time_scheduler_saturday { get; set; }
            public bool? time_scheduler_sunday { get; set; }
        }

        public class Jobs_Device_Entity
        {
            public string? id { get; set; }
        }

        public static async Task<string> Get_Policy(string json, string external_ip_address)
        {
            try
            {
                // Extract JSON
                Root_Entity rootData = JsonConvert.DeserializeObject<Root_Entity>(json);
                Device_Identity_Entity device_identity = rootData.device_identity;

                string device_name = device_identity.device_name?.Trim() ?? string.Empty;
                string group_name = string.Empty;
                int tenant_id = 0;
                int location_id = 0;
                
                // Policy information
                string policy_name = null;
                string antivirus_settings_json = string.Empty;
                string antivirus_exclusions_json = string.Empty;
                string antivirus_scan_jobs_json = string.Empty;
                string antivirus_controlled_folder_access_folders_json = string.Empty;
                string antivirus_controlled_folder_access_ruleset_json = string.Empty;
                string sensors_json = string.Empty;
                string jobs_json = string.Empty;
                string tray_icon_settings_json = string.Empty;
                string agent_settings_json = string.Empty;

                MySqlConnection conn = new MySqlConnection(Configuration.MySQL.Connection_String);
                await conn.OpenAsync();

                // Get tenant_id, location_id, group_id and names from devices table using access_key
                int group_id = 0;
                string tenant_name = string.Empty;
                string location_name = string.Empty;
                
                try
                {
                    string query = @"SELECT d.tenant_id, d.location_id, d.group_id, t.name as tenant_name, l.name as location_name, g.name as group_name 
                                    FROM devices d 
                                    LEFT JOIN tenants t ON d.tenant_id = t.id 
                                    LEFT JOIN locations l ON d.location_id = l.id 
                                    LEFT JOIN `groups` g ON d.group_id = g.id 
                                    WHERE d.access_key = @access_key;";

                    MySqlCommand command = new MySqlCommand(query, conn);
                    command.Parameters.AddWithValue("@access_key", device_identity.access_key);

                    Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy (device_info)", "MySQL_Prepared_Query", query);

                    using (DbDataReader reader = await command.ExecuteReaderAsync())
                    {
                        if (reader.HasRows)
                        {
                            while (await reader.ReadAsync())
                            {
                                tenant_id = Convert.ToInt32(reader["tenant_id"]);
                                location_id = Convert.ToInt32(reader["location_id"]);
                                group_id = reader["group_id"] != DBNull.Value ? Convert.ToInt32(reader["group_id"]) : 0;
                                tenant_name = (reader["tenant_name"] != DBNull.Value ? reader["tenant_name"].ToString() : string.Empty)?.Trim() ?? string.Empty;
                                location_name = (reader["location_name"] != DBNull.Value ? reader["location_name"].ToString() : string.Empty)?.Trim() ?? string.Empty;
                                group_name = (reader["group_name"] != DBNull.Value ? reader["group_name"].ToString() : string.Empty)?.Trim() ?? string.Empty;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Error("Agent.Windows.Policy_Handler.Get_Policy", "MySQL_Query", ex.ToString());
                }
                finally
                {
                    //conn.Close();
                }

                // Log the communicated agent information
                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Device Details", 
                    $"Device Name: {device_name}, Tenant Name: {tenant_name}, Location Name: {location_name}, Group Name: {group_name}, Internal IP: {device_identity.ip_address_internal}, External IP: {external_ip_address}, Domain: {device_identity.domain}");
                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "tenant_id", tenant_id.ToString());
                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "location_id", location_id.ToString());
                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "group_id", group_id.ToString());

                // Get automations from database
                List<Automation_Entity> automations_list = new List<Automation_Entity>();
                
                try
                {
                    string query = "SELECT * FROM automations;";

                    MySqlCommand command = new MySqlCommand(query, conn);
                    
                    Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy (automations)", "MySQL_Prepared_Query", query);

                    using (DbDataReader reader = await command.ExecuteReaderAsync())
                    {
                        if (reader.HasRows)
                        {
                            while (await reader.ReadAsync())
                            {
                                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "automation_json", json);

                                Automation_Entity automation = JsonConvert.DeserializeObject<Automation_Entity>(reader["json"].ToString());
                                automations_list.Add(automation);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Error("Agent.Windows.Policy_Handler.Get_Policy", "MySQL_Query", ex.ToString());
                }
                finally
                {
                    //conn.Close();
                }

                // Filter automations, detect which automation applies to the device and return the policy
                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Automation Count", $"Total automations: {automations_list.Count}");
                
                // Device Name
                foreach (var automation in automations_list)
                {
                    Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Checking Automation", 
                        $"Name: {automation.name}, Category: {automation.category}, Condition: {automation.condition}, Expected: '{automation.expected_result}', Device Name: '{device_name}'");
                    
                    if (automation.category == 0 && automation.condition == 0 && automation.expected_result?.Trim() == device_name)
                    {
                        policy_name = automation.trigger;
                        Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Match Found", $"Device Name matched: {policy_name}");
                        break;
                    }
                }

                // Tenant (compare with tenant_name)
                if (policy_name == null)
                {
                    foreach (var automation in automations_list)
                    {
                        if (automation.category == 0 && automation.condition == 1)
                        {
                            Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Checking Tenant", 
                                $"Expected: '{automation.expected_result}', Actual: '{tenant_name}'");
                            
                            if (automation.expected_result?.Trim() == tenant_name)
                            {
                                policy_name = automation.trigger;
                                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Match Found", $"Tenant matched: {policy_name}");
                                break;
                            }
                        }
                    }
                }

                // Location (compare with location_name)
                if (policy_name == null)
                {
                    foreach (var automation in automations_list)
                    {
                        if (automation.category == 0 && automation.condition == 2)
                        {
                            Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Checking Location", 
                                $"Expected: '{automation.expected_result}', Actual: '{location_name}'");
                            
                            if (automation.expected_result?.Trim() == location_name)
                            {
                                policy_name = automation.trigger;
                                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Match Found", $"Location matched: {policy_name}");
                                break;
                            }
                        }
                    }
                }

                // Group
                if (policy_name == null)
                {
                    foreach (var automation in automations_list)
                    {
                        if (automation.category == 0 && automation.condition == 3)
                        {
                            Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Checking Group", 
                                $"Expected: '{automation.expected_result}', Actual: '{group_name}'");
                            
                            if (automation.expected_result?.Trim() == group_name)
                            {
                                policy_name = automation.trigger;
                                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Match Found", $"Group matched: {policy_name}");
                                break;
                            }
                        }
                    }
                }

                // Internal IP
                if (policy_name == null)
                {
                    foreach (var automation in automations_list)
                    {
                        if (automation.category == 0 && automation.condition == 4)
                        {
                            Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Checking Internal IP", 
                                $"Expected: '{automation.expected_result}', Actual: '{device_identity.ip_address_internal}'");
                            
                            if (automation.expected_result?.Trim() == device_identity.ip_address_internal?.Trim())
                            {
                                policy_name = automation.trigger;
                                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Match Found", $"Internal IP matched: {policy_name}");
                                break;
                            }
                        }
                    }
                }

                // External IP
                if (policy_name == null)
                {
                    foreach (var automation in automations_list)
                    {
                        if (automation.category == 0 && automation.condition == 5)
                        {
                            Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Checking External IP", 
                                $"Expected: '{automation.expected_result}', Actual: '{external_ip_address}'");
                            
                            if (automation.expected_result?.Trim() == external_ip_address?.Trim())
                            {
                                policy_name = automation.trigger;
                                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Match Found", $"External IP matched: {policy_name}");
                                break;
                            }
                        }
                    }
                }

                // Domain
                if (policy_name == null)
                {
                    foreach (var automation in automations_list)
                    {
                        if (automation.category == 0 && automation.condition == 6)
                        {
                            Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Checking Domain", 
                                $"Expected: '{automation.expected_result}', Actual: '{device_identity.domain}'");
                            
                            if (automation.expected_result?.Trim() == device_identity.domain?.Trim())
                            {
                                policy_name = automation.trigger;
                                Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Match Found", $"Domain matched: {policy_name}");
                                break;
                            }
                        }
                    }
                }

                // No policy found
                if (policy_name == null)
                {
                    Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "matched", "no_assigned_policy_found");
                    return "no_assigned_policy_found";
                }
                else
                {
                    Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "Filter automations (matched)", policy_name);
                }
                
                try
                {
                    string query = "SELECT * FROM policies WHERE name = @policy_name;";

                    MySqlCommand command = new MySqlCommand(query, conn);
                    command.Parameters.AddWithValue("@policy_name", policy_name);
                    
                    Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy (policy)", "MySQL_Prepared_Query", query);

                    using (DbDataReader reader = await command.ExecuteReaderAsync())
                    {
                        if (reader.HasRows)
                        {
                            while (await reader.ReadAsync())
                            {
                                antivirus_settings_json = reader["antivirus_settings"].ToString();
                                antivirus_exclusions_json = reader["antivirus_exclusions"].ToString();
                                antivirus_scan_jobs_json = reader["antivirus_scan_jobs"].ToString();
                                antivirus_controlled_folder_access_folders_json = reader["antivirus_controlled_folder_access_folders"].ToString();
                                sensors_json = reader["sensors"].ToString();
                                jobs_json = reader["jobs"].ToString();
                                tray_icon_settings_json = reader["tray_icon_settings"].ToString();
                                agent_settings_json = reader["agent_settings"].ToString();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Error("Agent.Windows.Policy_Handler.Get_Policy", "MySQL_Query", ex.ToString());
                }
                finally
                {
                    //conn.Close();
                }

                // Get sensors from database
                List<Sensors_Device_Entity> device_sensors_list = JsonConvert.DeserializeObject<List<Sensors_Device_Entity>>(sensors_json);
                List<Sensors_Entity> sensors_list = new List<Sensors_Entity>();

                try
                {
                    string query = "SELECT * FROM sensors;";

                    MySqlCommand command = new MySqlCommand(query, conn);

                    Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy (sensors)", "MySQL_Prepared_Query", query);

                    using (DbDataReader reader = await command.ExecuteReaderAsync())
                    {
                        if (reader.HasRows)
                        {
                            while (await reader.ReadAsync())
                            {
                                foreach (var sensor in device_sensors_list)
                                {
                                    if (sensor.id == reader["id"].ToString())
                                    {
                                        Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "sensor_json", sensors_json);

                                        Sensors_Entity sensor_object = JsonConvert.DeserializeObject<Sensors_Entity>(reader["json"].ToString());
                                        sensors_list.Add(sensor_object);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Error("Agent.Windows.Policy_Handler.Get_Policy", "MySQL_Query", ex.ToString());
                }
                finally
                {
                    //conn.Close();
                }

                // Create sensors json
                string policy_sensors_json = JsonConvert.SerializeObject(sensors_list);

                // Get jobs from database
                List<Jobs_Device_Entity> device_jobs_list = JsonConvert.DeserializeObject<List<Jobs_Device_Entity>>(jobs_json);
                List<Jobs_Entity> jobs_list = new List<Jobs_Entity>();

                try
                {
                    string query = "SELECT * FROM jobs;";

                    MySqlCommand command = new MySqlCommand(query, conn);

                    Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy (jobs)", "MySQL_Prepared_Query", query);

                    using (DbDataReader reader = await command.ExecuteReaderAsync())
                    {
                        if (reader.HasRows)
                        {
                            while (await reader.ReadAsync())
                            {
                                foreach (var job in device_jobs_list)
                                {
                                    Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "job_json (id)", job.id);

                                    if (job.id == reader["id"].ToString())
                                    {
                                        Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy", "job_json", reader["json"].ToString());

                                        Jobs_Entity job_object = JsonConvert.DeserializeObject<Jobs_Entity>(reader["json"].ToString());
                                        jobs_list.Add(job_object);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Error("Agent.Windows.Policy_Handler.Get_Policy", "MySQL_Query", ex.ToString());
                }
                finally
                {
                    //await conn.CloseAsync();
                }

                // Create jobs json
                string policy_jobs_json = JsonConvert.SerializeObject(jobs_list);

                // Get antivirus controlled folder access ruleset from database
                try
                {
                    string ruleset_name = String.Empty;

                    using (JsonDocument document = JsonDocument.Parse(antivirus_settings_json))
                    {
                        JsonElement controlled_folder_access_ruleset_element = document.RootElement.GetProperty("controlled_folder_access_ruleset");
                        ruleset_name = controlled_folder_access_ruleset_element.ToString();
                    }

                    string query = "SELECT * FROM antivirus_controlled_folder_access_rulesets WHERE name = @name;";

                    MySqlCommand command = new MySqlCommand(query, conn);
                    command.Parameters.AddWithValue("@name", ruleset_name);

                    Logging.Handler.Debug("Agent.Windows.Policy_Handler.Get_Policy (antivirus_controlled_folder_access_rulesets)", "MySQL_Prepared_Query", query);

                    using (DbDataReader reader = await command.ExecuteReaderAsync())
                    {
                        if (reader.HasRows)
                        {
                            while (await reader.ReadAsync())
                            {
                                antivirus_controlled_folder_access_ruleset_json = reader["json"].ToString();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Error("Agent.Windows.Policy_Handler.Get_Policy", "MySQL_Query", ex.ToString());
                }
                finally
                {
                    //await conn.CloseAsync();
                }

                // Set synced = 1
                try
                {
                    string execute_query = "UPDATE devices SET synced = 1 WHERE access_key = @access_key;";

                    MySqlCommand command = new MySqlCommand(execute_query, conn);
                    command.Parameters.AddWithValue("@access_key", device_identity.access_key);

                    command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Logging.Handler.Error("Agent.Windows.Policy_Handler.Get_Policy (set synced = 1)", "Result", ex.Message);
                }
                finally
                {
                    await conn.CloseAsync();
                }

                // Create policy JSON
                var jsonObject = new
                {
                    antivirus_settings_json,
                    antivirus_exclusions_json,
                    antivirus_scan_jobs_json,
                    antivirus_controlled_folder_access_folders_json,
                    antivirus_controlled_folder_access_ruleset_json,
                    policy_sensors_json,
                    policy_jobs_json,
                    tray_icon_settings_json,
                    agent_settings_json
                };

                // Serialize JSON object to string and return it
                string policy_json = JsonConvert.SerializeObject(jsonObject);

                return policy_json;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("Agent.Windows.Version_Handler.Check_Version", "General error", ex.ToString());
                return "Invalid request.";
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Diagnostics;
using System.Threading;
using _x101.HWID_System;
using System.Data.SqlClient;
using System.Data;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using System.Xml;
using System.ServiceProcess;
using System.Net.NetworkInformation;
using static Global.Sensors.Time_Scheduler;
using Global.Helper;
using NetLock_RMM_Agent_Comm;
using static Global.Jobs.Time_Scheduler;

namespace Global.Sensors
{
    internal class Time_Scheduler
    {
        public class Sensor
        {
            public string id { get; set; } = string.Empty;
            public string name { get; set; } = string.Empty;
            public string date { get; set; } = string.Empty;
            public string last_run { get; set; } = string.Empty;
            public string author { get; set; } = string.Empty;
            public string description { get; set; } = string.Empty;
            public string platform { get; set; } = string.Empty;
            public int severity { get; set; } = 0;
            public int category { get; set; } = 0;
            public int sub_category { get; set; } = 0;
            public int utilization_category { get; set; } = 0;
            public int notification_treshold_count { get; set; } = 0;
            public int notification_treshold_max { get; set; } = 0;
            public string notification_history { get; set; } = string.Empty;
            public int action_treshold_count { get; set; } = 0;
            public int action_treshold_max { get; set; } = 0;

            public string action_history { get; set; } = string.Empty;
            public bool auto_reset { get; set; } = false;
            public string script { get; set; } = string.Empty;
            public string script_action { get; set; } = string.Empty;
            public int cpu_usage { get; set; } = 0;
            public string process_name { get; set; } = string.Empty;
            public int ram_usage { get; set; } = 0;
            public int disk_usage { get; set; } = 0;
            public int disk_minimum_capacity { get; set; } = 0;
            public int disk_category { get; set; } = 0;
            public string disk_letters { get; set; } = string.Empty;
            public bool disk_include_network_disks { get; set; } = false;
            public bool disk_include_removable_disks { get; set; } = false;
            public string eventlog { get; set; } = string.Empty;
            public string eventlog_event_id { get; set; } = string.Empty;
            public string expected_result { get; set; } = string.Empty;

            //service sensor
            public string service_name { get; set; } = string.Empty;
            public int service_condition { get; set; } = 0;
            public int service_action { get; set; } = 0;

            //ping sensor
            public string ping_address { get; set; } = string.Empty;
            public int ping_timeout { get; set; } = 0;
            public int ping_condition { get; set; } = 0;

            //time schedule
            public int time_scheduler_type { get; set; } = 0;
            public int time_scheduler_seconds { get; set; } = 0;
            public int time_scheduler_minutes { get; set; } = 0;
            public int time_scheduler_hours { get; set; } = 0;
            public string time_scheduler_time { get; set; } = string.Empty;
            public string time_scheduler_date { get; set; } = string.Empty;
            public bool time_scheduler_monday { get; set; } = true;
            public bool time_scheduler_tuesday { get; set; } = true;
            public bool time_scheduler_wednesday { get; set; } = true;
            public bool time_scheduler_thursday { get; set; } = true;
            public bool time_scheduler_friday { get; set; } = true;
            public bool time_scheduler_saturday { get; set; } = true; 
            public bool time_scheduler_sunday { get; set; } = true;

            // NetLock notifications
            public bool already_notified { get; set; } = false; // tracks if notification was sent (for spam prevention & resolved notifications)
            public bool suppress_notification { get; set; } = false; // controls whether already_notified is used for spam prevention
            public bool resolved_notification { get; set; } = false;
            public bool notifications_mail { get; set; } = false;
            public bool notifications_microsoft_teams { get; set; } = false;
            public bool notifications_telegram { get; set; } = false;
            public bool notifications_ntfy_sh { get; set; } = false;
            public bool notifications_webhook { get; set; } = false;
        }

        public class Notifications
        {
            public bool mail { get; set; }
            public bool microsoft_teams { get; set; }
            public bool telegram { get; set; }
            public bool ntfy_sh { get; set; }
            public bool webhook { get; set; }
        }

        public class Process_Information
        {
            public int id { get; set; }
            public string name { get; set; }
            public string cpu { get; set; }
            public string ram { get; set; }
            public string user { get; set; }
            public string created { get; set; }
            public string path { get; set; }
            public string cmd { get; set; }
        }

        // Helper method to check if sensor should run today based on weekday settings
        private static bool ShouldRunToday(Sensor sensor)
        {
            try
            {
                switch (DateTime.Now.DayOfWeek)
                {
                    case DayOfWeek.Monday:
                        return sensor.time_scheduler_monday;
                    case DayOfWeek.Tuesday:
                        return sensor.time_scheduler_tuesday;
                    case DayOfWeek.Wednesday:
                        return sensor.time_scheduler_wednesday;
                    case DayOfWeek.Thursday:
                        return sensor.time_scheduler_thursday;
                    case DayOfWeek.Friday:
                        return sensor.time_scheduler_friday;
                    case DayOfWeek.Saturday:
                        return sensor.time_scheduler_saturday;
                    case DayOfWeek.Sunday:
                        return sensor.time_scheduler_sunday;
                    default:
                        return false;
                }
            }
            catch (Exception e)
            {
                Logging.Error("Sensors.Time_Scheduler.ShouldRunToday", "Check if sensor should run today",
                    "Sensor id: " + sensor.id + " Exception: " + e.ToString());
                
                return false;
            }
        }

        // Helper method to write encrypted sensor to disk
        private static void WriteEncryptedSensor(string filePath, Sensor sensor)
        {
            try
            {
                string sensor_json = JsonSerializer.Serialize(sensor);
                string encrypted_json = Encryption.String_Encryption.Encrypt(sensor_json, Application_Settings.NetLock_Local_Encryption_Key);
                File.WriteAllText(filePath, encrypted_json);
            }
            catch (Exception e)
            {
                Logging.Error("Sensors.Time_Scheduler.WriteEncryptedSensor", "Write encrypted sensor to disk",
                    "Sensor id: " + sensor.id + " Exception: " + e.ToString());
            }
        }
        
        // Helper method to safely deserialize string list (for action_history and notification_history)
        private static List<string> SafeDeserializeStringList(string json)
        {
            if (string.IsNullOrEmpty(json))
                return new List<string>();

            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(json);
                return list ?? new List<string>();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        // Helper method to mark sensor as notified and optionally add to history (for spam prevention when suppress_notification is enabled)                                 
        private static void AddNotificationIfNeeded(Sensor sensor_item, string sensor_path, string details)               
        {            
            try
            {
                // Debug logging
                Logging.Sensors("Sensors.Time_Scheduler.AddNotificationIfNeeded", "Called", 
                    $"Sensor: {sensor_item.name}, already_notified: {sensor_item.already_notified}, suppress_notification: {sensor_item.suppress_notification}");
                
                // Always mark as notified (for resolved notifications)
                if (!sensor_item.already_notified)
                {
                    sensor_item.already_notified = true;
                    Logging.Sensors("Sensors.Time_Scheduler.AddNotificationIfNeeded", "Setting already_notified = true", "Sensor: " + sensor_item.name);
                    
                    // Only add to notification history if suppress_notification is enabled
                    if (sensor_item.suppress_notification)
                    {
                        if (String.IsNullOrEmpty(sensor_item.notification_history))                                               
                        {                                                                                                         
                            List<string> notification_history_list = new List<string> { details };                                
                            sensor_item.notification_history = JsonSerializer.Serialize(notification_history_list);               
                        }                                                                                                         
                        else                                                                                                      
                        {                                                                                                         
                            List<string> notification_history_list = SafeDeserializeStringList(sensor_item.notification_history); 
                            notification_history_list.Add(details);                                                               
                            sensor_item.notification_history = JsonSerializer.Serialize(notification_history_list);               
                        }
                    }
                    
                    WriteEncryptedSensor(sensor_path, sensor_item);                                                           
                }       
            }
            catch (Exception e)
            {
                Logging.Error("Sensors.Time_Scheduler.AddNotificationIfNeeded", "Add notification if needed",
                    "Sensor id: " + sensor_item.id + " Exception: " + e.ToString());
            }
        }                                                                                                                 
                                                                                                                          
        // Helper method to reset notification flag when sensor is no longer triggered (always resets for resolved notifications)                                          
        private static void ResetNotificationFlagIfNeeded(Sensor sensor_item, string sensor_path)                         
        {
            try
            {
                // Always reset the flag (for resolved notifications to work)
                if (sensor_item.already_notified)                                                            
                {                                                                                                             
                    sensor_item.already_notified = false;                                                                     
                    WriteEncryptedSensor(sensor_path, sensor_item);                                                           
                }
            }
            catch (Exception e)
            {
                Logging.Error("Sensors.Time_Scheduler.ResetNotificationFlagIfNeeded", "Reset notification flag if needed",
                    "Sensor id: " + sensor_item.id + " Exception: " + e.ToString());
            }
        }

        // Helper method to send resolved notification when problem is fixed
        private static void SendResolvedNotificationIfNeeded(Sensor sensor_item, string sensor_path)
        {
            try
            {
                // Debug logging
                Logging.Sensors("Sensors.Time_Scheduler.SendResolvedNotificationIfNeeded", "Check conditions", 
                    $"Sensor: {sensor_item.name}, already_notified: {sensor_item.already_notified}, resolved_notification: {sensor_item.resolved_notification}");
                
                // Only send resolved notification if both conditions are met:
                // 1. already_notified = true (notification was sent before)
                // 2. resolved_notification = true (resolved feature is enabled)
                if (sensor_item.already_notified && sensor_item.resolved_notification)
                {
                    Logging.Sensors("Sensors.Time_Scheduler.SendResolvedNotificationIfNeeded", "Sending resolved notification", "Sensor: " + sensor_item.name);
                    
                    string details = String.Empty;
                    
                    // Create resolved message based on language
                    if (Configuration.Agent.language == "en-US")
                    {
                        details = $"The issue with sensor '{sensor_item.name}' has been resolved." + Environment.NewLine + Environment.NewLine +
                                  "Sensor name: " + sensor_item.name + Environment.NewLine +
                                  "Description: " + sensor_item.description + Environment.NewLine +
                                  "Time resolved: " + DateTime.Now + Environment.NewLine +
                                  "Status: Problem resolved, monitoring continues.";
                    }
                    else if (Configuration.Agent.language == "de-DE")
                    {
                        details = $"Das Problem mit Sensor '{sensor_item.name}' wurde behoben." + Environment.NewLine + Environment.NewLine +
                                  "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                  "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                  "Behoben am: " + DateTime.Now + Environment.NewLine +
                                  "Status: Problem behoben, Überwachung läuft weiter.";
                    }

                    // Create notification_json
                    Notifications notifications = new Notifications
                    {
                        mail = sensor_item.notifications_mail,
                        microsoft_teams = sensor_item.notifications_microsoft_teams,
                        telegram = sensor_item.notifications_telegram,
                        ntfy_sh = sensor_item.notifications_ntfy_sh,
                        webhook = sensor_item.notifications_webhook
                    };

                    string notifications_json = JsonSerializer.Serialize(notifications, new JsonSerializerOptions { WriteIndented = true });

                    // Send resolved event
                    if (Configuration.Agent.language == "en-US")
                        Events.Logger.Insert_Event("0", "Sensor", "Sensor Resolved: " + sensor_item.name, details, notifications_json, 2, 0);
                    else if (Configuration.Agent.language == "de-DE")
                        Events.Logger.Insert_Event("0", "Sensor", "Sensor Behoben: " + sensor_item.name, details, notifications_json, 2, 1);

                    Logging.Sensors("Sensors.Time_Scheduler.SendResolvedNotificationIfNeeded", "Resolved notification sent", "Sensor: " + sensor_item.name);
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Sensors.Time_Scheduler.SendResolvedNotificationIfNeeded", "Error sending resolved notification", 
                    "Sensor: " + sensor_item.name + " Exception: " + ex.ToString());
            }
        }
        
        public static void Check_Execution()
        {
            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Check sensor execution", "Start");

            Initialization.Health.Check_Directories();

            string sensor_id = String.Empty; // needed for logging

            try
            {
                DateTime os_up_time = Global.Helper.Globalization.GetLastBootUpTime(); // Environment.TickCount is not reliable, use WMI instead

                List<Sensor> sensor_items = JsonSerializer.Deserialize<List<Sensor>>(Device_Worker.policy_sensors_json);

                // Write each sensor to disk if not already exists
                foreach (var sensor in sensor_items)
                {
                    try
                    {
                        // Check if job is for the current platform
                        if (OperatingSystem.IsWindows() && sensor.platform != "Windows")
                            continue;
                        else if (OperatingSystem.IsLinux() && sensor.platform != "Linux")
                            continue;
                        else if (OperatingSystem.IsMacOS() && sensor.platform != "MacOS")
                            continue;

                        Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Check if sensor exists on disk", "Sensor: " + sensor.name + " Sensor id: " + sensor.id);

                        string sensor_json = JsonSerializer.Serialize(sensor);
                        string sensor_path = Path.Combine(Application_Paths.program_data_sensors, sensor.id + ".json");

                        if (!File.Exists(sensor_path))
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Check if sensor exists on disk", "false");
                            // Encrypt sensor JSON before writing
                            string encrypted_sensor_json = Encryption.String_Encryption.Encrypt(sensor_json, Application_Settings.NetLock_Local_Encryption_Key);
                            File.WriteAllText(sensor_path, encrypted_sensor_json);
                        }

                        // Check if sensor file is valid and if script has changed
                        if (File.Exists(sensor_path))
                        {
                            try
                            {
                                // Decrypt sensor JSON after reading
                                string encrypted_existing_sensor_json = File.ReadAllText(sensor_path);
                                string existing_sensor_json = Encryption.String_Encryption.Decrypt(encrypted_existing_sensor_json, Application_Settings.NetLock_Local_Encryption_Key);
                                
                                // Integrity check: Try to deserialize to validate the JSON format
                                Sensor existing_sensor = JsonSerializer.Deserialize<Sensor>(existing_sensor_json);
                                
                                if (existing_sensor.script != sensor.script)
                                {
                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Sensor script has changed. Updating it.", "Sensor: " + sensor.name + " Sensor id: " + sensor.id);
                                    
                                    // Encrypt sensor JSON before writing
                                    string encrypted_sensor_json = Encryption.String_Encryption.Encrypt(sensor_json, Application_Settings.NetLock_Local_Encryption_Key);
                                    File.WriteAllText(sensor_path, encrypted_sensor_json);
                                }
                            }
                            catch (Exception ex)
                            {
                                // Integrity check failed: Delete corrupted sensor file
                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Sensor file integrity check failed. Deleting corrupted file.", 
                                    "Sensor: " + sensor.name + " Sensor id: " + sensor.id + " Exception: " + ex.Message);
                                
                                File.Delete(sensor_path);
                                
                                // Recreate the sensor file with the current valid data
                                string encrypted_sensor_json = Encryption.String_Encryption.Encrypt(sensor_json, Application_Settings.NetLock_Local_Encryption_Key);
                                File.WriteAllText(sensor_path, encrypted_sensor_json);
                                
                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Sensor file recreated with valid data.", 
                                    "Sensor: " + sensor.name + " Sensor id: " + sensor.id);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Logging.Error("Sensors.Time_Scheduler.Check_Execution", "Error processing sensor before execution check",
                            "Sensor id: " + sensor.id + " Exception: " + e.ToString());
                        
                        // Delete corrupted sensor file if exists
                        string sensor_path = Path.Combine(Application_Paths.program_data_sensors, sensor.id + ".json");
                        
                        if (File.Exists(sensor_path))
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Delete corrupted sensor file", "Sensor id: " + sensor.id);
                            File.Delete(sensor_path);
                        }
                    }
                }

                // Clean up old sensors not existing anymore
                foreach (string file in Directory.GetFiles(Application_Paths.program_data_sensors))
                {
                    try
                    {
                        Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Clean old sensors", "Sensor: " + file);

                        string file_name = Path.GetFileName(file);
                        string file_id = file_name.Replace(".json", "");

                        bool found = false;

                        foreach (var sensor in sensor_items)
                        {
                            if (sensor.id == file_id)
                            {
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Clean old sensors", "Delete sensor: " + file);
                            File.Delete(file);
                        }
                    }
                    catch (Exception e)
                    {
                        Logging.Error("Sensors.Time_Scheduler.Check_Execution", "Error during cleanup of old sensors",
                            "Sensor file: " + file + " Exception: " + e.ToString());
                    }
                }

                // Now read & consume each sensor
                foreach (var sensor in Directory.GetFiles(Application_Paths.program_data_sensors))
                {
                    try
                    {
                        // Decrypt sensor JSON after reading
                        string encrypted_sensor_json = File.ReadAllText(sensor);
                        string sensor_json = Encryption.String_Encryption.Decrypt(encrypted_sensor_json, Application_Settings.NetLock_Local_Encryption_Key);
                        Sensor sensor_item = JsonSerializer.Deserialize<Sensor>(sensor_json);

                        // Null-check after deserialization
                        if (sensor_item == null)
                        {
                            Logging.Error("Sensors.Time_Scheduler.Check_Execution", "Failed to deserialize sensor. Deleting it.", "Sensor file: " + sensor);
                            
                            // Delete corrupted sensor file
                            File.Delete(sensor);
                            continue; // Skip processing this sensor
                        }

                        sensor_id = sensor_item.id; // needed for logging
                        
                        // JSON format integrity check - verify all notification fields exist
                        // This handles cases where old JSON format is missing fields like notifications_webhook
                        bool hasIntegrityIssue = false;
                        try
                        {
                            // Parse raw JSON to check for missing fields
                            using (JsonDocument doc = JsonDocument.Parse(sensor_json))
                            {
                                JsonElement root = doc.RootElement;
                                
                                // Check if notification fields exist in JSON
                                if (!root.TryGetProperty("notifications_webhook", out _))
                                {
                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "JSON format integrity check", 
                                        $"Missing field 'notifications_webhook' in sensor {sensor_item.name} ({sensor_item.id}). Deleting corrupted sensor file.");
                                    hasIntegrityIssue = true;
                                }
                                
                                // Additional checks for other notification fields (in case they're also missing)
                                if (!root.TryGetProperty("notifications_mail", out _) ||
                                    !root.TryGetProperty("notifications_microsoft_teams", out _) ||
                                    !root.TryGetProperty("notifications_telegram", out _) ||
                                    !root.TryGetProperty("notifications_ntfy_sh", out _))
                                {
                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "JSON format integrity check", 
                                        $"Missing notification fields in sensor {sensor_item.name} ({sensor_item.id}). Deleting corrupted sensor file.");
                                    hasIntegrityIssue = true;
                                }
                                
                                // Check for other important fields that might be missing
                                if (!root.TryGetProperty("suppress_notification", out _) ||
                                    !root.TryGetProperty("resolved_notification", out _) ||
                                    !root.TryGetProperty("already_notified", out _))
                                {
                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "JSON format integrity check", 
                                        $"Missing notification control fields in sensor {sensor_item.name} ({sensor_item.id}). Deleting corrupted sensor file.");
                                    hasIntegrityIssue = true;
                                }
                            }
                            
                            // If integrity issue detected, delete sensor file (will be recreated by existing logic)
                            if (hasIntegrityIssue)
                            {
                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Deleting sensor with integrity issues", 
                                    $"Sensor: {sensor_item.name} ({sensor_item.id}). File will be recreated on next sync.");
                                
                                File.Delete(sensor);
                                continue; // Skip processing this sensor, it will be recreated on next run
                            }
                        }
                        catch (Exception ex)
                        {
                            Logging.Error("Sensors.Time_Scheduler.Check_Execution", "JSON format integrity check failed", 
                                $"Sensor: {sensor_item.name} ({sensor_item.id}) Exception: {ex.ToString()}");
                            
                            // Delete corrupted file on error
                            try
                            {
                                File.Delete(sensor);
                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Deleted corrupted sensor file after integrity check error", 
                                    $"Sensor: {sensor_item.name} ({sensor_item.id})");
                            }
                            catch
                            {
                                // Ignore deletion errors
                            }
                            continue; // Skip processing this sensor
                        }

                        Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Check sensor execution", "Sensor: " + sensor_item.name + " time_scheduler_type: " + sensor_item.time_scheduler_type);

                        // Check thresholds
                        // Check notification treshold
                        if (string.IsNullOrEmpty(sensor_item.notification_treshold_count.ToString()))
                        {
                            sensor_item.notification_treshold_count = 0;
                            WriteEncryptedSensor(sensor, sensor_item);
                        }

                        // Check action treshold
                        if (string.IsNullOrEmpty(sensor_item.action_treshold_count.ToString()))
                        {
                            sensor_item.action_treshold_count = 0;
                            WriteEncryptedSensor(sensor, sensor_item);
                        }

                        // Check enabled
                        /*if (!sensor_item.enabled)
                        {
                            Logging.Handler.Sensors("Sensors.Time_Scheduler.Check_Execution", "Check sensor execution", "Sensor disabled");

                            continue;
                        }*/

                        bool execute = false;

                        if (sensor_item.time_scheduler_type == 0) // system boot
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "System boot", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run ?? DateTime.Now.ToString()) + " Last boot: " + os_up_time.ToString());

                            // Check if last run is empty
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                // Only execute if the last boot was within the last 10 minutes
                                // This prevents the sensor from executing when newly created on a system that has been running for a while
                                if (DateTime.Now - os_up_time <= TimeSpan.FromMinutes(10))
                                {
                                    execute = true;
                                }
                                
                                // Set last_run to the current boot time to prevent re-execution until next reboot
                                sensor_item.last_run = os_up_time.ToString();
                                WriteEncryptedSensor(sensor, sensor_item);
                            }
                            else if (DateTime.Parse(sensor_item.last_run) < os_up_time)
                            {
                                // Sensor was last run before the current boot, so execute it
                                execute = true;
                            }
                        }
                        else if (sensor_item.time_scheduler_type == 1) // date & time
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "date & time", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run ?? DateTime.Now.ToString()));

                            DateTime scheduledDateTime = DateTime.ParseExact($"{sensor_item.time_scheduler_date.Split(' ')[0]} {sensor_item.time_scheduler_time}", "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

                            // Check if last run is empty, if so, subsract 24 hours from scheduled time to trigger the execution
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = (scheduledDateTime - TimeSpan.FromHours(24)).ToString();
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            DateTime lastRunDateTime = DateTime.Parse(sensor_item.last_run);

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "date & time", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run) + " scheduledDateTime: " + scheduledDateTime.ToString() + " execute: " + execute.ToString());

                            if (DateTime.Now.Date >= scheduledDateTime.Date && DateTime.Now.TimeOfDay >= scheduledDateTime.TimeOfDay && lastRunDateTime < scheduledDateTime)
                                execute = true;
                        }
                        else if (sensor_item.time_scheduler_type == 2) // all x seconds
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "all x seconds", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run ?? DateTime.Now.ToString()));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = DateTime.Now.ToString();
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            if (DateTime.Parse(sensor_item.last_run) <= DateTime.Now - TimeSpan.FromSeconds(sensor_item.time_scheduler_seconds))
                                execute = true;

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "all x seconds", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run) + " execute: " + execute.ToString());
                        }
                        else if (sensor_item.time_scheduler_type == 3) // all x minutes
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "all x minutes", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + (sensor_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            DateTime lastRun = DateTime.Parse(sensor_item.last_run, CultureInfo.InvariantCulture);
                            if (lastRun <= DateTime.Now - TimeSpan.FromMinutes(sensor_item.time_scheduler_minutes))
                                execute = true;

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "all x minutes", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + lastRun + " execute: " + execute.ToString());
                        }
                        else if (sensor_item.time_scheduler_type == 4) // all x hours
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "all x hours", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + (sensor_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            DateTime lastRun = DateTime.Parse(sensor_item.last_run, CultureInfo.InvariantCulture);
                            if (lastRun <= DateTime.Now - TimeSpan.FromHours(sensor_item.time_scheduler_hours))
                                execute = true;

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "all x hours", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + lastRun + " execute: " + execute.ToString());
                        }
                        else if (sensor_item.time_scheduler_type == 5) // date, all x seconds
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "date, all x seconds", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run ?? DateTime.Now.ToString()));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = DateTime.Now.ToString();
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            if (DateTime.Now.Date == DateTime.Parse(sensor_item.time_scheduler_date).Date && DateTime.Parse(sensor_item.last_run) <= DateTime.Now - TimeSpan.FromSeconds(sensor_item.time_scheduler_seconds))
                                execute = true;

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "date, all x seconds", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run) + " execute: " + execute.ToString());
                        }
                        else if (sensor_item.time_scheduler_type == 6) // date, all x minutes
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "date, all x minutes", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run ?? DateTime.Now.ToString()));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = DateTime.Now.ToString();
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            if (DateTime.Now.Date == DateTime.Parse(sensor_item.time_scheduler_date).Date && DateTime.Parse(sensor_item.last_run) < DateTime.Now - TimeSpan.FromMinutes(sensor_item.time_scheduler_minutes))
                                execute = true;

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "date, all x minutes", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run) + " execute: " + execute.ToString());
                        }
                        else if (sensor_item.time_scheduler_type == 7) // date, all x hours
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "date, all x hours", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run ?? DateTime.Now.ToString()));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = DateTime.Now.ToString();
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            if (DateTime.Now.Date == DateTime.Parse(sensor_item.time_scheduler_date).Date && DateTime.Parse(sensor_item.last_run) < DateTime.Now - TimeSpan.FromHours(sensor_item.time_scheduler_hours))
                                execute = true;

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "date, all x hours", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + DateTime.Parse(sensor_item.last_run) + " execute: " + execute.ToString());
                        }
                        else if (sensor_item.time_scheduler_type == 8) // following days at X time
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "following days at X time", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + (sensor_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            DateTime scheduledTime = DateTime.ParseExact(sensor_item.time_scheduler_time, "HH:mm:ss", CultureInfo.InvariantCulture);

                            // Check if last run is empty, if so set it to a time in the past to trigger initial execution
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = DateTime.Now.AddDays(-1).ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            DateTime lastRunDateTime = DateTime.Parse(sensor_item.last_run, CultureInfo.InvariantCulture);

                            // Check if current time is past the scheduled time and we haven't run today yet
                            bool shouldRunToday = DateTime.Now.TimeOfDay >= scheduledTime.TimeOfDay && lastRunDateTime.Date < DateTime.Now.Date;

                            // Use helper method to check weekday
                            if (ShouldRunToday(sensor_item) && shouldRunToday)
                                execute = true;

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "following days at X time", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + lastRunDateTime + " execute: " + execute.ToString());
                        }
                        else if (sensor_item.time_scheduler_type == 9) // following days, x seconds
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "following days, x seconds", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + (sensor_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            DateTime lastRun = DateTime.Parse(sensor_item.last_run, CultureInfo.InvariantCulture);

                            // Check if it's a valid day AND the interval has passed
                            if (ShouldRunToday(sensor_item) && lastRun <= DateTime.Now - TimeSpan.FromSeconds(sensor_item.time_scheduler_seconds))
                                execute = true;

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "following days, x seconds", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + lastRun + " execute: " + execute.ToString());
                        }
                        else if (sensor_item.time_scheduler_type == 10) // following days, x minutes
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "following days, x minutes", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + (sensor_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            DateTime lastRun = DateTime.Parse(sensor_item.last_run, CultureInfo.InvariantCulture);

                            // Check if it's a valid day AND the interval has passed
                            if (ShouldRunToday(sensor_item) && lastRun <= DateTime.Now - TimeSpan.FromMinutes(sensor_item.time_scheduler_minutes))
                                execute = true;

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "following days, x minutes", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + lastRun + " execute: " + execute.ToString());
                        }
                        else if (sensor_item.time_scheduler_type == 11) // following days, x hours
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "following days, x hours", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + (sensor_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(sensor_item.last_run))
                            {
                                sensor_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedSensor(sensor, sensor_item);
                            }

                            DateTime lastRun = DateTime.Parse(sensor_item.last_run, CultureInfo.InvariantCulture);

                            // Check if it's a valid day AND the interval has passed
                            if (ShouldRunToday(sensor_item) && lastRun <= DateTime.Now - TimeSpan.FromHours(sensor_item.time_scheduler_hours))
                                execute = true;

                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "following days, x hours", "name: " + sensor_item.name + " id: " + sensor_item.id + " last_run: " + lastRun + " execute: " + execute.ToString());
                        }

                        // Execute if needed
                        if (execute)
                        {
                            // Store the old last_run value in case we need to rollback
                            string previous_last_run = sensor_item.last_run;
                            
                            // Update last run IMMEDIATELY to prevent race conditions (before executing the sensor)
                            var startTime = DateTime.Now;
                            sensor_item.last_run = startTime.ToString(CultureInfo.InvariantCulture);
                            WriteEncryptedSensor(sensor, sensor_item);

                            bool triggered = false;
                            var endTime = DateTime.Now;

                            string action_result = String.Empty;

                            if (sensor_item.action_treshold_max != 1)
                                action_result = "[" + DateTime.Now.ToString(CultureInfo.InvariantCulture) + "]";

                            string details = String.Empty;
                            string additional_details = String.Empty;
                            string notification_history = String.Empty;
                            string action_history = String.Empty;

                            List<Process_Information> process_information_list = new List<Process_Information>();

                            int resource_usage = 0;

                            try
                            {
                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Execute sensor", "name: " + sensor_item.name + " id: " + sensor_item.id);

                                if (sensor_item.category == 0) // utilization 
                                {
                                    if (sensor_item.sub_category == 0) // cpu
                                    {
                                        resource_usage = Device_Information.Hardware.CPU_Usage();

                                        if (sensor_item.cpu_usage < resource_usage) // Check if CPU utilization is higher than the treshold
                                        {
                                            triggered = true;

                                            // if action treshold is reached, execute the action and reset the counter
                                            if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                            {
                                                if (OperatingSystem.IsWindows())
                                                    action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                                else if (OperatingSystem.IsLinux())
                                                    action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                                else if (OperatingSystem.IsMacOS())
                                                    action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                                // Create action history if not exists
                                                if (String.IsNullOrEmpty(sensor_item.action_history))
                                                {
                                                    List<string> action_history_list = new List<string>
                                                    {
                                                        action_result
                                                    };

                                                    sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                }
                                                else // if exists, add the result to the list
                                                {
                                                    List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                    action_history_list.Add(action_result);
                                            sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                        }

                                        // Reset the counter
                                        sensor_item.action_treshold_count = 0;
                                        WriteEncryptedSensor(sensor, sensor_item);
                                    }
                                    else // if not, increment the counter
                                    {
                                        sensor_item.action_treshold_count++;
                                        WriteEncryptedSensor(sensor, sensor_item);
                                    }

                                    // Create event
                                    if (Configuration.Agent.language == "en-US")
                                    {
                                        details =
                                            $"The processor utilization exceeds the threshold value. The current utilization is {resource_usage}%. The defined limit is {sensor_item.cpu_usage}%." + Environment.NewLine + Environment.NewLine +
                                                    "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                    "Description: " + sensor_item.description + Environment.NewLine +
                                                    "Type: Processor" + Environment.NewLine +
                                                    "Time: " + DateTime.Now + Environment.NewLine +
                                                    "Selected limit: " + sensor_item.cpu_usage + " (%)" + Environment.NewLine +
                                                    "In usage: " + resource_usage + " (%)" + Environment.NewLine +
                                                    "Action result: " + Environment.NewLine + action_result;
                                            }
                                            else if (Configuration.Agent.language == "de-DE")
                                            {
                                                details =
                                                   $"Die Prozessor-Auslastung überschreitet den Schwellenwert. Aktuell beträgt die Auslastung {resource_usage}%. Das festgelegte Limit ist {sensor_item.cpu_usage}%." + Environment.NewLine + Environment.NewLine +
                                                   "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                   "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                   "Typ: Prozessor" + Environment.NewLine +
                                                   "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                   "Festgelegtes Limit: " + sensor_item.cpu_usage + " (%)" + Environment.NewLine +
                                                   "In Verwendung: " + resource_usage + " (%)" + Environment.NewLine +
                                                   "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                            }
                                        }
                                    }
                                    else if (sensor_item.sub_category == 1) // RAM
                                    {
                                        int ram_usage = Device_Information.Hardware.RAM_Usage();
                                        
                                        Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "RAM Check", $"Current RAM usage: {ram_usage}%, Threshold: {sensor_item.ram_usage}%, Triggered: {sensor_item.ram_usage < ram_usage}");

                                        if (sensor_item.ram_usage < ram_usage)
                                        {
                                            triggered = true;

                                            // if action treshold is reached, execute the action and reset the counter
                                            if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                            {
                                                if (OperatingSystem.IsWindows())
                                                    action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                                else if (OperatingSystem.IsLinux())
                                                    action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                                else if (OperatingSystem.IsMacOS())
                                                    action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                                // Create action history if not exists
                                                if (String.IsNullOrEmpty(sensor_item.action_history))
                                                {
                                                    List<string> action_history_list = new List<string>
                                                    {
                                                        action_result
                                                    };

                                                    sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                }
                                                else // if exists, add the result to the list
                                                {
                                                    List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                    action_history_list.Add(action_result);
                                                    sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                }

                                                // Reset the counter
                                                sensor_item.action_treshold_count = 0;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }
                                            else // if not, increment the counter
                                            {
                                                sensor_item.action_treshold_count++;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }

                                    // Create event
                                    if (Configuration.Agent.language == "en-US")
                                    {
                                        details =
                                            $"Memory utilization exceeds the threshold. The current utilization is {ram_usage}%. The defined limit is {sensor_item.ram_usage}%." + Environment.NewLine + Environment.NewLine +
                                                    "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                    "Description: " + sensor_item.description + Environment.NewLine +
                                                    "Type: RAM" + Environment.NewLine +
                                                    "Time: " + DateTime.Now + Environment.NewLine +
                                                    "Selected limit: " + sensor_item.ram_usage + " (%)" + Environment.NewLine +
                                                    "In usage: " + ram_usage + " (%)" + Environment.NewLine +
                                                    "Action result: " + Environment.NewLine + action_result;
                                            }
                                            else if (Configuration.Agent.language == "de-DE")
                                            {
                                                details =
                                                    $"Die Arbeitsspeicherauslastung überschreitet den Schwellenwert. Aktuell beträgt die Auslastung {ram_usage}%. Das festgelegte Limit ist {sensor_item.ram_usage}%." + Environment.NewLine + Environment.NewLine +
                                                    "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                    "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                    "Typ: Arbeitsspeicher" + Environment.NewLine +
                                                    "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                    "Festgelegtes Limit: " + sensor_item.ram_usage + " (%)" + Environment.NewLine +
                                                    "In Verwendung: " + ram_usage + " (%)" + Environment.NewLine +
                                                    "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                            }
                                        }
                                    }
                                    else if (sensor_item.sub_category == 2) // Drives
                                    {
                                        // Get all drives
                                        List<DriveInfo> drives = DriveInfo.GetDrives().ToList();

                                        List<string> drive_letters = sensor_item.disk_letters.Split(',')
                                            .Select(letter => letter.Trim())
                                            .Where(letter => !string.IsNullOrEmpty(letter)) // Removes empty entries
                                            .ToList();

                                        foreach (var drive in drives)
                                        {
                                            // Extract the drive letter on Windows; use the full name on Linux
                                            string drive_name = OperatingSystem.IsWindows()
                                                ? drive.Name.Replace(":\\", "") // Windows: "C:\\" => "C"
                                                : (drive.Name.EndsWith("/") && drive.Name.Count(c => c == '/') > 1
                                                    ? drive.Name.TrimEnd('/') // Only trim if there is more than one slash
                                                    : drive.Name);             // Otherwise leave the path unchanged

                                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "foreach drive", "name: " + drive_name + " " + true.ToString());

                                            // Check if the disk is included in the drives list that should be checked
                                            if (drive_letters.Contains(drive_name) || drive_letters.Count == 0)
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "disk_included", "name: " + drive_name + " " + true.ToString());

                                                // Check if the disk is a network disk or a removable disk, if so & they should not be scanned, skip it
                                                if (drive.DriveType == DriveType.Network && !sensor_item.disk_include_network_disks)
                                                    continue;

                                                if (drive.DriveType == DriveType.Removable && !sensor_item.disk_include_removable_disks)
                                                    continue;

                                                // Get specification
                                                string specification = String.Empty;
                                                // if type 0 = gb
                                                if (sensor_item.disk_category == 0 || sensor_item.disk_category == 1)
                                                    specification = "(GB)";
                                                else if (sensor_item.disk_category == 2 || sensor_item.disk_category == 3)
                                                    specification = "(%)";

                                                // Check disk usage
                                                int drive_total_space_gb = Device_Information.Hardware.Drive_Size_GB(drive_name);
                                                int drive_free_space_gb = Device_Information.Hardware.Drive_Free_Space_GB(drive_name);
                                                int drive_usage = 0; // If disk_category is 0 or 1, just calculate the usage in GB. If not, calculate the usage in percentage and respect that drive usage should not be seen as drive usage but as drive free space instead. This can cause confusion if not known

                                                // If disk_category is 0 or 1, just calculate the usage in GB
                                                if (sensor_item.disk_category == 0 || sensor_item.disk_category == 1)
                                                    drive_usage = drive_total_space_gb - drive_free_space_gb;
                                                else
                                                    drive_usage = Device_Information.Hardware.Drive_Usage(sensor_item.disk_category, drive_name);

                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "disk_specification", $"Drive: {drive_name}, Total: {drive_total_space_gb} GB, Free: {drive_free_space_gb} GB, Usage: {drive_usage} {specification}");

                                                // 0 = More than X GB occupied, 1 = Less than X GB free, 2 = More than X percent occupied, 3 = Less than X percent free
                                                if (sensor_item.disk_category == 0) // 0 = More than X GB occupied
                                                {
                                                    if (drive_usage > sensor_item.disk_usage && drive_total_space_gb > sensor_item.disk_minimum_capacity)
                                                    {
                                                        triggered = true;

                                                        // if action treshold is reached, execute the action and reset the counter
                                                        if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                                        {
                                                            if (OperatingSystem.IsWindows())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                                            else if (OperatingSystem.IsLinux())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                                            else if (OperatingSystem.IsMacOS())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                                            // Create action history if not exists
                                                            if (String.IsNullOrEmpty(sensor_item.action_history))
                                                            {
                                                                List<string> action_history_list = new List<string>
                                                                {
                                                                    action_result
                                                                };

                                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                            }
                                                            else // if exists, add the result to the list
                                                            {
                                                                List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                                action_history_list.Add(action_result);
                                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                            }

                                                            // Reset the counter
                                                            sensor_item.action_treshold_count = 0;
                                                            WriteEncryptedSensor(sensor, sensor_item);
                                                        }
                                                        else // if not, increment the counter
                                                        {
                                                            sensor_item.action_treshold_count++;
                                                            WriteEncryptedSensor(sensor, sensor_item);
                                                        }

                                                        // Create event
                                                        if (Configuration.Agent.language == "en-US")
                                                        {
                                                            details =
                                                                $"More than the limit of {sensor_item.disk_usage} GB of storage space is being used. Currently, {drive_usage} GB is occupied, leaving {drive_free_space_gb} GB of free space remaining." + Environment.NewLine + Environment.NewLine +
                                                                "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                                "Description: " + sensor_item.description + Environment.NewLine +
                                                                "Type: Drive (more than X GB occupied)" + Environment.NewLine +
                                                                "Time: " + DateTime.Now + Environment.NewLine +
                                                                "Drive: " + drive.Name + Environment.NewLine +
                                                                "Drive size: " + drive_total_space_gb + " (GB)" + Environment.NewLine +
                                                                "Drive free space: " + drive_free_space_gb + " (GB)" + Environment.NewLine +
                                                                "Selected limit: " + sensor_item.disk_usage + $" {specification}" + Environment.NewLine +
                                                                "In usage: " + drive_usage + $" {specification}" + Environment.NewLine +
                                                                "Action result: " + Environment.NewLine + action_result;
                                                        }
                                                        else if (Configuration.Agent.language == "de-DE")
                                                        {
                                                            details =
                                                                $"Es wird mehr als das festgelegte Limit von {sensor_item.disk_usage} GB Speicherplatz genutzt. Aktuell sind {drive_usage} GB belegt, sodass noch {drive_free_space_gb} GB verfügbar sind." + Environment.NewLine + Environment.NewLine +
                                                                "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                                "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                                "Typ: Laufwerk (mehr als X GB belegt)" + Environment.NewLine +
                                                                "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                                "Laufwerk: " + drive.Name + Environment.NewLine +
                                                                "Laufwerksgröße: " + drive_total_space_gb + " (GB)" + Environment.NewLine +
                                                                "Freier Laufwerksspeicher: " + drive_free_space_gb + " (GB)" + Environment.NewLine +
                                                                "Festgelegtes Limit: " + sensor_item.disk_usage + $" {specification}" + Environment.NewLine +
                                                                "In Verwendung: " + drive_usage + $" {specification}" + Environment.NewLine +
                                                                "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                                        }
                                                    }
                                                }
                                                else if (sensor_item.disk_category == 1) // 1 = Less than X GB free
                                                {
                                                    if (drive_usage < sensor_item.disk_usage && drive_total_space_gb > sensor_item.disk_minimum_capacity)
                                                    {
                                                        triggered = true;

                                                        // if action treshold is reached, execute the action and reset the counter
                                                        if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                                        {
                                                            if (OperatingSystem.IsWindows())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                                            else if (OperatingSystem.IsLinux())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                                            else if (OperatingSystem.IsMacOS())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                                            // Create action history if not exists
                                                            if (String.IsNullOrEmpty(sensor_item.action_history))
                                                            {
                                                                List<string> action_history_list = new List<string>
                                                                {
                                                                    action_result
                                                                };

                                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                            }
                                                            else // if exists, add the result to the list
                                                            {
                                                                List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                                action_history_list.Add(action_result);
                                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                            }

                                                            // Reset the counter
                                                            sensor_item.action_treshold_count = 0;
                                                            WriteEncryptedSensor(sensor, sensor_item);
                                                        }
                                                        else // if not, increment the counter
                                                        {
                                                            sensor_item.action_treshold_count++;
                                                            WriteEncryptedSensor(sensor, sensor_item);
                                                        }

                                                        // Create event
                                                        if (Configuration.Agent.language == "en-US")
                                                        {
                                                            details =
                                                                $"Less than the limit of {sensor_item.disk_usage} GB of storage space is available. Currently, {drive_usage} GB is occupied, leaving {drive_free_space_gb} GB of free space remaining." + Environment.NewLine + Environment.NewLine +
                                                                "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                                "Description: " + sensor_item.description + Environment.NewLine +
                                                                "Type: Drive (less than X GB free)" + Environment.NewLine +
                                                                "Time: " + DateTime.Now + Environment.NewLine +
                                                                "Drive: " + drive.Name + Environment.NewLine +
                                                                "Drive size: " + drive_total_space_gb + " (GB)" + Environment.NewLine +
                                                                "Drive free space: " + drive_free_space_gb + " (GB)" + Environment.NewLine +
                                                                "Selected limit: " + sensor_item.disk_usage + $" {specification}" + Environment.NewLine +
                                                                "Free: " + drive_usage + $" {specification}" + Environment.NewLine +
                                                                "Action result: " + Environment.NewLine + action_result;
                                                        }
                                                        else if (Configuration.Agent.language == "de-DE")
                                                        {
                                                            details =
                                                                $"Weniger als der Grenzwert von {sensor_item.disk_usage} GB an Speicherplatz ist verfügbar. Aktuell sind {drive_usage} GB belegt, sodass noch {drive_free_space_gb} GB verfügbar sind." + Environment.NewLine + Environment.NewLine +
                                                                "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                                "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                                "Typ: Laufwerk (weniger als X GB frei)" + Environment.NewLine +
                                                                "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                                "Laufwerk: " + drive.Name + Environment.NewLine +
                                                                "Laufwerksgröße: " + drive_total_space_gb + " (GB)" + Environment.NewLine +
                                                                "Freier Platz auf dem Laufwerk: " + drive_free_space_gb + " (GB)" + Environment.NewLine +
                                                                "Festgelegtes Limit: " + sensor_item.disk_usage + $" {specification}" + Environment.NewLine +
                                                                "Frei: " + drive_usage + $" {specification}" + Environment.NewLine +
                                                                "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                                        }
                                                    }
                                                }
                                                else if (sensor_item.disk_category == 2) // 2 = More than X percent occupied
                                                {
                                                    if (drive_usage > sensor_item.disk_usage && drive_total_space_gb > sensor_item.disk_minimum_capacity)
                                                    {
                                                        triggered = true;

                                                        Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "disk_category_2", "name: " + drive_name + " triggered: " + true.ToString());

                                                        // if action treshold is reached, execute the action and reset the counter
                                                        if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                                        {
                                                            if (OperatingSystem.IsWindows())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                                            else if (OperatingSystem.IsLinux())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                                            else if (OperatingSystem.IsMacOS())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                                            // Create action history if not exists
                                                            if (String.IsNullOrEmpty(sensor_item.action_history))
                                                            {
                                                                List<string> action_history_list = new List<string>
                                                                {
                                                                    action_result
                                                                };

                                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                            }
                                                            else // if exists, add the result to the list
                                                            {
                                                                List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                                action_history_list.Add(action_result);
                                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                            }

                                                            // Reset the counter
                                                            sensor_item.action_treshold_count = 0;
                                                            WriteEncryptedSensor(sensor, sensor_item);
                                                        }
                                                        else // if not, increment the counter
                                                        {
                                                            sensor_item.action_treshold_count++;
                                                            WriteEncryptedSensor(sensor, sensor_item);
                                                        }

                                                        // Create event
                                                        if (Configuration.Agent.language == "en-US")
                                                        {
                                                            details =
                                                                $"More than the limit of {sensor_item.disk_usage}% of storage space is being used. Currently, {drive_usage}% is occupied, leaving {drive_free_space_gb} GB of free space remaining." + Environment.NewLine + Environment.NewLine +
                                                                "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                                "Description: " + sensor_item.description + Environment.NewLine +
                                                                "Type: Drive (more than X percent occupied)" + Environment.NewLine +
                                                                "Time: " + DateTime.Now + Environment.NewLine +
                                                                "Drive: " + drive.Name + Environment.NewLine +
                                                                "Drive size: " + drive_total_space_gb + " (GB)" + Environment.NewLine +
                                                                "Drive free space: " + drive_free_space_gb + " (GB)" + Environment.NewLine +
                                                                "Selected limit: " + sensor_item.disk_usage + $" {specification}" + Environment.NewLine +
                                                                "In usage: " + drive_usage + $" {specification}" + Environment.NewLine +
                                                                "Action result: " + Environment.NewLine + action_result;
                                                        }
                                                        else if (Configuration.Agent.language == "de-DE")
                                                        {
                                                            details =
                                                                $"Es wird mehr als das festgelegte Limit von {sensor_item.disk_usage}% Speicherplatz genutzt. Aktuell sind {drive_usage}% belegt, sodass noch {drive_free_space_gb} GB verfügbar sind." + Environment.NewLine + Environment.NewLine +
                                                                "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                                "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                                "Typ: Laufwerk (mehr als X Prozent belegt)" + Environment.NewLine +
                                                                "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                                "Laufwerk: " + drive.Name + Environment.NewLine +
                                                                "Laufwerksgröße: " + drive_total_space_gb + " (GB)" + Environment.NewLine +
                                                                "Freier Platz auf dem Laufwerk: " + drive_free_space_gb + " (GB)" + Environment.NewLine +
                                                                "Festgelegtes Limit: " + sensor_item.disk_usage + $" {specification}" + Environment.NewLine +
                                                                "In Verwendung: " + drive_usage + $" {specification}" + Environment.NewLine +
                                                                "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                                        }
                                                    }
                                                }
                                                else if (sensor_item.disk_category == 3) // 3 = Less than X percent free
                                                {
                                                    if (drive_usage < sensor_item.disk_usage && drive_total_space_gb > sensor_item.disk_minimum_capacity)
                                                    {
                                                        triggered = true;

                                                        // if action treshold is reached, execute the action and reset the counter
                                                        if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                                        {
                                                            if (OperatingSystem.IsWindows())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                                            else if (OperatingSystem.IsLinux())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                                            else if (OperatingSystem.IsMacOS())
                                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                                            // Create action history if not exists
                                                            if (String.IsNullOrEmpty(sensor_item.action_history))
                                                            {
                                                                List<string> action_history_list = new List<string>
                                                                {
                                                                    action_result
                                                                };

                                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                            }
                                                            else // if exists, add the result to the list
                                                            {
                                                                List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                                action_history_list.Add(action_result);
                                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                            }

                                                            // Reset the counter
                                                            sensor_item.action_treshold_count = 0;
                                                            WriteEncryptedSensor(sensor, sensor_item);
                                                        }
                                                        else // if not, increment the counter
                                                        {
                                                            sensor_item.action_treshold_count++;
                                                            WriteEncryptedSensor(sensor, sensor_item);
                                                        }


                                                        // Create event
                                                        if (Configuration.Agent.language == "en-US")
                                                        {
                                                            details =
                                                                $"Less than the limit of {sensor_item.disk_usage}% of storage space is available. Leaving {drive_free_space_gb} GB of free space remaining." + Environment.NewLine + Environment.NewLine +
                                                                "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                                "Description: " + sensor_item.description + Environment.NewLine +
                                                                "Type: Drive (less than X percent free)" + Environment.NewLine +
                                                                "Time: " + DateTime.Now + Environment.NewLine +
                                                                "Drive: " + drive.Name + Environment.NewLine +
                                                                "Drive size: " + drive_total_space_gb + " (GB)" + Environment.NewLine +
                                                                "Drive free space: " + drive_free_space_gb + " (GB)" + Environment.NewLine +
                                                                "Selected limit: " + sensor_item.disk_usage + $" {specification}" + Environment.NewLine +
                                                                "Free: " + drive_usage + $" {specification}" + Environment.NewLine +
                                                                "Action result: " + Environment.NewLine + action_result;
                                                        }
                                                        else if (Configuration.Agent.language == "de-DE")
                                                        {
                                                            details =
                                                                $"Weniger als der Grenzwert von {sensor_item.disk_usage}% an Speicherplatz ist verfügbar. Sodass noch {drive_free_space_gb} GB verfügbar sind." + Environment.NewLine + Environment.NewLine +
                                                                "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                                "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                                "Typ: Laufwerk (weniger als X Prozent frei)" + Environment.NewLine +
                                                                "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                                "Laufwerk: " + drive.Name + Environment.NewLine +
                                                                "Laufwerksgröße: " + drive_total_space_gb + " (GB)" + Environment.NewLine +
                                                                "Freier Platz auf dem Laufwerk: " + drive_free_space_gb + " (GB)" + Environment.NewLine +
                                                                "Festgelegtes Limit: " + sensor_item.disk_usage + $" {specification}" + Environment.NewLine +
                                                                "Frei: " + drive_usage + $" {specification}" + Environment.NewLine +
                                                                "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    if (sensor_item.sub_category == 3) // Process cpu utilization (%)
                                    {
                                        //foreach process
                                        foreach (Process process in Process.GetProcesses())
                                        {
                                            if (process.ProcessName.ToLower() != sensor_item.process_name.Replace(".exe", "").ToLower()) // Check if the process name is the same, replace .exe to catch user fails
                                                continue;

                                            resource_usage = Device_Information.Processes.Get_CPU_Usage_By_ID(process.Id);

                                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "process cpu utilization", "name: " + sensor_item.name + " id: " + sensor_item.id);

                                            if (resource_usage > sensor_item.cpu_usage)
                                            {
                                                triggered = true;

                                                //Logging.Handler.Sensors("Sensors.Time_Scheduler.Check_Execution", "process cpu utilization sensor triggered", "name: " + sensor_item.name + " id: " + sensor_item.id);

                                                int ram = Device_Information.Processes.Get_RAM_Usage_By_ID(process.Id, false);
                                                string user = Device_Information.Processes.Process_Owner(process);
                                                string created = process.StartTime.ToString();
                                                string path = process.MainModule.FileName;
                                                string cmd = "-";

                                                if (OperatingSystem.IsWindows())
                                                    cmd = Windows.Helper.WMI.Search("root\\cimv2", "SELECT * FROM Win32_Process WHERE ProcessId = " + process.Id, "CommandLine");

                                                Process_Information proc_info = new Process_Information
                                                {
                                                    id = process.Id,
                                                    name = process.ProcessName,
                                                    cpu = resource_usage.ToString(),
                                                    ram = ram.ToString(),
                                                    user = user,
                                                    created = created,
                                                    path = path,
                                                    cmd = cmd
                                                };

                                                process_information_list.Add(proc_info);

                                                // if action treshold is reached, execute the action and reset the counter
                                                if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                                {
                                                    if (OperatingSystem.IsWindows())
                                                        action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                                    else if (OperatingSystem.IsLinux())
                                                        action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                                    else if (OperatingSystem.IsMacOS())
                                                        action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                                    // Create action history if not exists
                                                    if (String.IsNullOrEmpty(sensor_item.action_history))
                                                    {
                                                        List<string> action_history_list = new List<string>
                                                        {
                                                            action_result
                                                        };

                                                        sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                    }
                                                    else // if exists, add the result to the list
                                                    {
                                                        List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                        action_history_list.Add(action_result);
                                                        sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                    }

                                                // Reset the counter
                                                sensor_item.action_treshold_count = 0;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }
                                            else // if not, increment the counter
                                            {
                                                sensor_item.action_treshold_count++;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }

                                            // Create event
                                            if (Configuration.Agent.language == "en-US")
                                            {
                                                details =
                                                    $"The process utilization of {sensor_item.process_name} exceeds the threshold value. The current utilization is {resource_usage}%. The defined limit is {sensor_item.cpu_usage}%. The ram usage is {ram} (MB) & the owner of the process is {user}." + Environment.NewLine + Environment.NewLine +
                                                        "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                        "Description: " + sensor_item.description + Environment.NewLine +
                                                        "Type: Process CPU usage (%)" + Environment.NewLine +
                                                        "Time: " + DateTime.Now + Environment.NewLine +
                                                        "Process name: " + sensor_item.process_name + " (" + process.Id + ")" + Environment.NewLine +
                                                        "Selected limit: " + sensor_item.cpu_usage + " (%)" + Environment.NewLine +
                                                        "In usage: " + resource_usage + " (%)" + Environment.NewLine +
                                                        "Ram usage: " + ram + " (MB)" + Environment.NewLine +
                                                        "User: " + user + Environment.NewLine +
                                                        "Commandline: " + cmd + Environment.NewLine +
                                                        "Action result: " + Environment.NewLine + action_result;
                                                }
                                                else if (Configuration.Agent.language == "de-DE")
                                                {
                                                    details =
                                                        $"Die Prozessauslastung von {sensor_item.process_name} überschreitet den Schwellenwert. Die aktuelle Auslastung beträgt {resource_usage}%. Der definierte Grenzwert ist {sensor_item.cpu_usage}%. Die Ram-Auslastung beträgt {ram} (MB) & der Besitzer des Prozesses ist {user}." + Environment.NewLine + Environment.NewLine +
                                                        "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                        "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                        "Typ: Prozess-CPU-Nutzung" + Environment.NewLine +
                                                        "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                        "Prozess Name: " + sensor_item.process_name + " (%)" + Environment.NewLine +
                                                        "Festgelegtes Limit: " + sensor_item.cpu_usage + " (%)" + Environment.NewLine +
                                                        "In Verwendung: " + resource_usage + " (%)" + Environment.NewLine +
                                                        "Ram Nutzung: " + ram + " (MB)" + Environment.NewLine +
                                                        "Benutzer: " + user + Environment.NewLine +
                                                        "Commandline: " + cmd + Environment.NewLine +
                                                        "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                                }
                                            }
                                        }
                                    }
                                    else if (sensor_item.sub_category == 4) // Process ram utilization (%)
                                    {
                                        //foreach process
                                        foreach (Process process in Process.GetProcesses())
                                        {
                                            if (process.ProcessName.ToLower() != sensor_item.process_name.Replace(".exe", "").ToLower()) // Check if the process name is the same, replace .exe to catch user fails
                                                continue;

                                            resource_usage = Device_Information.Processes.Get_RAM_Usage_By_ID(process.Id, true);
                                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "process cpu utilization", "name: " + sensor_item.name + " id: " + sensor_item.id);

                                            if (resource_usage > sensor_item.ram_usage)
                                            {
                                                triggered = true;

                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "process cpu utilization sensor triggered", "name: " + sensor_item.name + " id: " + sensor_item.id);

                                                int ram = Device_Information.Processes.Get_RAM_Usage_By_ID(process.Id, false);
                                                string user = Device_Information.Processes.Process_Owner(process);
                                                string created = process.StartTime.ToString();
                                                string path = process.MainModule.FileName;
                                                string cmd = "-";

                                                if (OperatingSystem.IsWindows())
                                                    cmd = Windows.Helper.WMI.Search("root\\cimv2", "SELECT * FROM Win32_Process WHERE ProcessId = " + process.Id, "CommandLine");

                                                Process_Information proc_info = new Process_Information
                                                {
                                                    id = process.Id,
                                                    name = process.ProcessName,
                                                    cpu = resource_usage.ToString(),
                                                    ram = ram.ToString(),
                                                    user = user,
                                                    created = created,
                                                    path = path,
                                                    cmd = cmd
                                                };

                                                process_information_list.Add(proc_info);

                                                // if action treshold is reached, execute the action and reset the counter
                                                if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                                {
                                                    if (OperatingSystem.IsWindows())
                                                        action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                                    else if (OperatingSystem.IsLinux())
                                                        action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                                    else if (OperatingSystem.IsMacOS())
                                                        action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                                    // Create action history if not exists
                                                    if (String.IsNullOrEmpty(sensor_item.action_history))
                                                    {
                                                        List<string> action_history_list = new List<string>
                                                        {
                                                            action_result
                                                        };

                                                        sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                    }
                                                    else // if exists, add the result to the list
                                                    {
                                                        List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                        action_history_list.Add(action_result);
                                                        sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                    }

                                                // Reset the counter
                                                sensor_item.action_treshold_count = 0;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }
                                            else // if not, increment the counter
                                            {
                                                sensor_item.action_treshold_count++;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }

                                            // Create event
                                            if (Configuration.Agent.language == "en-US")
                                            {
                                                details =
                                                    $"The memory utilization of {sensor_item.process_name} exceeds the threshold value. The current utilization is {resource_usage}%. The defined limit is {sensor_item.ram_usage}%. The ram usage is {ram} (MB) & the owner of the process is {user}." + Environment.NewLine + Environment.NewLine +
                                                        "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                        "Description: " + sensor_item.description + Environment.NewLine +
                                                        "Type: Process RAM usage (%)" + Environment.NewLine +
                                                        "Time: " + DateTime.Now + Environment.NewLine +
                                                        "Process name: " + sensor_item.process_name + " (" + process.Id + ")" + Environment.NewLine +
                                                        "Selected limit: " + sensor_item.ram_usage + " (%)" + Environment.NewLine +
                                                        "In usage: " + resource_usage + " (%)" + Environment.NewLine +
                                                        "Ram usage: " + ram + " (MB)" + Environment.NewLine +
                                                        "User: " + user + Environment.NewLine +
                                                        "Commandline: " + cmd + Environment.NewLine +
                                                        "Action result: " + Environment.NewLine + action_result;
                                                }
                                                else if (Configuration.Agent.language == "de-DE")
                                                {
                                                    details =
                                                        $"Die Speicherauslastung von {sensor_item.process_name} überschreitet den Schwellenwert. Die aktuelle Auslastung beträgt {resource_usage}%. Der definierte Grenzwert ist {sensor_item.ram_usage}%. Die Ram-Auslastung beträgt {ram} (MB) & der Besitzer des Prozesses ist {user}." + Environment.NewLine + Environment.NewLine +
                                                        "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                        "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                        "Typ: Prozess-RAM-Nutzung (%)" + Environment.NewLine +
                                                        "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                        "Prozess Name: " + sensor_item.process_name + " (%)" + Environment.NewLine +
                                                        "Festgelegtes Limit: " + sensor_item.ram_usage + " (%)" + Environment.NewLine +
                                                        "In Verwendung: " + resource_usage + " (%)" + Environment.NewLine +
                                                        "Ram Nutzung: " + ram + " (MB)" + Environment.NewLine +
                                                        "Benutzer: " + user + Environment.NewLine +
                                                        "Commandline: " + cmd + Environment.NewLine +
                                                        "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                                }
                                            }
                                        }
                                    }
                                    else if (sensor_item.sub_category == 5) // Process ram utilization (MB)
                                    {
                                        //foreach process
                                        foreach (Process process in Process.GetProcesses())
                                        {
                                            if (process.ProcessName.ToLower() != sensor_item.process_name.Replace(".exe", "").ToLower()) // Check if the process name is the same, replace .exe to catch user fails
                                                continue;

                                            resource_usage = Device_Information.Processes.Get_RAM_Usage_By_ID(process.Id, false);
                                            //Logging.Handler.Sensors("Sensors.Time_Scheduler.Check_Execution", "process cpu utilization", "name: " + sensor_item.name + " id: " + sensor_item.id);

                                            if (resource_usage > sensor_item.ram_usage)
                                            {
                                                triggered = true;

                                                //Logging.Handler.Sensors("Sensors.Time_Scheduler.Check_Execution", "process cpu utilization sensor triggered", "name: " + sensor_item.name + " id: " + sensor_item.id);

                                                int ram = resource_usage;
                                                string user = Device_Information.Processes.Process_Owner(process);
                                                string created = process.StartTime.ToString();
                                                string path = process.MainModule.FileName;
                                                string cmd = "-";

                                                if (OperatingSystem.IsWindows())
                                                    cmd = Windows.Helper.WMI.Search("root\\cimv2", "SELECT * FROM Win32_Process WHERE ProcessId = " + process.Id, "CommandLine");

                                                Process_Information proc_info = new Process_Information
                                                {
                                                    id = process.Id,
                                                    name = process.ProcessName,
                                                    cpu = resource_usage.ToString(),
                                                    ram = ram.ToString(),
                                                    user = user,
                                                    created = created,
                                                    path = path,
                                                    cmd = cmd
                                                };

                                                process_information_list.Add(proc_info);

                                                // if action treshold is reached, execute the action and reset the counter
                                                if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                                {
                                                    if (OperatingSystem.IsWindows())
                                                        action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                                    else if (OperatingSystem.IsLinux())
                                                        action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                                    else if (OperatingSystem.IsMacOS())
                                                        action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                                    // Create action history if not exists
                                                    if (String.IsNullOrEmpty(sensor_item.action_history))
                                                    {
                                                        List<string> action_history_list = new List<string>
                                                        {
                                                            action_result
                                                        };

                                                        sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                    }
                                                    else // if exists, add the result to the list
                                                    {
                                                        List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                        action_history_list.Add(action_result);
                                                        sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                    }

                                                // Reset the counter
                                                sensor_item.action_treshold_count = 0;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }
                                            else // if not, increment the counter
                                            {
                                                sensor_item.action_treshold_count++;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }

                                            // Create event
                                            if (Configuration.Agent.language == "en-US")
                                            {
                                                details =
                                                    $"The memory utilization of {sensor_item.process_name} exceeds the threshold value. The current utilization is {resource_usage} (MB). The defined limit is {sensor_item.ram_usage} (MB). The owner of the process is {user}." + Environment.NewLine + Environment.NewLine +
                                                        "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                        "Description: " + sensor_item.description + Environment.NewLine +
                                                        "Type: Process RAM usage (MB)" + Environment.NewLine +
                                                        "Time: " + DateTime.Now + Environment.NewLine +
                                                        "Process name: " + sensor_item.process_name + " (" + process.Id + ")" + Environment.NewLine +
                                                        "Selected limit: " + sensor_item.ram_usage + " (MB)" + Environment.NewLine +
                                                        "In usage: " + resource_usage + " (MB)" + Environment.NewLine +
                                                        "User: " + user + Environment.NewLine +
                                                        "Commandline: " + cmd + Environment.NewLine +
                                                        "Action result: " + Environment.NewLine + action_result;
                                                }
                                                else if (Configuration.Agent.language == "de-DE")
                                                {
                                                    details =
                                                        $"Die Speicherauslastung von {sensor_item.process_name} überschreitet den Schwellenwert. Die aktuelle Auslastung beträgt {resource_usage} (MB). Der definierte Grenzwert ist {sensor_item.ram_usage} (MB). Der Besitzer des Prozesses ist {user}." + Environment.NewLine + Environment.NewLine +
                                                        "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                        "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                        "Typ: Prozess-RAM-Nutzung (MB)" + Environment.NewLine +
                                                        "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                        "Prozess Name: " + sensor_item.process_name + " (%)" + Environment.NewLine +
                                                        "Festgelegtes Limit: " + sensor_item.ram_usage + " (MB)" + Environment.NewLine +
                                                        "In Verwendung: " + resource_usage + " (MB)" + Environment.NewLine +
                                                        "Benutzer: " + user + Environment.NewLine +
                                                        "Commandline: " + cmd + Environment.NewLine +
                                                        "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                                }
                                            }
                                        }
                                    }
                                }
                                else if (sensor_item.category == 1) // Windows Eventlog
                                {
                                    bool event_log_existing = false;
                                    bool action_already_executed = false; // prevents the action from being executed multiple times

                                    EventLogQuery query;
                                    EventLogReader reader = null;
                                    EventRecord eventRecord;

                                    try
                                    {
                                        // Filter by time range and event ID
                                        query = new EventLogQuery(sensor_item.eventlog, PathType.LogName, string.Format("*[System[(EventID={0}) and TimeCreated[@SystemTime >= '{1}'] and TimeCreated[@SystemTime <= '{2}']]]", sensor_item.eventlog_event_id, startTime.ToUniversalTime().ToString("o"), endTime.ToUniversalTime().ToString("o")));

                                        reader = new EventLogReader(query);

                                        event_log_existing = true;

                                        Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Check event log existence (" + sensor_item.eventlog + ")", event_log_existing.ToString());

                                        // Read each event from event logs
                                        while ((eventRecord = reader.ReadEvent()) != null)
                                        {
                                            //Logging.Handler.Sensors("Sensors.Time_Scheduler.Check_Execution", "Found events", eventRecord.Id.ToString());

                                            if (DateTime.Parse(sensor_item.last_run) > eventRecord.TimeCreated)
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Check event time (" + eventRecord.TimeCreated + ")", "Last scan is newer than last event log.");
                                            }
                                            else
                                            {
                                                // Print current scanning event log
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Scan event", "EventID: " + eventRecord.Id.ToString() + " Timestamp: " + eventRecord.TimeCreated.Value.ToString() + " lastscan: " + sensor_item.last_run);

                                                string result = "-";
                                                string content = eventRecord.FormatDescription();
                                                bool regex_match = false;

                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "event log content", content);

                                                if (String.IsNullOrEmpty(content))
                                                {
                                                    content = eventRecord.ToXml();

                                                    // code below is not reliable enough in current state. Maybe we can use it in the future.
                                                    //XmlDocument doc = new XmlDocument();
                                                    //doc.LoadXml(eventRecord.ToXml());

                                                    //XmlNode messageNode = doc.SelectSingleNode("//Event/EventData/Data[@Name='Message']");
                                                    //content = messageNode?.InnerText ?? "N/A";

                                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "event log content xml extracted", content);
                                                }

                                                if (String.IsNullOrEmpty(sensor_item.expected_result))
                                                {
                                                    // Increment notification counter for each hit
                                                    sensor_item.notification_treshold_count++;

                                                    triggered = true;
                                                }
                                                else if (Regex.IsMatch(content, sensor_item.expected_result))
                                                {
                                                    // Increment notification counter for each hit
                                                    sensor_item.notification_treshold_count++;

                                                    triggered = true;
                                                }

                                                // if action treshold is reached, execute the action and reset the counter
                                                if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                                {
                                                    if (!action_already_executed)
                                                    {
                                                        if (OperatingSystem.IsWindows())
                                                            action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);

                                                        action_already_executed = true;
                                                    }

                                                    // Create action history if not exists
                                                    if (String.IsNullOrEmpty(sensor_item.action_history))
                                                    {
                                                        List<string> action_history_list = new List<string>
                                                        {
                                                            action_result
                                                        };

                                                        sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                    }
                                                    else // if exists, add the result to the list
                                                    {
                                                        List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                        action_history_list.Add(action_result);
                                                        sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                                    }

                                                // Reset the counter
                                                sensor_item.action_treshold_count = 0;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }
                                            else // if not, increment the counter
                                            {
                                                sensor_item.action_treshold_count++;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }

                                            // Create event
                                            if (Configuration.Agent.language == "en-US")
                                            {
                                                details =
                                                    $"The check of event log {sensor_item.eventlog} by event ID {sensor_item.eventlog_event_id} resulted in a hit for the expected result {sensor_item.expected_result}." + Environment.NewLine + Environment.NewLine +
                                                        "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                        "Description: " + sensor_item.description + Environment.NewLine +
                                                        "Type: Windows Eventlog" + Environment.NewLine +
                                                        "Time: " + DateTime.Now + Environment.NewLine +
                                                        "Eventlog: " + sensor_item.eventlog + Environment.NewLine +
                                                        "Event id: " + sensor_item.eventlog_event_id + Environment.NewLine +
                                                        "Expected result: " + sensor_item.expected_result + Environment.NewLine +
                                                        "Content: " + content + Environment.NewLine +
                                                        "Level: " + eventRecord.Level + Environment.NewLine +
                                                        "Process id: " + eventRecord.ProcessId + Environment.NewLine +
                                                        "Created: " + eventRecord.TimeCreated + Environment.NewLine +
                                                        "User id: " + eventRecord.UserId + Environment.NewLine +
                                                        "Version: " + eventRecord.Version + Environment.NewLine +
                                                        "Action result: " + Environment.NewLine + action_result;
                                                }
                                                else if (Configuration.Agent.language == "de-DE")
                                                {
                                                    details =
                                                        $"Die Prüfung von Eventlog {sensor_item.eventlog} nach Event ID {sensor_item.eventlog_event_id} ergab einen Treffer für das erwartete Ergebnis {sensor_item.expected_result}." + Environment.NewLine + Environment.NewLine +
                                                        "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                        "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                        "Typ: Windows Eventlog" + Environment.NewLine +
                                                        "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                        "Eventlog: " + sensor_item.eventlog + Environment.NewLine +
                                                        "Event ID: " + sensor_item.eventlog_event_id + Environment.NewLine +
                                                        "Erwartetes Ergebnis: " + sensor_item.expected_result + Environment.NewLine +
                                                        "Inhalt: " + content + Environment.NewLine +
                                                        "Level: " + eventRecord.Level + Environment.NewLine +
                                                        "Prozess ID: " + eventRecord.ProcessId + Environment.NewLine +
                                                        "Erstellt: " + eventRecord.TimeCreated + Environment.NewLine +
                                                        "Benutzer ID: " + eventRecord.UserId + Environment.NewLine +
                                                        "Version: " + eventRecord.Version + Environment.NewLine +
                                                        "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                                }


                                                //Trigger incident response | NetLock legacy code
                                                /*if (sensor["incident_response_ruleset"].ToString() != "LQ==")
                                                {
                                                    //Trigger it
                                                    Incident_Response.Handler.Get_Incident_Response_Ruleset(sensor["incident_response_ruleset"].ToString());
                                                }*/
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Check event log existence (" + sensor_item.eventlog + ")", event_log_existing.ToString() + " error: " + ex.ToString());
                                    }
                                }
                                else if (sensor_item.category == 2 || sensor_item.category == 5 || sensor_item.category == 6) // PowerShell, Linux Bash or MacOS Zsh
                                {
                                    string result = "-";

                                    if (sensor_item.category == 2)
                                        result = Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script);
                                    else if (sensor_item.category == 5)
                                        result = Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script);
                                    else if (sensor_item.category == 6)
                                        result = MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script);

                                    if (Regex.IsMatch(result, sensor_item.expected_result))
                                    {
                                        triggered = true;

                                        // if action treshold is reached, execute the action and reset the counter
                                        if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                        {
                                            if (OperatingSystem.IsWindows())
                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                            else if (OperatingSystem.IsLinux())
                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                            else if (OperatingSystem.IsMacOS())
                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                            // Create action history if not exists
                                            if (String.IsNullOrEmpty(sensor_item.action_history))
                                            {
                                                List<string> action_history_list = new List<string>
                                                {
                                                    action_result
                                                };

                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                            }
                                            else // if exists, add the result to the list
                                            {
                                                List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                action_history_list.Add(action_result);
                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                            }

                                        // Reset the counter
                                        sensor_item.action_treshold_count = 0;
                                        WriteEncryptedSensor(sensor, sensor_item);
                                    }
                                    else // if not, increment the counter
                                    {
                                        sensor_item.action_treshold_count++;
                                        WriteEncryptedSensor(sensor, sensor_item);
                                    }

                                    string details_type_specification = String.Empty;

                                        if (sensor_item.category == 2)
                                            details_type_specification = "PowerShell";
                                        else if (sensor_item.category == 5)
                                            details_type_specification = "Bash";
                                        else if (sensor_item.category == 6)
                                            details_type_specification = "Zsh";

                                        // Create event
                                        if (Configuration.Agent.language == "en-US")
                                        {
                                            details =
                                                $"The script execution of {sensor_item.name} resulted in a hit for the expected result {sensor_item.expected_result}." + Environment.NewLine + Environment.NewLine +
                                                "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                "Description: " + sensor_item.description + Environment.NewLine +
                                                $"Type: {details_type_specification}" + Environment.NewLine +
                                                "Time: " + DateTime.Now + Environment.NewLine +
                                                "Script: " + sensor_item.script + Environment.NewLine +
                                                "Pattern: " + sensor_item.expected_result + Environment.NewLine +
                                                "Result: " + result + Environment.NewLine +
                                                "Action result: " + Environment.NewLine + action_result;
                                        }
                                        else if (Configuration.Agent.language == "de-DE")
                                        {
                                            details =
                                                $"Die Skriptausführung von {sensor_item.name} ergab einen Treffer für das erwartete Ergebnis {sensor_item.expected_result}." + Environment.NewLine + Environment.NewLine +
                                                "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                $"Typ: {details_type_specification}" + Environment.NewLine +
                                                "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                "Skript: " + sensor_item.script + Environment.NewLine +
                                                "Pattern: " + sensor_item.expected_result + Environment.NewLine +
                                                "Ergebnis: " + result + Environment.NewLine +
                                                "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                        }
                                    }
                                }
                                else if (sensor_item.category == 3) // Service
                                {
                                    bool service_start_failed = false;
                                    string service_error_message = String.Empty;
                                    string service_status = String.Empty;

                                    try
                                    {
                                        if (OperatingSystem.IsWindows())
                                        {
                                            ServiceController sc = new ServiceController(sensor_item.service_name);
                                            service_status = sc.Status.Equals(ServiceControllerStatus.Paused).ToString();

                                            if (sensor_item.service_condition == 0 && sc.Status.Equals(ServiceControllerStatus.Running)) // if service is running and condition is 0 = running
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is running", sensor_item.service_name + " " + sc.Status.ToString());

                                                triggered = true;

                                                if (sensor_item.service_action == 1) // stop the service if it's running and the action is 1 = stop
                                                    sc.Stop();
                                            }
                                            else if (sensor_item.service_condition == 1 && sc.Status.Equals(ServiceControllerStatus.Paused)) // if service is paused and condition is 1 = paused
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is paused", sensor_item.service_name + " " + sc.Status.ToString());

                                                triggered = true;

                                                if (sensor_item.service_action == 2) // restart the service if it's paused and the action is 2 = restart
                                                {
                                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is paused, restarting", sensor_item.service_name + " " + sc.Status.ToString());

                                                    sc.Stop();
                                                    sc.WaitForStatus(ServiceControllerStatus.Stopped);
                                                    sc.Start();
                                                }
                                            }
                                            else if (sensor_item.service_condition == 2 && sc.Status.Equals(ServiceControllerStatus.Stopped)) // if service is stopped and condition is 2 = stopped
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is stopped", sensor_item.service_name + " " + sc.Status.ToString());

                                                triggered = true;

                                                if (sensor_item.service_action == 0) // start the service if it's stopped and the action is 0 = start
                                                    sc.Start();
                                            }
                                        }
                                        else if (OperatingSystem.IsLinux()) // marked for refactoring for cleaner code
                                        {
                                            sensor_item.service_name = sensor_item.service_name.Replace(".service", ""); // remove .service from the service name
                                            string serviceCommand = $"systemctl list-units --type=service --all | grep -w {sensor_item.service_name}.service";

                                            // Execute bash script and save the output
                                            string output = Linux.Helper.Bash.Execute_Script("Service Sensor", false, serviceCommand);

                                            if (string.IsNullOrWhiteSpace(output))
                                            {
                                                //Console.WriteLine($"Service {sensor_item.service_name} not found or no output received.");
                                                continue;
                                            }

                                            bool isServiceRunning = false;

                                            // Check status
                                            // Use Regex to match running status
                                            var match = Regex.Match(output, $@"running", RegexOptions.Multiline);

                                            if (match.Success)
                                                isServiceRunning = true;

                                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service status", sensor_item.service_name + " " + isServiceRunning.ToString());

                                            if (sensor_item.service_condition == 0 && isServiceRunning) // if service is running and condition is 0 = running
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is running", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                triggered = true;

                                                if (sensor_item.service_action == 1) // stop the service if it's running and the action is 1 = stop
                                                {
                                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is running, stopping", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                    // Stoppe den Dienst
                                                    Linux.Helper.Bash.Execute_Script("", false, $"systemctl stop {sensor_item.service_name}");
                                                }
                                            }
                                            else if (sensor_item.service_condition == 1 && !isServiceRunning) // if service is stopped and condition is 1 = paused (simuliert als gestoppt)
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is stopped", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                triggered = true;

                                                if (sensor_item.service_action == 2) // restart the service if it's stopped and the action is 2 = restart
                                                {
                                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is stopped, restarting", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                    // Starte den Dienst
                                                    Linux.Helper.Bash.Execute_Script("Sensor", false, $"systemctl start {sensor_item.service_name}");
                                                }
                                            }
                                            else if (sensor_item.service_condition == 2 && !isServiceRunning) // if service is stopped and condition is 2 = stopped
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is stopped", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                triggered = true;

                                                if (sensor_item.service_action == 0) // start the service if it's stopped and the action is 0 = start
                                                {
                                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is stopped, starting", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                    // Starte den Dienst
                                                    Linux.Helper.Bash.Execute_Script("Sensor", false, $"systemctl start {sensor_item.service_name}");
                                                }
                                            }
                                        }
                                        else if (OperatingSystem.IsMacOS()) // Currently only supports system wide services
                                        {
                                            string output = MacOS.Helper.Zsh.Execute_Script("Service Sensor", false, $"launchctl list | grep {sensor_item.service_name}");

                                            // Regex, um nur die Zeile für den spezifischen Dienst zu extrahieren
                                            string pattern = $@"^\S+\s+\S+\s+{sensor_item.service_name}$";
                                            var match = Regex.Match(output, pattern, RegexOptions.Multiline);

                                            bool isServiceRunning = false;

                                            if (match.Success)
                                            {
                                                // Extrahiere den PID-Wert oder das `-` am Anfang der Zeile
                                                string[] parts = match.Value.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                                isServiceRunning = parts[0] != "-"; // "-" bedeutet, der Dienst läuft nicht
                                            }

                                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service status", sensor_item.service_name + " " + isServiceRunning.ToString());

                                            if (sensor_item.service_condition == 0 && isServiceRunning) // Wenn der Dienst läuft und die Bedingung 0 ist (sollte laufen)
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is running", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                triggered = true;

                                                if (sensor_item.service_action == 1) // Stoppe den Dienst, wenn die Aktion 1 ist (soll gestoppt werden)
                                                {
                                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is running, stopping", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                    // Stoppe den Dienst
                                                    MacOS.Helper.Zsh.Execute_Script("Service Sensor", false, $"launchctl stop {sensor_item.service_name}.plist");
                                                }
                                            }
                                            else if (sensor_item.service_condition == 1 && !isServiceRunning) // Wenn der Dienst gestoppt ist und die Bedingung 1 ist (simuliert als pausiert)
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is stopped", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                triggered = true;

                                                if (sensor_item.service_action == 2) // Starte den Dienst neu, wenn die Aktion 2 ist
                                                {
                                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is stopped, restarting", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                    // Starte den Dienst neu
                                                    MacOS.Helper.Zsh.Execute_Script("Service Sensor", false, $"launchctl stop {sensor_item.service_name}.plist");
                                                    MacOS.Helper.Zsh.Execute_Script("Service Sensor", false, $"launchctl start {sensor_item.service_name}.plist");
                                                }
                                            }
                                            else if (sensor_item.service_condition == 2 && !isServiceRunning) // Wenn der Dienst gestoppt ist und die Bedingung 2 ist (sollte gestoppt sein)
                                            {
                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is stopped", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                triggered = true;

                                                if (sensor_item.service_action == 0) // Starte den Dienst, wenn die Aktion 0 ist (soll gestartet werden)
                                                {
                                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Service is stopped, starting", sensor_item.service_name + " " + isServiceRunning.ToString());

                                                    // Starte den Dienst
                                                    MacOS.Helper.Zsh.Execute_Script("Service Sensor", false, $"launchctl start {sensor_item.service_name}.plist");
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        service_start_failed = true;
                                        service_error_message = ex.Message;
                                        Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Checking service state, or performing action failed", ex.ToString());
                                    }

                                    if (triggered)
                                    {
                                        // if action treshold is reached, execute the action and reset the counter
                                        if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                        {
                                            if (OperatingSystem.IsWindows())
                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action);
                                            else if (OperatingSystem.IsLinux())
                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);
                                            else if (OperatingSystem.IsMacOS())
                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action);

                                            // Create action history if not exists
                                            if (String.IsNullOrEmpty(sensor_item.action_history))
                                            {
                                                List<string> action_history_list = new List<string>
                                                {
                                                    action_result
                                                };

                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                            }
                                            else // if exists, add the result to the list
                                            {
                                                List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                action_history_list.Add(action_result);
                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                            }

                                        // Reset the counter
                                        sensor_item.action_treshold_count = 0;
                                        WriteEncryptedSensor(sensor, sensor_item);
                                    }
                                    else // if not, increment the counter
                                    {
                                        sensor_item.action_treshold_count++;
                                        WriteEncryptedSensor(sensor, sensor_item);
                                    }

                                    // Create event
                                    if (Configuration.Agent.language == "en-US")
                                    {
                                        // Convert the service condition to human readable text
                                            string service_condition = String.Empty;

                                            if (sensor_item.service_condition == 0)
                                                service_condition = "running";
                                            else if (sensor_item.service_condition == 1)
                                                service_condition = "paused";
                                            else if (sensor_item.service_condition == 2)
                                                service_condition = "stopped";

                                            // Convert the service action to human readable text
                                            string service_action = String.Empty;

                                            if (sensor_item.service_action == 0)
                                                service_action = "start";
                                            else if (sensor_item.service_action == 1)
                                                service_action = "stop";
                                            else if (sensor_item.service_action == 2)
                                                service_action = "restart";

                                            if (service_start_failed)
                                            {
                                                details =
                                                    $"The service {sensor_item.service_name} was {service_condition}. The service action ({service_action}) could not be performed." + Environment.NewLine + Environment.NewLine +
                                                    "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                    "Description: " + sensor_item.description + Environment.NewLine +
                                                    "Type: Service" + Environment.NewLine +
                                                    "Time: " + DateTime.Now + Environment.NewLine +
                                                    "Service: " + sensor_item.service_name + Environment.NewLine +
                                                    "Result: The requested service action could not be performed." + Environment.NewLine +
                                                    "Error: " + service_error_message + Environment.NewLine +
                                                    "Action result: " + Environment.NewLine + action_result;
                                            }
                                            else
                                            {
                                                details =
                                                    $"The service {sensor_item.service_name} was {service_condition}. The service action ({service_action}) was successfully executed." + Environment.NewLine + Environment.NewLine +
                                                    "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                    "Description: " + sensor_item.description + Environment.NewLine +
                                                    "Type: Service" + Environment.NewLine +
                                                    "Time: " + DateTime.Now + Environment.NewLine +
                                                    "Service: " + sensor_item.service_name + Environment.NewLine +
                                                    "Result: The requested service action was successfully executed." + Environment.NewLine +
                                                    "Action result: " + Environment.NewLine + action_result;
                                            }
                                        }
                                        else if (Configuration.Agent.language == "de-DE")
                                        {
                                            // Convert the service condition to human readable text
                                            string service_condition = String.Empty;

                                            if (sensor_item.service_condition == 0)
                                                service_condition = "läuft";
                                            else if (sensor_item.service_condition == 1)
                                                service_condition = "pausiert";
                                            else if (sensor_item.service_condition == 2)
                                                service_condition = "gestoppt";

                                            // Convert the service action to human readable text
                                            string service_action = String.Empty;

                                            if (sensor_item.service_action == 0)
                                                service_action = "starten";
                                            else if (sensor_item.service_action == 1)
                                                service_action = "stoppen";
                                            else if (sensor_item.service_action == 2)
                                                service_action = "neu starten";

                                            if (service_start_failed)
                                            {
                                                details =
                                                    $"Der Dienst {sensor_item.service_name} war {service_condition}. Die Dienstaktion ({service_action}) konnte nicht ausgeführt werden." + Environment.NewLine +
                                                    "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                    "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                    "Typ: Dienst" + Environment.NewLine +
                                                    "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                    "Dienst: " + sensor_item.service_name + Environment.NewLine +
                                                    "Ergebnis: The requested service action could not be performed." + Environment.NewLine +
                                                    "Fehler: " + service_error_message + Environment.NewLine +
                                                    "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                            }
                                            else
                                            {
                                                details =
                                                    $"Der Dienst {sensor_item.service_name} war {service_condition}. Die Dienstaktion ({service_action}) wurde erfolgreich ausgeführt." + Environment.NewLine +
                                                    "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                    "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                    "Typ: Dienst" + Environment.NewLine +
                                                    "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                    "Dienst: " + sensor_item.service_name + Environment.NewLine +
                                                    "Ergebnis: Die gewünschte Dienst Aktion wurde erfolgreich ausgeführt." + Environment.NewLine +
                                                    "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                            }
                                        }
                                    }
                                }
                                else if (sensor_item.category == 4) // Ping
                                {
                                    bool ping_status = Device_Information.Network.Ping(sensor_item.ping_address, sensor_item.ping_timeout);

                                    if (ping_status && sensor_item.ping_condition == 0)
                                        triggered = true;
                                    else if (!ping_status && sensor_item.ping_condition == 1)
                                        triggered = true;

                                    if (triggered)
                                    {
                                        // if action treshold is reached, execute the action and reset the counter
                                        if (sensor_item.action_treshold_count >= sensor_item.action_treshold_max)
                                        {
                                            if (OperatingSystem.IsWindows())
                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Windows.Helper.PowerShell.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, sensor_item.script_action, 0);
                                            else if (OperatingSystem.IsLinux())
                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + Linux.Helper.Bash.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action, 0);
                                            else if (OperatingSystem.IsMacOS())
                                                action_result += Environment.NewLine + Environment.NewLine + " [" + DateTime.Now.ToString() + "]" + Environment.NewLine + MacOS.Helper.Zsh.Execute_Script("Sensors.Time_Scheduler.Check_Execution (execute action) " + sensor_item.name, true, sensor_item.script_action, 0);

                                            // Create action history if not exists
                                            if (String.IsNullOrEmpty(sensor_item.action_history))
                                            {
                                                List<string> action_history_list = new List<string>
                                                {
                                                    action_result
                                                };

                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                            }
                                            else // if exists, add the result to the list
                                            {
                                                List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);
                                                action_history_list.Add(action_result);
                                                sensor_item.action_history = JsonSerializer.Serialize(action_history_list);
                                            }

                                        // Reset the counter
                                        sensor_item.action_treshold_count = 0;
                                        WriteEncryptedSensor(sensor, sensor_item);
                                    }
                                    else // if not, increment the counter
                                    {
                                        sensor_item.action_treshold_count++;
                                        WriteEncryptedSensor(sensor, sensor_item);
                                    }

                                    // Create event
                                    string ping_result = String.Empty;

                                        if (Configuration.Agent.language == "en-US")
                                        {
                                            if (sensor_item.ping_condition == 0)
                                                ping_result = "Successful";
                                            else if (sensor_item.ping_condition == 1)
                                                ping_result = "Failed";

                                            details =
                                                $"The ping check of {sensor_item.ping_address} resulted in a hit for the expected result {ping_result}." + Environment.NewLine + Environment.NewLine +
                                                "Sensor name: " + sensor_item.name + Environment.NewLine +
                                                "Description: " + sensor_item.description + Environment.NewLine +
                                                "Type: Ping" + Environment.NewLine +
                                                "Time: " + DateTime.Now + Environment.NewLine +
                                                "Address: " + sensor_item.ping_address + Environment.NewLine +
                                                "Timeout: " + sensor_item.ping_timeout + Environment.NewLine +
                                                "Result: " + ping_result + Environment.NewLine +
                                                "Action result: " + Environment.NewLine + action_result;
                                        }
                                        else if (Configuration.Agent.language == "de-DE")
                                        {
                                            if (sensor_item.ping_condition == 0)
                                                ping_result = "Erfolgreich";
                                            else if (sensor_item.ping_condition == 1)
                                                ping_result = "Fehlgeschlagen";

                                            details =
                                                $"Der Ping-Check von {sensor_item.ping_address} ergab einen Treffer für das erwartete Ergebnis {ping_result}." + Environment.NewLine + Environment.NewLine +
                                                "Sensor Name: " + sensor_item.name + Environment.NewLine +
                                                "Beschreibung: " + sensor_item.description + Environment.NewLine +
                                                "Typ: Ping" + Environment.NewLine +
                                                "Uhrzeit: " + DateTime.Now + Environment.NewLine +
                                                "Adresse: " + sensor_item.ping_address + Environment.NewLine +
                                                "Timeout: " + sensor_item.ping_timeout + Environment.NewLine +
                                                "Ergebnis: " + ping_result + Environment.NewLine +
                                                "Ergebnis der Aktion: " + Environment.NewLine + action_result;
                                        }
                                    }
                                }

                                // Execution finished, set last run time
                                endTime = DateTime.Now; // set end time for the next scan

                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Sensor executed", "name: " + sensor_item.name + " id: " + sensor_item.id);

                                // Build additional details string
                                foreach (var process in process_information_list)
                                {
                                    if (Configuration.Agent.language == "en-US")
                                        additional_details += Environment.NewLine + "Process ID: " + process.id + Environment.NewLine + "Process name: " + process.name + Environment.NewLine + "Usage: " + process.cpu + " (%)" + Environment.NewLine + "RAM usage: " + process.ram + " (MB)" + Environment.NewLine + "User: " + process.user + Environment.NewLine + "Created: " + process.created + Environment.NewLine + "Path: " + process.path + Environment.NewLine + "Commandline: " + process.cmd + Environment.NewLine;
                                    else if (Configuration.Agent.language == "de-DE")
                                        additional_details += Environment.NewLine + "Prozess ID: " + process.id + Environment.NewLine + "Prozess Name: " + process.name + Environment.NewLine + "Nutzung: " + process.cpu + " (%)" + Environment.NewLine + "RAM Nutzung: " + process.ram + " (MB)" + Environment.NewLine + "Benutzer: " + process.user + Environment.NewLine + "Erstellt: " + process.created + Environment.NewLine + "Pfad: " + process.path + Environment.NewLine + "Commandline: " + process.cmd + Environment.NewLine;
                                }

                                // Insert event
                                // Check already_notified only if suppress_notification is enabled
                                
                                // Debug logging
                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Notification decision", 
                                    $"Sensor: {sensor_item.name}, triggered: {triggered}, already_notified: {sensor_item.already_notified}, suppress_notification: {sensor_item.suppress_notification}, resolved_notification: {sensor_item.resolved_notification}");
                                
                                if (triggered)
                                {
                                    bool should_notify = !sensor_item.suppress_notification || !sensor_item.already_notified;
                                    
                                    if (should_notify) 
                                    {
                                        Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Triggered (id)", triggered.ToString() + " (" + sensor_item.id + ")");
                                        
                                        // Check if job description is empty
                                        if (String.IsNullOrEmpty(sensor_item.description) && Configuration.Agent.language == "en-US")
                                            sensor_item.description = "No description";
                                        else if (String.IsNullOrEmpty(sensor_item.description) && Configuration.Agent.language == "de-DE")
                                            sensor_item.description = "Keine Beschreibung";

                                        // if notification treshold is reached, insert event and reset the counter
                                        if (sensor_item.notification_treshold_count >= sensor_item.notification_treshold_max)
                                        {
                                            // Create action history, if treshold is not 1
                                            if (sensor_item.notification_treshold_max != 1)
                                            {
                                                List<string> action_history_list = JsonSerializer.Deserialize<List<string>>(sensor_item.action_history);

                                                foreach (var action_history_item in action_history_list)
                                                    action_history += Environment.NewLine + action_history_item + Environment.NewLine;

                                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "action_history", action_history + " (" + sensor_item.id + ")");

                                                // Clear action history
                                                action_history_list.Clear();
                                                sensor_item.action_history = null;
                                                WriteEncryptedSensor(sensor, sensor_item);
                                            }

                                            // Create notification_json
                                            Notifications notifications = new Notifications
                                            {
                                                mail = sensor_item.notifications_mail,
                                                microsoft_teams = sensor_item.notifications_microsoft_teams,
                                                telegram = sensor_item.notifications_telegram,
                                                ntfy_sh = sensor_item.notifications_ntfy_sh,
                                                webhook = sensor_item.notifications_webhook
                                            };

                                            // Serializing the extracted properties to JSON
                                            string notifications_json = JsonSerializer.Serialize(notifications, new JsonSerializerOptions { WriteIndented = true });
                                            
                                            if (sensor_item.category == 0) //utilization
                                            {
                                                if (sensor_item.sub_category == 0)
                                                {
                                                    // CPU usage
                                                    if (Configuration.Agent.language == "en-US")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor CPU (" + sensor_item.name +  ")", details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0); // type is 2 = sensor
                                                    else if (Configuration.Agent.language == "de-DE")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor CPU (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                                }
                                                else if (sensor_item.sub_category == 1) // RAM usage
                                                {
                                                    if (Configuration.Agent.language == "en-US")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor RAM (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                    else if (Configuration.Agent.language == "de-DE")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor RAM (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                                }
                                                else if (sensor_item.sub_category == 2) // Disks
                                                {
                                                    if (Configuration.Agent.language == "en-US")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor Drive (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                    else if (Configuration.Agent.language == "de-DE")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor Laufwerk (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                                }
                                                else if (sensor_item.sub_category == 3) // CPU process usage
                                                {
                                                    if (Configuration.Agent.language == "en-US")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor Process CPU usage (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Further information" + additional_details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                    else if (Configuration.Agent.language == "de-DE")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor Prozess-CPU-Nutzung (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Weitere Informationen" + additional_details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                                }
                                                else if (sensor_item.sub_category == 4) // RAM process usage in %
                                                {
                                                    if (Configuration.Agent.language == "en-US")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor Process RAM usage (%) (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Further information" + additional_details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                    else if (Configuration.Agent.language == "de-DE")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor Prozess-RAM-Nutzung (%) (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Weitere Informationen" + additional_details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                                }
                                                else if (sensor_item.sub_category == 5) // RAM process usage in MB
                                                {
                                                    if (Configuration.Agent.language == "en-US")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor Process RAM usage (MB) (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Further information" + additional_details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                    else if (Configuration.Agent.language == "de-DE")
                                                        Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Sensor Prozess-RAM-Nutzung (MB) (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Weitere Informationen" + additional_details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                                }
                                            }
                                            else if (sensor_item.category == 1) // Windows Eventlog
                                            {
                                                if (Configuration.Agent.language == "en-US")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Windows Eventlog (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                else if (Configuration.Agent.language == "de-DE")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Windows Eventlog (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                            }
                                            else if (sensor_item.category == 2) // PowerShell
                                            {
                                                if (Configuration.Agent.language == "en-US")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "PowerShell (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                else if (Configuration.Agent.language == "de-DE")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "PowerShell (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                            }
                                            else if (sensor_item.category == 3) // Service
                                            {
                                                if (Configuration.Agent.language == "en-US")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Service (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                else if (Configuration.Agent.language == "de-DE")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Dienst (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                            }
                                            else if (sensor_item.category == 4) // Ping
                                            {
                                                if (Configuration.Agent.language == "en-US")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Ping (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                else if (Configuration.Agent.language == "de-DE")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Ping (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                            }
                                            else if (sensor_item.category == 5)
                                            {
                                                if (Configuration.Agent.language == "en-US")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Bash (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                else if (Configuration.Agent.language == "de-DE")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Bash (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                            }
                                            else if (sensor_item.category == 6)
                                            {
                                                if (Configuration.Agent.language == "en-US")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Zsh (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "History of actions" + Environment.NewLine + action_history, notifications_json, 2, 0);
                                                else if (Configuration.Agent.language == "de-DE")
                                                    Events.Logger.Insert_Event(sensor_item.severity.ToString(), "Sensor", "Zsh (" + sensor_item.name + ")", details + Environment.NewLine + Environment.NewLine + "Historie der Aktionen" + Environment.NewLine + action_history, notifications_json, 2, 1);
                                            }

                                            // Mark as notified to prevent spam (only after actually sending the event)
                                            AddNotificationIfNeeded(sensor_item, sensor, details);

                                            sensor_item.notification_treshold_count = 0;
                                            WriteEncryptedSensor(sensor, sensor_item);
                                        }
                                        else // if not, increment the counter
                                        {
                                            sensor_item.notification_treshold_count++;
                                            WriteEncryptedSensor(sensor, sensor_item);
                                        }
                                    } // end of should_notify
                                    else
                                    {
                                        Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Sensor triggered but notification suppressed", $"name: {sensor_item.name}, id: {sensor_item.id}, category: {sensor_item.category}, sub_category: {sensor_item.sub_category}");
                                    }
                                } // end of triggered
                                else // not triggered - sensor is now resolved
                                {
                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Sensor not triggered", $"name: {sensor_item.name}, id: {sensor_item.id}, category: {sensor_item.category}, sub_category: {sensor_item.sub_category}");

                                    // Send resolved notification if needed (before resetting flags)
                                    SendResolvedNotificationIfNeeded(sensor_item, sensor);

                                    // Reset notification flag if sensor is no longer triggered (problem resolved)
                                    ResetNotificationFlagIfNeeded(sensor_item, sensor);

                                    // if auto reset is enabled, reset the counters
                                    if (sensor_item.auto_reset)
                                    {
                                        // Reset notification counter
                                        sensor_item.notification_treshold_count = 0;
                                        WriteEncryptedSensor(sensor, sensor_item);

                                        // Reset action counter
                                        sensor_item.action_treshold_count = 0;
                                        WriteEncryptedSensor(sensor, sensor_item);
                                    }
                                }

                                // Sensor execution successful
                                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Execution finished successfully", "name: " + sensor_item.name + " id: " + sensor_item.id);
                            }
                            catch (Exception ex)
                            {
                                // Sensor failed - rollback last_run to allow retry on next scheduler run
                                sensor_item.last_run = previous_last_run;
                                WriteEncryptedSensor(sensor, sensor_item);

                                Logging.Error("Sensors.Time_Scheduler.Check_Execution", "Sensor execution failed (rolled back last_run)", "name: " + sensor_item.name + " id: " + sensor_item.id + " error: " + ex.ToString());

                                // Insert error event
                                if (Configuration.Agent.language == "en-US")
                                    Events.Logger.Insert_Event("2", "Sensor", sensor_item.name + " failed", "Sensor: " + sensor_item.name + " (" + sensor_item.description + ") " + Environment.NewLine + Environment.NewLine + "Error: " + Environment.NewLine + ex.ToString(), String.Empty, 1, 0);
                                else if (Configuration.Agent.language == "de-DE")
                                    Events.Logger.Insert_Event("2", "Sensor", sensor_item.name + " fehlgeschlagen", "Sensor: " + sensor_item.name + " (" + sensor_item.description + ") " + Environment.NewLine + Environment.NewLine + "Fehler: " + Environment.NewLine + ex.ToString(), String.Empty, 1, 1);
                                
                                // Delete corrupted sensor file if exists
                                string sensor_path = Path.Combine(Application_Paths.program_data_sensors, sensor_item.id + ".json");
                        
                                if (File.Exists(sensor_path))
                                {
                                    Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Delete corrupted sensor file", "Sensor id: " + sensor_item.id);
                                    File.Delete(sensor_path);
                                }
                            }
                        }
                        else
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Sensor will not be executed", "name: " + sensor_item.name + " id: " + sensor_item.id);
                    }
                    catch (Exception e)
                    {
                        Logging.Error("Sensors.Time_Scheduler.Check_Execution", "Error processing sensor during execution check",
                            "Sensor file: " + sensor + " Exception: " + e.ToString());
                        
                        // Delete corrupted sensor file if exists
                        string sensor_path = Path.Combine(Application_Paths.program_data_sensors, sensor_id + ".json");
                        
                        if (File.Exists(sensor_path))
                        {
                            Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Delete corrupted sensor file", "Sensor id: " + sensor_id);
                            File.Delete(sensor_path);
                        }
                    }
                }

                Logging.Sensors("Sensors.Time_Scheduler.Check_Execution", "Check sensor execution", "Stop");
            }
            catch (Exception ex)
            {
                Logging.Error("Sensors.Time_Scheduler.Check_Execution", "General Error (id)", ex.ToString() + "(" + sensor_id + ")");
            }
        }
    }
}
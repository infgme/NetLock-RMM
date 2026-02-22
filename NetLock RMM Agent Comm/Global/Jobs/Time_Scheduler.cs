using Global.Helper;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Management;
using NetLock_RMM_Agent_Comm;

namespace Global.Jobs
{
    internal class Time_Scheduler
    {
        public class Job
        {
            public string id { get; set; }
            public string name { get; set; }
            public string date { get; set; }
            public string last_run { get; set; }
            public string author { get; set; }
            public string description { get; set; }
            public string platform { get; set; }
            public string type { get; set; }
            public string script { get; set; }
            public int? timeout { get; set; } // Nullable to handle null values in JSON

            public int time_scheduler_type { get; set; }
            public int time_scheduler_seconds { get; set; }
            public int time_scheduler_minutes { get; set; }
            public int time_scheduler_hours { get; set; }
            public string time_scheduler_time { get; set; }
            public string time_scheduler_date { get; set; }
            public bool time_scheduler_monday { get; set; }
            public bool time_scheduler_tuesday { get; set; }
            public bool time_scheduler_wednesday { get; set; }
            public bool time_scheduler_thursday { get; set; }
            public bool time_scheduler_friday { get; set; }
            public bool time_scheduler_saturday { get; set; }
            public bool time_scheduler_sunday { get; set; }
        }

        // Helper method to check if job should run today based on weekday settings
        private static bool ShouldRunToday(Job job)
        {
            try
            {
                switch (DateTime.Now.DayOfWeek)
                {
                    case DayOfWeek.Monday:
                        return job.time_scheduler_monday;
                    case DayOfWeek.Tuesday:
                        return job.time_scheduler_tuesday;
                    case DayOfWeek.Wednesday:
                        return job.time_scheduler_wednesday;
                    case DayOfWeek.Thursday:
                        return job.time_scheduler_thursday;
                    case DayOfWeek.Friday:
                        return job.time_scheduler_friday;
                    case DayOfWeek.Saturday:
                        return job.time_scheduler_saturday;
                    case DayOfWeek.Sunday:
                        return job.time_scheduler_sunday;
                    default:
                        return false;
                }
            }
            catch (Exception e)
            {
                Logging.Error("Sensors.Time_Scheduler.ShouldRunToday", "Check if sensor should run today",
                    "Sensor id: " + job.id + " Exception: " + e.ToString());
                
                return false;
            }
        }

        // Helper method to write encrypted job to disk
        private static void WriteEncryptedJob(string filePath, Job job)
        {
            try
            {
                string job_json = JsonSerializer.Serialize(job);
                string encrypted_json = Encryption.String_Encryption.Encrypt(job_json, Application_Settings.NetLock_Local_Encryption_Key);
                File.WriteAllText(filePath, encrypted_json);
            }
            catch (Exception e)
            {
                Logging.Error("Jobs.Time_Scheduler.WriteEncryptedJob", "Error writing encrypted job", e.ToString());
            }
        }

        public static void Check_Execution()
        {
            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Check job execution", "Start");

            Initialization.Health.Check_Directories();

            try
            {
                DateTime os_up_time = Global.Helper.Globalization.GetLastBootUpTime(); // Environment.TickCount is not reliable, use WMI instead

                List<Job> job_items = JsonSerializer.Deserialize<List<Job>>(Device_Worker.policy_jobs_json);

                // Write each job to disk if not already exists, check if script has changed
                foreach (var job in job_items)
                {
                    try
                    {
                        // Check if job is for the current platform
                        if (OperatingSystem.IsWindows() && job.platform != "Windows")
                            continue;
                        else if (OperatingSystem.IsLinux() && job.platform != "Linux")
                            continue;
                        else if (OperatingSystem.IsMacOS() && job.platform != "MacOS")
                            continue;
                        
                        Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Check if job exists on disk", "Job: " + job.name + " Job id: " + job.id);

                        string job_json = JsonSerializer.Serialize(job);
                        string job_path = Path.Combine(Application_Paths.program_data_jobs, job.id + ".json");

                        if (!File.Exists(job_path))
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Check if job exists on disk", "false");
                            // Encrypt job JSON before writing
                            string encrypted_job_json = Encryption.String_Encryption.Encrypt(job_json, Application_Settings.NetLock_Local_Encryption_Key);
                            File.WriteAllText(job_path, encrypted_job_json);
                        }

                        // Check if script has changed
                        if (File.Exists(job_path))
                        {
                            // Decrypt job JSON after reading
                            string encrypted_existing_job_json = File.ReadAllText(job_path);
                            string existing_job_json = Encryption.String_Encryption.Decrypt(encrypted_existing_job_json, Application_Settings.NetLock_Local_Encryption_Key);
                            
                            Job existing_job = JsonSerializer.Deserialize<Job>(existing_job_json);
                            
                            if (existing_job.script != job.script)
                            {
                                Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Job script has changed. Updating it.", "Job: " + job.name + " Job id: " + job.id);
                                // Encrypt job JSON before writing
                                string encrypted_job_json = Encryption.String_Encryption.Encrypt(job_json, Application_Settings.NetLock_Local_Encryption_Key);
                                File.WriteAllText(job_path, encrypted_job_json);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Logging.Error("Jobs.Time_Scheduler.Check_Execution", "Error processing job before execution check", "Job id: " + job.id + " Exception: " + e.ToString());
                        
                        // Delete corrupted file so it will be recreated
                        string job_path = Path.Combine(Application_Paths.program_data_jobs, job.id + ".json");
                        
                        try
                        {
                            File.Delete(job_path);
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Deleted corrupted job file (pre-execution check)", "Job file: " + job_path);
                        }
                        catch (Exception deleteEx)
                        {
                            Logging.Error("Jobs.Time_Scheduler.Check_Execution", "Failed to delete corrupted job file (pre-execution check)", "Job file: " + job_path + " Error: " + deleteEx.Message);
                        }
                    }
                }

                // Clean up old jobs not existing anymore
                foreach (string file in Directory.GetFiles(Application_Paths.program_data_jobs))
                {
                    try
                    {
                        Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Clean old jobs", "Job: " + file);

                        string file_name = Path.GetFileName(file);
                        string file_id = file_name.Replace(".json", "");

                        bool found = false;

                        foreach (var job in job_items)
                        {
                            if (job.id == file_id)
                            {
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Clean old jobs", "Delete job: " + file);
                            File.Delete(file);
                        }
                    }
                    catch (Exception e)
                    {
                        Logging.Error("Jobs.Time_Scheduler.Check_Execution", "Error during cleanup of old jobs", "Job file: " + file + " Exception: " + e.ToString());
                    }
                }

                // Now read & consume each job
                foreach (var job in Directory.GetFiles(Application_Paths.program_data_jobs))
                {
                    Job job_item = null;
                    
                    try
                    {
                        // Decrypt job JSON after reading
                        string encrypted_job_json = File.ReadAllText(job);
                        string job_json = Encryption.String_Encryption.Decrypt(encrypted_job_json, Application_Settings.NetLock_Local_Encryption_Key);
                        job_item = JsonSerializer.Deserialize<Job>(job_json);

                        // Null-check after deserialization
                        if (job_item == null)
                        {
                            Logging.Error("Jobs.Time_Scheduler.Check_Execution", "Failed to deserialize job", "Job file: " + job);
                            
                            // Delete corrupted file so it will be recreated
                            File.Delete(job);
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Deleted corrupted job file (job was null)", "Job file: " + job);
                            
                            continue;
                        }
                        
                        Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Check job execution", "Job: " + job_item.name + " time_scheduler_type: " + job_item.time_scheduler_type);

                        // Check enabled
                        /*if (!job_item.enabled)
                        {
                            Logging.Handler.Jobs("Jobs.Time_Scheduler.Check_Execution", "Check job execution", "Job disabled");

                            continue;
                        }*/

                        bool execute = false;

                        if (job_item.time_scheduler_type == 0) // system boot
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "System boot", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run ?? DateTime.Now.ToString()) + " Last boot: " + os_up_time.ToString());

                            // Check if last run is empty
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                // Only execute if the last boot was within the last 10 minutes
                                // This prevents the job from executing when newly created on a system that has been running for a while
                                if (DateTime.Now - os_up_time <= TimeSpan.FromMinutes(10))
                                {
                                    execute = true;
                                }
                                
                                // Set last_run to the current boot time to prevent re-execution until next reboot
                                job_item.last_run = os_up_time.ToString();
                                WriteEncryptedJob(job, job_item);
                            }
                            else if (DateTime.Parse(job_item.last_run) < os_up_time)
                            {
                                // Job was last run before the current boot, so execute it
                                execute = true;
                            }
                        }
                        else if (job_item.time_scheduler_type == 1) // date & time
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "date & time", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run ?? DateTime.Now.ToString()));

                            DateTime scheduledDateTime = DateTime.ParseExact($"{job_item.time_scheduler_date.Split(' ')[0]} {job_item.time_scheduler_time}", "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

                            // Check if last run is empty, if so, subsract 24 hours from scheduled time to trigger the execution
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = (scheduledDateTime - TimeSpan.FromHours(24)).ToString();
                                WriteEncryptedJob(job, job_item);
                            }

                            DateTime lastRunDateTime = DateTime.Parse(job_item.last_run);

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "date & time", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run) + " scheduledDateTime: " + scheduledDateTime.ToString() + " execute: " + execute.ToString());

                            if (DateTime.Now.Date >= scheduledDateTime.Date && DateTime.Now.TimeOfDay >= scheduledDateTime.TimeOfDay && lastRunDateTime < scheduledDateTime)
                                execute = true;
                        }
                        else if (job_item.time_scheduler_type == 2) // all x seconds
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "all x seconds", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run ?? DateTime.Now.ToString()));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = DateTime.Now.ToString();
                                WriteEncryptedJob(job, job_item);
                            }

                            if (DateTime.Parse(job_item.last_run) <= DateTime.Now - TimeSpan.FromSeconds(job_item.time_scheduler_seconds))
                                execute = true;

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "all x seconds", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run) + " execute: " + execute.ToString());
                        }
                        else if (job_item.time_scheduler_type == 3) // all x minutes
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "all x minutes", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + (job_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedJob(job, job_item);
                            }

                            DateTime lastRun = DateTime.Parse(job_item.last_run, CultureInfo.InvariantCulture);
                            if (lastRun <= DateTime.Now - TimeSpan.FromMinutes(job_item.time_scheduler_minutes))
                                execute = true;

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "all x minutes", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + lastRun + " execute: " + execute.ToString());
                        }
                        else if (job_item.time_scheduler_type == 4) // all x hours
                        {
                            Logging.Jobs("Sensors.Time_Scheduler.Check_Execution", "all x hours", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + (job_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedJob(job, job_item);
                            }

                            DateTime lastRun = DateTime.Parse(job_item.last_run, CultureInfo.InvariantCulture);
                            if (lastRun <= DateTime.Now - TimeSpan.FromHours(job_item.time_scheduler_hours))
                                execute = true;

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "all x hours", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + lastRun + " execute: " + execute.ToString());
                        }
                        else if (job_item.time_scheduler_type == 5) // date, all x seconds
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "date, all x seconds", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run ?? DateTime.Now.ToString()));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = DateTime.Now.ToString();
                                WriteEncryptedJob(job, job_item);
                            }

                            if (DateTime.Now.Date == DateTime.Parse(job_item.time_scheduler_date).Date && DateTime.Parse(job_item.last_run) <= DateTime.Now - TimeSpan.FromSeconds(job_item.time_scheduler_seconds))
                                execute = true;

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "date, all x seconds", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run) + " execute: " + execute.ToString());
                        }
                        else if (job_item.time_scheduler_type == 6) // date, all x minutes
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "date, all x minutes", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run ?? DateTime.Now.ToString()));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = DateTime.Now.ToString();
                                WriteEncryptedJob(job, job_item);
                            }

                            if (DateTime.Now.Date == DateTime.Parse(job_item.time_scheduler_date).Date && DateTime.Parse(job_item.last_run) < DateTime.Now - TimeSpan.FromMinutes(job_item.time_scheduler_minutes))
                                execute = true;

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "date, all x minutes", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run) + " execute: " + execute.ToString());
                        }
                        else if (job_item.time_scheduler_type == 7) // date, all x hours
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "date, all x hours", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run ?? DateTime.Now.ToString()));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = DateTime.Now.ToString();
                                WriteEncryptedJob(job, job_item);
                            }

                            if (DateTime.Now.Date == DateTime.Parse(job_item.time_scheduler_date).Date && DateTime.Parse(job_item.last_run) < DateTime.Now - TimeSpan.FromHours(job_item.time_scheduler_hours))
                                execute = true;

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "date, all x hours", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + DateTime.Parse(job_item.last_run) + " execute: " + execute.ToString());
                        }
                        else if (job_item.time_scheduler_type == 8) // following days at X time
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "following days at X time", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + (job_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            DateTime scheduledTime = DateTime.ParseExact(job_item.time_scheduler_time, "HH:mm:ss", CultureInfo.InvariantCulture);

                            // Check if last run is empty, if so set it to a time in the past to trigger initial execution
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = DateTime.Now.AddDays(-1).ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedJob(job, job_item);
                            }

                            DateTime lastRunDateTime = DateTime.Parse(job_item.last_run, CultureInfo.InvariantCulture);

                            // Check if current time is past the scheduled time and we haven't run today yet
                            bool shouldRunToday = DateTime.Now.TimeOfDay >= scheduledTime.TimeOfDay && lastRunDateTime.Date < DateTime.Now.Date;

                            // Use helper method to check weekday
                            if (ShouldRunToday(job_item) && shouldRunToday)
                                execute = true;

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "following days at X time", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + lastRunDateTime + " execute: " + execute.ToString());
                        }
                        else if (job_item.time_scheduler_type == 9) // following days, x seconds
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "following days, x seconds", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + (job_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedJob(job, job_item);
                            }

                            DateTime lastRun = DateTime.Parse(job_item.last_run, CultureInfo.InvariantCulture);

                            // Check if it's a valid day AND the interval has passed
                            if (ShouldRunToday(job_item) && lastRun <= DateTime.Now - TimeSpan.FromSeconds(job_item.time_scheduler_seconds))
                                execute = true;

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "following days, x seconds", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + lastRun + " execute: " + execute.ToString());
                        }
                        else if (job_item.time_scheduler_type == 10) // following days, x minutes
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "following days, x minutes", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + (job_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedJob(job, job_item);
                            }

                            DateTime lastRun = DateTime.Parse(job_item.last_run, CultureInfo.InvariantCulture);

                            // Check if it's a valid day AND the interval has passed
                            if (ShouldRunToday(job_item) && lastRun <= DateTime.Now - TimeSpan.FromMinutes(job_item.time_scheduler_minutes))
                                execute = true;

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "following days, x minutes", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + lastRun + " execute: " + execute.ToString());
                        }
                        else if (job_item.time_scheduler_type == 11) // following days, x hours
                        {
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "following days, x hours", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + (job_item.last_run ?? DateTime.Now.ToString(CultureInfo.InvariantCulture)));

                            // Check if last run is empty, if so set it to now
                            if (String.IsNullOrEmpty(job_item.last_run))
                            {
                                job_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                                WriteEncryptedJob(job, job_item);
                            }

                            DateTime lastRun = DateTime.Parse(job_item.last_run, CultureInfo.InvariantCulture);

                            // Check if it's a valid day AND the interval has passed
                            if (ShouldRunToday(job_item) && lastRun <= DateTime.Now - TimeSpan.FromHours(job_item.time_scheduler_hours))
                                execute = true;

                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "following days, x hours", "name: " + job_item.name + " id: " + job_item.id + " last_run: " + lastRun + " execute: " + execute.ToString());
                        }

                        // Execute if needed
                        if (execute)
                        {
                            // Store the old last_run value in case we need to rollback
                            string previous_last_run = job_item.last_run;
                            
                            // Update last run IMMEDIATELY to prevent race conditions (before executing the job)
                            job_item.last_run = DateTime.Now.ToString(CultureInfo.InvariantCulture);
                            WriteEncryptedJob(job, job_item);

                            string result = String.Empty;

                            try
                            {
                                Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Execute job", "name: " + job_item.name + " id: " + job_item.id);

                                // Use null-coalescing operator to provide default timeout of 0 (which means 60 minutes default)
                                int timeoutValue = job_item.timeout ?? 0;

                                //Execute job
                                if (OperatingSystem.IsWindows())
                                    result = Windows.Helper.PowerShell.Execute_Script("Jobs.Time_Scheduler.Check_Execution (execute job) " + job_item.name, job_item.script, timeoutValue);
                                else if (OperatingSystem.IsLinux())
                                    result = Linux.Helper.Bash.Execute_Script("Jobs.Time_Scheduler.Check_Execution (execute job) " + job_item.name, true, job_item.script, timeoutValue);
                                else if (OperatingSystem.IsMacOS())
                                    result = MacOS.Helper.Zsh.Execute_Script("Jobs.Time_Scheduler.Check_Execution (execute job) " + job_item.name, true, job_item.script, timeoutValue);
                                
                                // Insert event
                                Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Job executed", "name: " + job_item.name + " id: " + job_item.id + " result: " + result);

                                // Check if job description is empty
                                if (String.IsNullOrEmpty(job_item.description) && Configuration.Agent.language == "en-US")
                                    job_item.description = "No description";
                                else if (String.IsNullOrEmpty(job_item.description) && Configuration.Agent.language == "de-DE")
                                    job_item.description = "Keine Beschreibung";

                                if (Configuration.Agent.language == "en-US")
                                    Events.Logger.Insert_Event("0", "Job", job_item.name + " completed", "Job: " + job_item.name + " (" + job_item.description + ") " + Environment.NewLine + Environment.NewLine + "Result: " + Environment.NewLine + result, String.Empty, 1, 0);
                                else if (Configuration.Agent.language == "de-DE")
                                    Events.Logger.Insert_Event("0", "Job", job_item.name + " fertiggestellt.", "Job: " + job_item.name + " (" + job_item.description + ") " + Environment.NewLine + Environment.NewLine + "Ergebnis: " + Environment.NewLine + result, String.Empty, 1, 1);

                                Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Execution finished successfully", "name: " + job_item.name + " id: " + job_item.id);
                            }
                            catch (Exception ex)
                            {
                                // Job failed - rollback last_run to allow retry on next scheduler run
                                job_item.last_run = previous_last_run;
                                WriteEncryptedJob(job, job_item);

                                Logging.Error("Jobs.Time_Scheduler.Check_Execution", "Job execution failed (rolled back last_run)", "name: " + job_item.name + " id: " + job_item.id + " error: " + ex.ToString());

                                // Insert error event
                                if (Configuration.Agent.language == "en-US")
                                    Events.Logger.Insert_Event("2", "Job", job_item.name + " failed", "Job: " + job_item.name + " (" + job_item.description + ") " + Environment.NewLine + Environment.NewLine + "Error: " + Environment.NewLine + ex.ToString(), String.Empty, 1, 0);
                                else if (Configuration.Agent.language == "de-DE")
                                    Events.Logger.Insert_Event("2", "Job", job_item.name + " fehlgeschlagen", "Job: " + job_item.name + " (" + job_item.description + ") " + Environment.NewLine + Environment.NewLine + "Fehler: " + Environment.NewLine + ex.ToString(), String.Empty, 1, 1);
                            }
                        }
                        else
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Job will not be executed", "name: " + job_item.name + " id: " + job_item.id);
                    }
                    catch (Exception ex)
                    {
                        Logging.Error("Jobs.Time_Scheduler.Check_Execution", "Failed to read or decrypt job file", "Job file: " + job + " Error: " + ex.Message);
                        
                        // Delete corrupted file so it will be recreated on next sync
                        try
                        {
                            File.Delete(job);
                            Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Deleted corrupted job file", "Job file: " + job);
                        }
                        catch (Exception deleteEx)
                        {
                            Logging.Error("Jobs.Time_Scheduler.Check_Execution", "Failed to delete corrupted job file", "Job file: " + job + " Error: " + deleteEx.Message);
                        } 
                    }
                }

                Logging.Jobs("Jobs.Time_Scheduler.Check_Execution", "Check job execution", "Stop");
            }
            catch (Exception ex)
            {
                Logging.Error("Jobs.Time_Scheduler.Check_Execution", "General Error", ex.ToString());
            }
        }
    }
}
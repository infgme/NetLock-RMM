using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Global.Helper;
using System.Diagnostics;
using Windows.Helper;
using NetLock_RMM_Agent_Comm;
using System.Text.Json;
using Global.Encryption;

namespace Global.Initialization
{
    internal class Health
    {
        // Check if the directories are in place
        public static void Check_Directories()
        {
            try
            {
                // Program Data
                if (!Directory.Exists(Application_Paths.program_data))
                    Directory.CreateDirectory(Application_Paths.program_data);

                // Logs
                if (!Directory.Exists(Application_Paths.program_data_logs))
                    Directory.CreateDirectory(Application_Paths.program_data_logs);
                
                // Installer
                if (!Directory.Exists(Application_Paths.program_data_installer))
                    Directory.CreateDirectory(Application_Paths.program_data_installer);

                // Updates
                if (!Directory.Exists(Application_Paths.program_data_updates))
                    Directory.CreateDirectory(Application_Paths.program_data_updates);

                // NetLock Temp
                if (!Directory.Exists(Application_Paths.program_data_temp))
                    Directory.CreateDirectory(Application_Paths.program_data_temp);

                // Jobs
                if (!Directory.Exists(Application_Paths.program_data_jobs))
                    Directory.CreateDirectory(Application_Paths.program_data_jobs);

                // Scripts
                if (!Directory.Exists(Application_Paths.program_data_scripts))
                    Directory.CreateDirectory(Application_Paths.program_data_scripts);

                // Sensors
                if (!Directory.Exists(Application_Paths.program_data_sensors))
                    Directory.CreateDirectory(Application_Paths.program_data_sensors);
                
                // Tray Icon
                if (!Directory.Exists(Application_Paths.tray_icon_dir))
                    Directory.CreateDirectory(Application_Paths.tray_icon_dir);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Logging.Error("Global.Initialization.Health.Check_Directories", "", ex.ToString());
            }
        }

        public static void CleanTempScripts()
        {
            try
            {
                // Check if files exist in the scripts temp directory (comm agent)
                if (Directory.Exists(Application_Paths.program_data_scripts))
                {
                    string[] temp_files = Directory.GetFiles(Application_Paths.program_data_scripts);

                    foreach (string file in temp_files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            Logging.Error("Global.Initialization.Health.CleanThings",
                                "Failed to delete temp script: " + file, ex.ToString());
                        }
                    }
                }
                    
                // Check if files exist in the scripts temp directory (remote agent)
                if (Directory.Exists(Application_Paths.program_data_remote_agent_scripts))
                {
                    string[] temp_files = Directory.GetFiles(Application_Paths.program_data_remote_agent_scripts);

                    foreach (string file in temp_files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            Logging.Error("Global.Initialization.Health.CleanThings",
                                "Failed to delete remote agent temp script: " + file, ex.ToString());
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logging.Error("Global.Initialization.Health.CleanThings", "", e.ToString());
            }
        }

        // Check if the registry keys are in place
        public static void Check_Registry()
        { 
            try
            {
                // Check if netlock key exists
                if (!Windows.Helper.Registry.HKLM_Key_Exists(Application_Paths.netlock_reg_path))
                    Windows.Helper.Registry.HKLM_Create_Key(Application_Paths.netlock_reg_path);

                // Check if msdav key exists
                if (!Windows.Helper.Registry.HKLM_Key_Exists(Application_Paths.netlock_microsoft_defender_antivirus_reg_path))
                    Windows.Helper.Registry.HKLM_Create_Key(Application_Paths.netlock_microsoft_defender_antivirus_reg_path);
            }
            catch (Exception ex)
            {
                Logging.Error("Global.Initialization.Health.Check_Registry", "", ex.ToString());
            }
        }

        // Check if the firewall rules are in place
        public static void Check_Firewall()
        {
            // Dev setup moved to linux. Com is not available on linux. Need to look for a alternative to set firewall rules and verify them
            /*
            Windows.Microsoft_Defender_Firewall.Handler.NetLock_RMM_Comm_Agent_Rule_Inbound();
            Windows.Microsoft_Defender_Firewall.Handler.NetLock_RMM_Comm_Agent_Rule_Outbound();
            Windows.Microsoft_Defender_Firewall.Handler.NetLock_RMM_Health_Service_Rule();
            Windows.Microsoft_Defender_Firewall.Handler.NetLock_Installer_Rule();
            Windows.Microsoft_Defender_Firewall.Handler.NetLock_Uninstaller_Rule();
            */
        }

        // Check if the databases are in place
        public static void Check_Databases()
        {
            // Check if the databases are in place
            if (!File.Exists(Application_Paths.program_data_netlock_policy_database))
                Database.NetLock_Data_Setup();

            // Check if the events database is in place
            if (!File.Exists(Application_Paths.program_data_netlock_events_database))
                Database.NetLock_Events_Setup();
        }

        public static void Clean_Service_Restart()
        {
            Logging.Debug("Global.Initialization.Health.Clean_Service_Restart", "Starting.", "");

            Process cmd_process = new Process();
            cmd_process.StartInfo.UseShellExecute = true;
            cmd_process.StartInfo.CreateNoWindow = true;
            cmd_process.StartInfo.FileName = "cmd.exe";
            cmd_process.StartInfo.Arguments = "/c powershell" + " Stop-Service 'NetLock_RMM_Agent_Comm'; Remove-Item 'C:\\ProgramData\\0x101 Cyber Security\\NetLock RMM\\Comm Agent\\policy.nlock'; Remove-Item 'C:\\ProgramData\\0x101 Cyber Security\\NetLock RMM\\Comm Agent\\events.nlock'; Start-Service 'NetLock_RMM_Agent_Comm'";
            cmd_process.Start();
            cmd_process.WaitForExit();

            Logging.Error("Global.Initialization.Health.Clean_Service_Restart", "Stopping.", "");
        }

        public static void Setup_Events_Virtual_Datatable()
        {
            try
            {
                Device_Worker.events_data_table.Columns.Clear();
                Device_Worker.events_data_table.Columns.Add("severity");
                Device_Worker.events_data_table.Columns.Add("reported_by");
                Device_Worker.events_data_table.Columns.Add("event");
                Device_Worker.events_data_table.Columns.Add("description");
                Device_Worker.events_data_table.Columns.Add("type");
                Device_Worker.events_data_table.Columns.Add("language");
                Device_Worker.events_data_table.Columns.Add("notification_json");

                Logging.Debug("Global.Initialization.Health.Setup_Events_Virtual_Datatable", "Create datatable", "Done.");
            }
            catch (Exception ex)
            {
                Logging.Error("Global.Initialization.Health.Setup_Events_Virtual_Datatable", "Create datatable", ex.ToString());
            }
        }
        
        public static void User_Processes()
        {
            if (OperatingSystem.IsWindows())
            {
                // Delete old NetLock RMM User Agent from the registry, if it exists
                //Registry.HKLM_Delete_Value(Application_Paths.hklm_run_directory_reg_path, "NetLock RMM User Process");
        
                // Delete the NetLock RMM User Agent from the registry, if it exists (is now started interactively by the remote agent)
                Registry.HKLM_Delete_Value(Application_Paths.hklm_run_directory_reg_path, "NetLock RMM User Agent");
                
                // Create the NetLock RMM User Process in the registry to start at user login, is used as fallback if the remote agent fails to start the process interactively
                Registry.HKLM_Write_Value(Application_Paths.hklm_run_directory_reg_path, "NetLock RMM User Process", Application_Paths.program_files_user_process_uac_path);
                
                // Check if Tray Icon is enabled in the config
                if (IsTrayIconEnabled())
                {
                    // Create the NetLock RMM Tray Icon in the registry to start at user login
                    Logging.Debug("Initialization.Health.User_Processes", "Windows", "Tray Icon enabled - creating registry entry");
                    Registry.HKLM_Write_Value(Application_Paths.hklm_run_directory_reg_path, "NetLock RMM Tray Icon", Application_Paths.program_files_tray_icon_path);
                }
                else
                {
                    // Delete the NetLock RMM Tray Icon from the registry if it exists
                    Logging.Debug("Initialization.Health.User_Processes", "Windows", "Tray Icon disabled - removing registry entry");
                    Registry.HKLM_Delete_Value(Application_Paths.hklm_run_directory_reg_path, "NetLock RMM Tray Icon");
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                try
                {
                    Logging.Debug("Initialization.Health.User_Processes", "Linux", "Setting up autostart for User Process");
                    
                    // Ensure binaries have execute permissions (Comm Agent runs as root, so it can chmod)
                    // Check if the files exist before trying to chmod them
                    if (File.Exists(Application_Paths.program_files_user_process_path))
                    {
                        try
                        {
                            // Use Process.Start directly for more reliable execution
                            var chmodProcess = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = "/bin/chmod",
                                    Arguments = $"+x {Application_Paths.program_files_user_process_path}",
                                    UseShellExecute = false,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                    CreateNoWindow = true
                                }
                            };
                            chmodProcess.Start();
                            string chmodError = chmodProcess.StandardError.ReadToEnd();
                            chmodProcess.WaitForExit(5000);
                            
                            if (chmodProcess.ExitCode == 0)
                            {
                                Logging.Debug("Initialization.Health.User_Processes", "Linux", $"Set execute permissions on User Process: {Application_Paths.program_files_user_process_path}");
                            }
                            else
                            {
                                Logging.Error("Initialization.Health.User_Processes", $"chmod failed with exit code {chmodProcess.ExitCode}", chmodError);
                            }
                        }
                        catch (Exception chmodEx)
                        {
                            Logging.Error("Initialization.Health.User_Processes", "Failed to chmod User Process", chmodEx.Message);
                        }
                    }
                    else
                    {
                        Logging.Debug("Initialization.Health.User_Processes", "Linux", $"User Process binary not found: {Application_Paths.program_files_user_process_path}");
                    }
                    
                    // Create the autostart directory if it doesn't exist
                    if (!Directory.Exists(Application_Paths.linux_autostart_dir))
                    {
                        Directory.CreateDirectory(Application_Paths.linux_autostart_dir);
                        Logging.Debug("Initialization.Health.User_Processes", "Linux", $"Created autostart directory: {Application_Paths.linux_autostart_dir}");
                    }
                    
                    // Create the .desktop file for User Process autostart
                    string userProcessDesktopContent = $@"[Desktop Entry]
Type=Application
Name=NetLock RMM User Process
Comment=NetLock RMM User Process for system monitoring
Exec={Application_Paths.program_files_user_process_path}
Terminal=false
Hidden=false
NoDisplay=true
X-GNOME-Autostart-enabled=true
StartupNotify=false
";
                    
                    File.WriteAllText(Application_Paths.linux_autostart_user_process_desktop_file, userProcessDesktopContent);
                    Logging.Debug("Initialization.Health.User_Processes", "Linux", $"Created User Process autostart desktop file: {Application_Paths.linux_autostart_user_process_desktop_file}");
                    
                    // Set correct permissions (644) for User Process
                    Linux.Helper.Bash.Execute_Script("Set User Process autostart permissions", false, $"chmod 644 \"{Application_Paths.linux_autostart_user_process_desktop_file}\"");
                    
                    // Tray Icon
                    if (IsTrayIconEnabled())
                    {
                        if (File.Exists(Application_Paths.program_files_tray_icon_path))
                        {
                            try
                            {
                                var chmodProcess = new Process
                                {
                                    StartInfo = new ProcessStartInfo
                                    {
                                        FileName = "/bin/chmod",
                                        Arguments = $"+x {Application_Paths.program_files_tray_icon_path}",
                                        UseShellExecute = false,
                                        RedirectStandardOutput = true,
                                        RedirectStandardError = true,
                                        CreateNoWindow = true
                                    }
                                };
                                chmodProcess.Start();
                                string chmodError = chmodProcess.StandardError.ReadToEnd();
                                chmodProcess.WaitForExit(5000);
                                
                                if (chmodProcess.ExitCode == 0)
                                {
                                    Logging.Debug("Initialization.Health.User_Processes", "Linux", $"Set execute permissions on Tray Icon: {Application_Paths.program_files_tray_icon_path}");
                                }
                                else
                                {
                                    Logging.Error("Initialization.Health.User_Processes", $"chmod failed with exit code {chmodProcess.ExitCode}", chmodError);
                                }
                            }
                            catch (Exception chmodEx)
                            {
                                Logging.Error("Initialization.Health.User_Processes", "Failed to chmod Tray Icon", chmodEx.Message);
                            }
                        }
                        else
                        {
                            Logging.Debug("Initialization.Health.User_Processes", "Linux", $"Tray Icon binary not found: {Application_Paths.program_files_tray_icon_path}");
                        }   
                        
                        // Create the .desktop file for Tray Icon autostart
                        Logging.Debug("Initialization.Health.User_Processes", "Linux", "Tray Icon enabled - creating autostart entry");
                        string trayIconDesktopContent = $@"[Desktop Entry]
Type=Application
Name=NetLock RMM Tray Icon
Comment=NetLock RMM Tray Icon for system tray
Exec={Application_Paths.program_files_tray_icon_path}
Terminal=false
Hidden=false
NoDisplay=true
X-GNOME-Autostart-enabled=true
StartupNotify=false
";
                        
                        File.WriteAllText(Application_Paths.linux_autostart_tray_icon_desktop_file, trayIconDesktopContent);
                        Logging.Debug("Initialization.Health.User_Processes", "Linux", $"Created Tray Icon autostart desktop file: {Application_Paths.linux_autostart_tray_icon_desktop_file}");
                        
                        // Set correct permissions (644) for Tray Icon
                        Linux.Helper.Bash.Execute_Script("Set Tray Icon autostart permissions", false, $"chmod 644 \"{Application_Paths.linux_autostart_tray_icon_desktop_file}\"");
                    }
                    else
                    {
                        // Delete the Tray Icon autostart entry if it exists
                        Logging.Debug("Initialization.Health.User_Processes", "Linux", "Tray Icon disabled - removing autostart entry");
                        if (File.Exists(Application_Paths.linux_autostart_tray_icon_desktop_file))
                        {
                            File.Delete(Application_Paths.linux_autostart_tray_icon_desktop_file);
                            Logging.Debug("Initialization.Health.User_Processes", "Linux", $"Deleted Tray Icon autostart desktop file: {Application_Paths.linux_autostart_tray_icon_desktop_file}");
                        }
                    }
                       
                    Logging.Debug("Initialization.Health.User_Processes", "Linux", "Autostart setup completed");
                }
                catch (Exception ex)
                {
                    Logging.Error("Initialization.Health.User_Processes", "Linux autostart setup failed", ex.ToString());
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                //  temporary disable because this will not work. we need a digital signed app bundle to request security privileges from the os
                return;
                
                try
                {
                    Logging.Debug("Initialization.Health.User_Processes", "macOS", "Setting up LaunchAgents for User Process");
                    
                    // Create the LaunchAgents directory if it doesn't exist
                    if (!Directory.Exists(Application_Paths.macos_launch_agents_dir))
                    {
                        Directory.CreateDirectory(Application_Paths.macos_launch_agents_dir);
                        Logging.Debug("Initialization.Health.User_Processes", "macOS", $"Created LaunchAgents directory: {Application_Paths.macos_launch_agents_dir}");
                    }
                    
                    // Ensure binaries have execute permissions (Comm Agent runs as root, so it can chmod)
                    MacOS.Helper.Zsh.Execute_Script("Set User Process execute permissions", false, $"chmod +x \"{Application_Paths.program_files_user_process_path}\"");
                    MacOS.Helper.Zsh.Execute_Script("Set Tray Icon execute permissions", false, $"chmod +x \"{Application_Paths.program_files_tray_icon_path}\"");
                    
                    // Create the plist file for User Process LaunchAgent
                    // Note: Logs are written to /tmp which is world-writable, since LaunchAgents run as the user
                    // and we cannot predict the user's home directory from this system-level plist
                    string userProcessPlistContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>com.netlock.rmm.user.process</string>

    <key>ProgramArguments</key>
    <array>
        <string>{Application_Paths.program_files_user_process_path}</string>
    </array>

    <key>RunAtLoad</key>
    <true/>

    <key>KeepAlive</key>
    <true/>

    <key>LimitLoadToSessionType</key>
    <string>Aqua</string>

    <key>StandardOutPath</key>
    <string>/tmp/netlock_rmm_user_process.log</string>

    <key>StandardErrorPath</key>
    <string>/tmp/netlock_rmm_user_process_error.log</string>
</dict>
</plist>
";
                        
                        File.WriteAllText(Application_Paths.macos_launch_agent_user_process_plist, userProcessPlistContent);
                        Logging.Debug("Initialization.Health.User_Processes", "macOS", $"Created User Process LaunchAgent plist: {Application_Paths.macos_launch_agent_user_process_plist}");

                        // Set correct permissions (644) and ownership (root:wheel) for User Process
                        MacOS.Helper.Zsh.Execute_Script("Set User Process LaunchAgent permissions", false, $"chmod 644 \"{Application_Paths.macos_launch_agent_user_process_plist}\" && chown root:wheel \"{Application_Paths.macos_launch_agent_user_process_plist}\"");

                        // Check if Tray Icon is enabled in the config
                        if (IsTrayIconEnabled())
                        {
                            // Create the plist file for Tray Icon LaunchAgent
                            Logging.Debug("Initialization.Health.User_Processes", "macOS", "Tray Icon enabled - creating LaunchAgent entry");
                            string trayIconPlistContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>com.netlock.rmm.tray.icon</string>

    <key>ProgramArguments</key>
    <array>
        <string>{Application_Paths.program_files_tray_icon_path}</string>
    </array>

    <key>RunAtLoad</key>
    <true/>

    <key>KeepAlive</key>
    <true/>

    <key>LimitLoadToSessionType</key>
    <string>Aqua</string>

    <key>StandardOutPath</key>
    <string>/tmp/netlock_rmm_tray_icon.log</string>

    <key>StandardErrorPath</key>
    <string>/tmp/netlock_rmm_tray_icon_error.log</string>
</dict>
</plist>
";

                            File.WriteAllText(Application_Paths.macos_launch_agent_tray_icon_plist, trayIconPlistContent);
                            Logging.Debug("Initialization.Health.User_Processes", "macOS", $"Created Tray Icon LaunchAgent plist: {Application_Paths.macos_launch_agent_tray_icon_plist}");

                            // Set correct permissions (644) and ownership (root:wheel) for Tray Icon
                            MacOS.Helper.Zsh.Execute_Script("Set Tray Icon LaunchAgent permissions", false, $"chmod 644 \"{Application_Paths.macos_launch_agent_tray_icon_plist}\" && chown root:wheel \"{Application_Paths.macos_launch_agent_tray_icon_plist}\"");
                        }
                        else
                        {
                            // Delete the Tray Icon LaunchAgent if it exists
                            Logging.Debug("Initialization.Health.User_Processes", "macOS", "Tray Icon disabled - removing LaunchAgent entry");
                            if (File.Exists(Application_Paths.macos_launch_agent_tray_icon_plist))
                            {
                                // Unload the LaunchAgent before deleting
                                MacOS.Helper.Zsh.Execute_Script("Unload Tray Icon LaunchAgent", false, $"launchctl unload \"{Application_Paths.macos_launch_agent_tray_icon_plist}\" 2>/dev/null || true");
                                File.Delete(Application_Paths.macos_launch_agent_tray_icon_plist);
                                Logging.Debug("Initialization.Health.User_Processes", "macOS", $"Deleted Tray Icon LaunchAgent plist: {Application_Paths.macos_launch_agent_tray_icon_plist}");
                            }
                        }

                        Logging.Debug("Initialization.Health.User_Processes", "macOS", "LaunchAgent setup completed");
                    }
                    catch (Exception ex)
                    {
                        Logging.Error("Initialization.Health.User_Processes", "macOS LaunchAgent setup failed", ex.ToString());
                    }
                }
        }
        
        private static bool IsTrayIconEnabled()
        {
            try
            {
                string configPath = Application_Paths.tray_icon_settings_json_path;
                
                if (!File.Exists(configPath))
                {
                    Logging.Debug("Initialization.Health.IsTrayIconEnabled", "Config file not found", configPath);
                    // If config doesn't exist, don't start tray icon
                    return false;
                }
                
                string jsonString = File.ReadAllText(configPath);
                
                // Decrypt the config
                jsonString = String_Encryption.Decrypt(jsonString, Application_Settings.NetLock_Local_Encryption_Key);
                
                var configRoot = JsonSerializer.Deserialize<TrayIconConfigRoot>(jsonString);
                
                if (configRoot?.TrayIcon?.Enabled == true)
                {
                    Logging.Debug("Initialization.Health.IsTrayIconEnabled", "Tray icon status", "enabled");
                    return true;
                }
                else
                {
                    Logging.Debug("Initialization.Health.IsTrayIconEnabled", "Tray icon status", "disabled");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Initialization.Health.IsTrayIconEnabled", "Error checking tray icon settings", ex.Message);
                // If there's an error reading the config, don't start
                return false;
            }
        }
        
        // Helper classes for Tray Icon config deserialization
        private class TrayIconConfigRoot
        {
            public TrayIconSettings? TrayIcon { get; set; }
        }
        
        private class TrayIconSettings
        {
            public bool? Enabled { get; set; } = false;
        }
    }
}

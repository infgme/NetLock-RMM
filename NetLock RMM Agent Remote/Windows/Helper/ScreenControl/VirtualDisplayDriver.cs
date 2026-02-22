using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Global.Helper;
using Global.Configuration;
using NetLock_RMM_Agent_Remote;

namespace Windows.Helper.ScreenControl
{
    /// <summary>
    /// Manages virtual display drivers for remote desktop sessions
    /// </summary>
    internal class VirtualDisplayDriver
    {
        // Hardware ID for the virtual display driver
        private const string VIRTUAL_DISPLAY_HWID = "Root\\MttVDD";
        
        /// <summary>
        /// Checks if the virtual display driver is installed
        /// </summary>
        public static async Task<string> CheckStatus()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    return "driver_not_installed";

                bool isInstalled = await IsDriverInstalled();
                
                if (Agent.debug_mode)
                    Logging.Debug("VirtualDisplayDriver.CheckStatus", "Driver status checked", 
                        isInstalled ? "driver_installed" : "driver_not_installed");

                return isInstalled ? "driver_installed" : "driver_not_installed";
            }
            catch (Exception ex)
            {
                Logging.Error("VirtualDisplayDriver.CheckStatus", "Failed to check driver status", ex.ToString());
                return "driver_not_installed";
            }
        }
        
        /// <summary>
        /// Installs the virtual display driver via DevCon
        /// </summary>
        public static async Task<string> InstallDriver()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    return "Virtual display driver is only supported on Windows";

                if (Agent.debug_mode)
                    Logging.Debug("VirtualDisplayDriver.InstallDriver", "Starting driver installation", "");

                string devconPath = Application_Paths.devConPath;
                string infPath = Application_Paths.virtualDisplayDriverInfPath;
                string dllPath = Application_Paths.virtualDisplayDriverDllPath;
                string catPath = Application_Paths.virtualDisplayDriverCatPath;

                // Check DevCon
                if (!File.Exists(devconPath))
                    return $"DevCon not found at: {devconPath}";

                // Check if driver is already installed
                bool isInstalled = await IsDriverInstalled();
                
                if (isInstalled)
                {
                    if (Agent.debug_mode)
                        Logging.Debug("VirtualDisplayDriver.InstallDriver", "Driver already installed", "Skipping installation");
                    
                    return "Driver already installed.";
                }

                // Check driver files
                if (!File.Exists(infPath))
                    return $"Driver INF file not found at: {infPath}";
                
                if (!File.Exists(dllPath))
                    return $"Driver DLL file not found at: {dllPath}";
                
                if (!File.Exists(catPath))
                    return $"Driver CAT file not found at: {catPath}";

                if (Agent.debug_mode)
                    Logging.Debug("VirtualDisplayDriver.InstallDriver", "All files found", 
                        $"DevCon: {devconPath}, INF: {infPath}");

                // Execute installation in separate task with timeout
                var installTask = Task.Run(() =>
                {
                    try
                    {
                        var processInfo = new ProcessStartInfo
                        {
                            FileName = devconPath,
                            Arguments = $"install \"{infPath}\" {VIRTUAL_DISPLAY_HWID}",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using (var process = Process.Start(processInfo))
                        {
                            if (process == null)
                                return "Failed to start DevCon process";

                            // Read streams asynchronously to avoid deadlock
                            var outputTask = process.StandardOutput.ReadToEndAsync();
                            var errorTask = process.StandardError.ReadToEndAsync();

                            // 30 second timeout
                            if (!process.WaitForExit(30000))
                            {
                                try { process.Kill(); } catch { }
                                return "Driver installation timed out after 30 seconds";
                            }

                            // Wait for stream reads
                            Task.WaitAll(new Task[] { outputTask, errorTask }, 5000);
                            
                            string output = outputTask.IsCompleted ? outputTask.Result : "";
                            string error = errorTask.IsCompleted ? errorTask.Result : "";

                            if (process.ExitCode != 0)
                                return $"Driver installation failed (Exit Code: {process.ExitCode}): {error}\nOutput: {output}";

                            if (Agent.debug_mode)
                                Logging.Debug("VirtualDisplayDriver.InstallDriver", "driver_installed", output);

                            return output.Contains("Drivers installed successfully") || process.ExitCode == 0
                                ? "driver_installed"
                                : $"Installation completed with output: {output}";
                        }
                    }
                    catch (Exception ex)
                    {
                        return $"Driver installation exception: {ex.Message}";
                    }
                });

                // Wait for installation with additional timeout
                if (await Task.WhenAny(installTask, Task.Delay(35000)) == installTask)
                {
                    return await installTask;
                }
                else
                {
                    return "Driver installation operation timed out";
                }
            }
            catch (Exception ex)
            {
                Logging.Error("VirtualDisplayDriver.InstallDriver", "Failed to install driver", ex.ToString());
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Uninstalls the virtual display driver via DevCon
        /// </summary>
        public static async Task<string> UninstallDriver()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    return "Virtual display driver is only supported on Windows";

                if (Agent.debug_mode)
                    Logging.Debug("VirtualDisplayDriver.UninstallDriver", "Starting driver uninstallation", "");

                string devconPath = Application_Paths.devConPath;

                if (!File.Exists(devconPath))
                    return $"DevCon not found at: {devconPath}";

                // First remove all virtual displays (set monitor count to 0)
                await ResetMonitorCount();

                // Execute uninstallation in separate task with timeout
                var uninstallTask = Task.Run(() =>
                {
                    try
                    {
                        var processInfo = new ProcessStartInfo
                        {
                            FileName = devconPath,
                            Arguments = $"remove \"{VIRTUAL_DISPLAY_HWID}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using (var process = Process.Start(processInfo))
                        {
                            if (process == null)
                                return "Failed to start DevCon process";

                            // Read streams asynchronously to avoid deadlock
                            var outputTask = process.StandardOutput.ReadToEndAsync();
                            var errorTask = process.StandardError.ReadToEndAsync();

                            // 30 second timeout
                            if (!process.WaitForExit(30000))
                            {
                                try { process.Kill(); } catch { }
                                return "Driver uninstallation timed out after 30 seconds";
                            }

                            // Wait for stream reads
                            Task.WaitAll(new Task[] { outputTask, errorTask }, 5000);
                            
                            string output = outputTask.IsCompleted ? outputTask.Result : "";
                            string error = errorTask.IsCompleted ? errorTask.Result : "";

                            // Exit code 2 means "no devices found" - that's OK
                            if (process.ExitCode != 0 && process.ExitCode != 2)
                                return $"Driver uninstallation failed (Exit Code: {process.ExitCode}): {error}\nOutput: {output}";

                            if (Agent.debug_mode)
                                Logging.Debug("VirtualDisplayDriver.UninstallDriver", "Driver uninstalled successfully", output);

                            return "driver_uninstalled";
                        }
                    }
                    catch (Exception ex)
                    {
                        return $"Driver uninstallation exception: {ex.Message}";
                    }
                });

                // Wait for uninstallation with additional timeout
                if (await Task.WhenAny(uninstallTask, Task.Delay(35000)) == uninstallTask)
                {
                    return await uninstallTask;
                }
                else
                {
                    return "Driver uninstallation operation timed out";
                }
            }
            catch (Exception ex)
            {
                Logging.Error("VirtualDisplayDriver.UninstallDriver", "Failed to uninstall driver", ex.ToString());
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Applies an XML configuration for virtual displays
        /// </summary>
        /// <param name="configBase64">Base64-encoded XML configuration</param>
        /// <returns>Success or error message</returns>
        public static async Task<string> ApplyConfig(string configBase64)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                    return "Virtual display driver is only supported on Windows";

                if (string.IsNullOrEmpty(configBase64))
                    return "Error: configBase64 parameter is empty";

                if (Agent.debug_mode)
                    Logging.Debug("VirtualDisplayDriver.ApplyConfig", "Starting config application", "");

                // 1. Decode Base64
                string xmlContent;
                try
                {
                    xmlContent = await Base64.Decode(configBase64);
                    
                    if (Agent.debug_mode)
                        Logging.Debug("VirtualDisplayDriver.ApplyConfig", "Base64 decoded successfully", $"Length: {xmlContent.Length}");
                }
                catch (FormatException ex)
                {
                    return $"Error: Invalid Base64 format - {ex.Message}";
                }

                // 2. Validate XML
                try
                {
                    var xmlDoc = XDocument.Parse(xmlContent);
                    
                    // Check if root element is correct
                    if (xmlDoc.Root?.Name.LocalName != "vdd_settings")
                        return "Error: Invalid XML structure - root element must be 'vdd_settings'";
                    
                    // Check if monitors/count exists
                    var countElement = xmlDoc.Descendants("monitors").Descendants("count").FirstOrDefault();
                    if (countElement == null)
                        return "Error: Invalid XML structure - monitors/count element not found";
                    
                    if (!int.TryParse(countElement.Value, out int monitorCount) || monitorCount < 0 || monitorCount > 4)
                        return $"Error: Invalid monitor count. Must be between 0 and 4, got: {countElement.Value}";
                    
                    if (Agent.debug_mode)
                        Logging.Debug("VirtualDisplayDriver.ApplyConfig", "XML validated", $"Monitor count: {monitorCount}");
                }
                catch (Exception ex)
                {
                    return $"Error: Invalid XML format - {ex.Message}";
                }

                // 3. Save XML file
                string xmlPath = Application_Paths.virtualDisplayDriverSettingsPath;
                
                try
                {
                    // Create directory if not exists
                    string directory = Path.GetDirectoryName(xmlPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);
                    
                    // Write XML file
                    await File.WriteAllTextAsync(xmlPath, xmlContent, Encoding.UTF8);
                    
                    if (Agent.debug_mode)
                        Logging.Debug("VirtualDisplayDriver.ApplyConfig", "XML file saved", xmlPath);
                }
                catch (Exception ex)
                {
                    return $"Error: Failed to save XML file - {ex.Message}";
                }

                // 4. Apply configuration (reload driver)
                var reloadResult = await ReloadDriver();
                
                if (!reloadResult.Contains("success", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Configuration saved but driver reload failed: {reloadResult}";
                }

                // Wait briefly until displays are available
                await Task.Delay(1000);

                if (Agent.debug_mode)
                    Logging.Debug("VirtualDisplayDriver.ApplyConfig", "Configuration applied successfully", "");

                return "config_applied";
            }
            catch (Exception ex)
            {
                Logging.Error("VirtualDisplayDriver.ApplyConfig", "Failed to apply configuration", ex.ToString());
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Resets the monitor count to 0 (for uninstallation)
        /// </summary>
        private static async Task ResetMonitorCount()
        {
            try
            {
                string xmlPath = Application_Paths.virtualDisplayDriverSettingsPath;

                if (!File.Exists(xmlPath))
                    return;

                var xmlDoc = XDocument.Load(xmlPath);
                var countElement = xmlDoc.Descendants("monitors").Descendants("count").FirstOrDefault();
                
                if (countElement != null)
                {
                    countElement.Value = "0";
                    xmlDoc.Save(xmlPath);
                    
                    if (Agent.debug_mode)
                        Logging.Debug("VirtualDisplayDriver.ResetMonitorCount", "Monitor count reset to 0", "");
                    
                    // Reload driver
                    await ReloadDriver();
                }
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.Debug("VirtualDisplayDriver.ResetMonitorCount", "Failed to reset monitor count", ex.Message);
            }
        }

        /// <summary>
        /// Reloads the virtual display driver (disable/enable)
        /// </summary>
        private static async Task<string> ReloadDriver()
        {
            try
            {
                string devconPath = Application_Paths.devConPath;

                if (!File.Exists(devconPath))
                    return "DevCon not found";

                // Step 1: Disable
                var disableTask = Task.Run(() =>
                {
                    var processInfo = new ProcessStartInfo
                    {
                        FileName = devconPath,
                        Arguments = $"disable \"{VIRTUAL_DISPLAY_HWID}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(processInfo);
                    if (process == null) return false;
                    
                    process.WaitForExit(10000);
                    return process.ExitCode == 0 || process.ExitCode == 2; // 2 = no devices (OK)
                });

                if (!await disableTask)
                    return "Failed to disable driver";

                // Brief pause
                await Task.Delay(500);

                // Step 2: Enable
                var enableTask = Task.Run(() =>
                {
                    var processInfo = new ProcessStartInfo
                    {
                        FileName = devconPath,
                        Arguments = $"enable \"{VIRTUAL_DISPLAY_HWID}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(processInfo);
                    if (process == null) return false;
                    
                    process.WaitForExit(10000);
                    return process.ExitCode == 0;
                });

                if (!await enableTask)
                    return "Failed to enable driver";

                if (Agent.debug_mode)
                    Logging.Debug("VirtualDisplayDriver.ReloadDriver", "Driver reloaded successfully", "");

                return "Driver reload success";
            }
            catch (Exception ex)
            {
                Logging.Error("VirtualDisplayDriver.ReloadDriver", "Failed to reload driver", ex.ToString());
                return $"Reload error: {ex.Message}";
            }
        }

        /// <summary>
        /// Checks if the virtual display driver is already installed (via DevCon)
        /// </summary>
        private static async Task<bool> IsDriverInstalled()
        {
            try
            {
                string devconPath = Application_Paths.devConPath;

                if (!File.Exists(devconPath))
                {
                    if (Agent.debug_mode)
                        Logging.Debug("VirtualDisplayDriver.IsDriverInstalled", "DevCon not found", devconPath);
                    return false;
                }

                var processInfo = new ProcessStartInfo
                {
                    FileName = devconPath,
                    Arguments = "find \"*MttVDD*\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processInfo);
                        
                if (process == null)
                {
                    if (Agent.debug_mode)
                        Logging.Debug("VirtualDisplayDriver.IsDriverInstalled", "Failed to start DevCon", "");
                    return false;
                }

                string output = process.StandardOutput.ReadToEnd();
                        
                process.WaitForExit(10000);

                if (Agent.debug_mode)
                    Logging.Debug("VirtualDisplayDriver.IsDriverInstalled", 
                        $"DevCon find completed (Exit Code: {process.ExitCode})", 
                        $"Output: {output}");

                bool hasMatchingDevice = output.Contains("matching device(s) found", StringComparison.OrdinalIgnoreCase);

                return hasMatchingDevice;
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.Debug("VirtualDisplayDriver.IsDriverInstalled", "Check failed", ex.Message);
                
                return false;
            }
        }
    }
}

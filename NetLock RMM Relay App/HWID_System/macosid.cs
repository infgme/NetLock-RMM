using System;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Global.Helper;

namespace _x101.HWID_System
{
    internal class MacOsId
    {
        [System.Reflection.ObfuscationAttribute(Feature = "Virtualization", Exclude = false)]

        public static string GetMacOsId()
        {
            try
            {
                var identifiers = new StringBuilder();

                // Hardware UUID - most reliable on macOS
                string hardwareUuid = GetHardwareUuid();
                if (!string.IsNullOrEmpty(hardwareUuid))
                    identifiers.Append(hardwareUuid);

                // Serial Number - available on all Macs
                string serialNumber = GetSerialNumber();
                if (!string.IsNullOrEmpty(serialNumber))
                    identifiers.Append(serialNumber);

                // Platform UUID - additional identifier
                string platformUuid = GetPlatformUuid();
                if (!string.IsNullOrEmpty(platformUuid))
                    identifiers.Append(platformUuid);

                // Board ID
                string boardId = GetBoardId();
                if (!string.IsNullOrEmpty(boardId))
                    identifiers.Append(boardId);

                // MAC Address of primary network interface (en0)
                string macAddress = GetPrimaryMacAddress();
                if (!string.IsNullOrEmpty(macAddress))
                    identifiers.Append(macAddress);

                // Fallback: If nothing was found
                if (identifiers.Length == 0)
                {
                    string hostname = GetHostname();
                    string bootUuid = GetBootUuid();
                    identifiers.Append(hostname).Append(bootUuid);
                }

                // Generate SHA256 hash for better security
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(identifiers.ToString()));
                    return BitConverter.ToString(hashBytes).Replace("-", "");
                }
            }
            catch (Exception ex)
            {
                Logging.Error("MacOsId", "Error generating macOS HWID", ex.ToString());
                return "ND";
            }
        }

        private static string GetHardwareUuid()
        {
            try
            {
                // Use ioreg for Hardware UUID
                string output = ExecuteCommand("/usr/sbin/ioreg", "-d2 -c IOPlatformExpertDevice");
                
                if (!string.IsNullOrEmpty(output))
                {
                    // Search for IOPlatformUUID
                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Contains("IOPlatformUUID"))
                        {
                            // Format: "IOPlatformUUID" = "XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX"
                            var parts = line.Split('=');
                            if (parts.Length >= 2)
                            {
                                var uuid = parts[1].Trim().Trim('"');
                                if (!string.IsNullOrEmpty(uuid))
                                    return uuid;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("MacOsId", "Error reading Hardware UUID", ex.ToString());
            }
            return string.Empty;
        }

        private static string GetSerialNumber()
        {
            try
            {
                // system_profiler for Serial Number
                string output = ExecuteCommand("/usr/sbin/system_profiler", "SPHardwareDataType");
                
                if (!string.IsNullOrEmpty(output))
                {
                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Contains("Serial Number"))
                        {
                            var parts = line.Split(':');
                            if (parts.Length >= 2)
                            {
                                var serial = parts[1].Trim();
                                if (!string.IsNullOrEmpty(serial))
                                    return serial;
                            }
                        }
                    }
                }

                // Alternative: ioreg
                output = ExecuteCommand("/usr/sbin/ioreg", "-l");
                if (!string.IsNullOrEmpty(output))
                {
                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Contains("IOPlatformSerialNumber"))
                        {
                            var parts = line.Split('=');
                            if (parts.Length >= 2)
                            {
                                var serial = parts[1].Trim().Trim('"');
                                if (!string.IsNullOrEmpty(serial))
                                    return serial;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("MacOsId", "Error reading Serial Number", ex.ToString());
            }
            return string.Empty;
        }

        private static string GetPlatformUuid()
        {
            try
            {
                // Alternative: sysctl for Hardware UUID
                string output = ExecuteCommand("/usr/sbin/sysctl", "-n hw.uuid");
                if (!string.IsNullOrEmpty(output))
                    return output;
            }
            catch (Exception ex)
            {
                Logging.Error("MacOsId", "Error reading Platform UUID", ex.ToString());
            }
            return string.Empty;
        }

        private static string GetBoardId()
        {
            try
            {
                string output = ExecuteCommand("/usr/sbin/ioreg", "-d2 -c IOPlatformExpertDevice");
                
                if (!string.IsNullOrEmpty(output))
                {
                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Contains("board-id"))
                        {
                            var parts = line.Split('=');
                            if (parts.Length >= 2)
                            {
                                var boardId = parts[1].Trim().Trim('<', '>', '"');
                                if (!string.IsNullOrEmpty(boardId))
                                    return boardId;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("MacOsId", "Error reading Board ID", ex.ToString());
            }
            return string.Empty;
        }

        private static string GetPrimaryMacAddress()
        {
            try
            {
                // Read MAC address from en0 (primary network interface)
                string output = ExecuteCommand("/sbin/ifconfig", "en0");
                
                if (!string.IsNullOrEmpty(output))
                {
                    foreach (var line in output.Split('\n'))
                    {
                        var trimmedLine = line.Trim();
                        if (trimmedLine.StartsWith("ether "))
                        {
                            var parts = trimmedLine.Split(' ');
                            if (parts.Length >= 2)
                            {
                                var mac = parts[1].Trim();
                                if (!string.IsNullOrEmpty(mac))
                                    return mac;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("MacOsId", "Error reading MAC address", ex.ToString());
            }
            return string.Empty;
        }

        private static string GetBootUuid()
        {
            try
            {
                string output = ExecuteCommand("/usr/sbin/sysctl", "-n kern.bootsessionuuid");
                if (!string.IsNullOrEmpty(output))
                    return output;
            }
            catch (Exception ex)
            {
                Logging.Error("MacOsId", "Error reading boot UUID", ex.ToString());
            }
            return string.Empty;
        }

        private static string GetHostname()
        {
            try
            {
                string output = ExecuteCommand("/bin/hostname", "");
                if (!string.IsNullOrEmpty(output))
                    return output;
            }
            catch (Exception ex)
            {
                Logging.Error("MacOsId", "Error reading hostname", ex.ToString());
            }
            return string.Empty;
        }

        private static string ExecuteCommand(string command, string arguments)
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processInfo))
                {
                    if (process != null)
                    {
                        var output = process.StandardOutput.ReadToEnd().Trim();
                        process.WaitForExit(5000); // 5 seconds timeout
                        return output;
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("MacOsId", $"Error executing command {command}", ex.ToString());
            }
            return string.Empty;
        }
    }
}


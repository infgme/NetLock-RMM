using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Global.Helper;

namespace _x101.HWID_System
{
    internal class LinuxId
    {
        [System.Reflection.ObfuscationAttribute(Feature = "Virtualization", Exclude = false)]

        public static string GetLinuxId()
        {
            try
            {
                var identifiers = new StringBuilder();

                // Machine ID - universally available on all modern Linux systems
                string machineId = GetMachineId();
                if (!string.IsNullOrEmpty(machineId))
                    identifiers.Append(machineId);

                // Product UUID (DMI) - works on most physical and virtual machines
                string productUuid = GetProductUuid();
                if (!string.IsNullOrEmpty(productUuid))
                    identifiers.Append(productUuid);

                // Board Serial - if available
                string boardSerial = GetBoardSerial();
                if (!string.IsNullOrEmpty(boardSerial))
                    identifiers.Append(boardSerial);

                // CPU Info - always available
                string cpuInfo = GetCpuInfo();
                if (!string.IsNullOrEmpty(cpuInfo))
                    identifiers.Append(cpuInfo);

                // Disk ID from root filesystem
                string diskId = GetRootDiskId();
                if (!string.IsNullOrEmpty(diskId))
                    identifiers.Append(diskId);

                // Fallback: If nothing was found, use hostname and boot ID
                if (identifiers.Length == 0)
                {
                    string hostname = GetHostname();
                    string bootId = GetBootId();
                    identifiers.Append(hostname).Append(bootId);
                }

                // Generate SHA256 hash for better security than MD5
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(identifiers.ToString()));
                    return BitConverter.ToString(hashBytes).Replace("-", "");
                }
            }
            catch (Exception ex)
            {
                Logging.Error("LinuxId", "Error generating Linux HWID", ex.ToString());
                return "ND";
            }
        }

        private static string GetMachineId()
        {
            try
            {
                // /etc/machine-id is available on systemd-based systems (Ubuntu, Debian, Fedora, RHEL, Arch, etc.)
                if (File.Exists("/etc/machine-id"))
                    return File.ReadAllText("/etc/machine-id").Trim();

                // /var/lib/dbus/machine-id is an alternative (used by some older systems)
                if (File.Exists("/var/lib/dbus/machine-id"))
                    return File.ReadAllText("/var/lib/dbus/machine-id").Trim();
            }
            catch (Exception ex)
            {
                Logging.Error("LinuxId", "Error reading machine-id", ex.ToString());
            }
            return string.Empty;
        }

        private static string GetProductUuid()
        {
            try
            {
                // DMI Product UUID - works on most systems with read permission
                if (File.Exists("/sys/class/dmi/id/product_uuid"))
                {
                    string uuid = File.ReadAllText("/sys/class/dmi/id/product_uuid").Trim();
                    if (!string.IsNullOrEmpty(uuid) && uuid != "00000000-0000-0000-0000-000000000000")
                        return uuid;
                }
            }
            catch
            {
                // No permission or not available - normal on some systems
            }
            return string.Empty;
        }

        private static string GetBoardSerial()
        {
            try
            {
                if (File.Exists("/sys/class/dmi/id/board_serial"))
                {
                    string serial = File.ReadAllText("/sys/class/dmi/id/board_serial").Trim();
                    if (!string.IsNullOrEmpty(serial) && serial != "None" && serial != "Default string")
                        return serial;
                }
            }
            catch
            {
                // No permission or not available
            }
            return string.Empty;
        }

        private static string GetCpuInfo()
        {
            try
            {
                if (File.Exists("/proc/cpuinfo"))
                {
                    var lines = File.ReadAllLines("/proc/cpuinfo");
                    
                    // For x86/x64: processor, model name
                    var processorLine = lines.FirstOrDefault(l => l.StartsWith("processor"));
                    var modelLine = lines.FirstOrDefault(l => l.StartsWith("model name"));
                    
                    // For ARM: Hardware, Serial (if available)
                    var hardwareLine = lines.FirstOrDefault(l => l.StartsWith("Hardware"));
                    var serialLine = lines.FirstOrDefault(l => l.StartsWith("Serial"));
                    
                    var cpuInfo = new StringBuilder();
                    if (processorLine != null) cpuInfo.Append(processorLine);
                    if (modelLine != null) cpuInfo.Append(modelLine);
                    if (hardwareLine != null) cpuInfo.Append(hardwareLine);
                    if (serialLine != null) cpuInfo.Append(serialLine);
                    
                    return cpuInfo.ToString();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("LinuxId", "Error reading CPU info", ex.ToString());
            }
            return string.Empty;
        }

        private static string GetRootDiskId()
        {
            try
            {
                // Find the root filesystem device
                var mountInfo = File.ReadAllLines("/proc/mounts");
                var rootMount = mountInfo.FirstOrDefault(l => l.Contains(" / "));
                
                if (rootMount != null)
                {
                    var device = rootMount.Split(' ')[0];
                    
                    // If it's a /dev/mapper or /dev/dm- device, try to find the UUID
                    if (device.StartsWith("/dev/"))
                    {
                        // Try to read UUID from /dev/disk/by-uuid
                        if (Directory.Exists("/dev/disk/by-uuid"))
                        {
                            var uuidLinks = Directory.GetFiles("/dev/disk/by-uuid");
                            foreach (var link in uuidLinks)
                            {
                                try
                                {
                                    var target = new FileInfo(link).LinkTarget;
                                    if (target != null && target.Contains(Path.GetFileName(device)))
                                    {
                                        return Path.GetFileName(link);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                
                // Alternative: read directly from blkid (if available and executable)
                var blkidOutput = ExecuteCommand("blkid", "-s UUID -o value /");
                if (!string.IsNullOrEmpty(blkidOutput))
                    return blkidOutput;
            }
            catch (Exception ex)
            {
                Logging.Error("LinuxId", "Error reading disk ID", ex.ToString());
            }
            return string.Empty;
        }

        private static string GetBootId()
        {
            try
            {
                // Boot ID changes with every reboot, but useful as fallback
                if (File.Exists("/proc/sys/kernel/random/boot_id"))
                    return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            }
            catch (Exception ex)
            {
                Logging.Error("LinuxId", "Error reading boot ID", ex.ToString());
            }
            return string.Empty;
        }

        private static string GetHostname()
        {
            try
            {
                if (File.Exists("/etc/hostname"))
                    return File.ReadAllText("/etc/hostname").Trim();
                
                return ExecuteCommand("hostname", "");
            }
            catch (Exception ex)
            {
                Logging.Error("LinuxId", "Error reading hostname", ex.ToString());
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
                Logging.Error("LinuxId", $"Error executing command {command}", ex.ToString());
            }
            return string.Empty;
        }
    }
}


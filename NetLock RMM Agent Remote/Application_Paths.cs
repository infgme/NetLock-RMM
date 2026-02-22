using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace NetLock_RMM_Agent_Remote
{
    internal class Application_Paths
    {
        public static string program_data = Path.Combine(GetBasePath_CommonApplicationData(), "0x101 Cyber Security", "NetLock RMM", "Remote Agent");
        public static string program_data_logs = Path.Combine(GetBasePath_CommonApplicationData(), "0x101 Cyber Security", "NetLock RMM", "Remote Agent", "Logs");

        public static string program_data_debug_txt = Path.Combine(GetBasePath_CommonApplicationData(), "0x101 Cyber Security", "NetLock RMM", "Remote Agent", "debug.txt");
        public static string program_data_scripts = Path.Combine(GetBasePath_CommonApplicationData(), "0x101 Cyber Security", "NetLock RMM", "Remote Agent", "Scripts");
        
        public static string relay_server_fingerprints_json = Path.Combine(GetBasePath_CommonApplicationData(), "0x101 Cyber Security", "NetLock RMM", "Remote Agent", "relay_server_fingerprints.json");

        public static string netlock_rmm_user_agent_path = GetUserAgentPath();
        public static string netlock_rmm_user_agent_uac_path = GetUserAgentUacPath();

        private static string GetUserAgentPath()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Path.Combine(GetBasePath_ProgramFiles(), "0x101 Cyber Security", "NetLock RMM", "User Process", "NetLock_RMM_User_Process.exe");
            }
            else
            {
                // Linux and macOS use underscores instead of spaces in paths
                return Path.Combine(GetBasePath_ProgramFiles(), "0x101_Cyber_Security", "NetLock_RMM", "User_Process", "NetLock_RMM_User_Process");
            }
        }

        private static string GetUserAgentUacPath()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Path.Combine(GetBasePath_ProgramFiles(), "0x101 Cyber Security", "NetLock RMM", "User Process", "NetLock_RMM_User_Process_UAC.exe");
            }
            else
            {
                // Linux and macOS don't have UAC - use the regular user process path
                return GetUserAgentPath();
            }
        }

        public static string program_files_tray_icon_path = GetTrayIconPath();
        
        private static string GetTrayIconPath()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Path.Combine(GetBasePath_ProgramFiles(), "0x101 Cyber Security", "NetLock RMM", "Tray Icon", "NetLock_RMM_Tray_Icon.exe");
            }
            else
            {
                // Linux and macOS use underscores instead of spaces in paths
                return Path.Combine(GetBasePath_ProgramFiles(), "0x101_Cyber_Security", "NetLock_RMM", "Tray_Icon", "NetLock_RMM_Tray_Icon");
            }
        }
        
        public static string program_data_server_config_json = Path.Combine(GetBasePath_CommonApplicationData(), "0x101 Cyber Security", "NetLock RMM", "Comm Agent", "server_config.json");

        public static string device_identity_json_path = Path.Combine(GetBasePath_CommonApplicationData(), "0x101 Cyber Security", "NetLock RMM", "Comm Agent", "device_identity.json");

        public static string agent_settings_json_path = Path.Combine(GetBasePath_CommonApplicationData(), "0x101 Cyber Security", "NetLock RMM", "Comm Agent", "agent_config.json");
        public static string tray_icon_settings_json_path = Path.Combine(GetBasePath_CommonApplicationData(), "0x101 Cyber Security", "NetLock RMM", "Comm Agent", "Tray Icon", "config.json");

        // Virtual Display Driver Paths (architekturspezifisch)
        private static string GetDisplayDriverArchitecture()
        {
            // Ermittle die aktuelle System-Architektur
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => "x64" // Fallback auf x64
            };
        }
        
        // DevCon.exe Pfad (liegt im DisplayDriver root)
        public static string devConPath = Path.Combine(
            GetBasePath_ProgramFiles(), 
            "0x101 Cyber Security", 
            "NetLock RMM", 
            "Remote Agent", 
            "DisplayDriver",
            "devcon.exe");
        
        public static string virtualDisplayDriverPath = Path.Combine(
            GetBasePath_ProgramFiles(), 
            "0x101 Cyber Security", 
            "NetLock RMM", 
            "Remote Agent", 
            "DisplayDriver",
            GetDisplayDriverArchitecture());
            
        public static string virtualDisplayDriverInfPath = Path.Combine(virtualDisplayDriverPath, "MttVDD.inf");
        public static string virtualDisplayDriverDllPath = Path.Combine(virtualDisplayDriverPath, "MttVDD.dll");
        public static string virtualDisplayDriverCatPath = Path.Combine(virtualDisplayDriverPath, "mttvdd.cat");
        
        public static string virtualDisplayDriverSettingsPath = Path.Combine("C:\\VirtualDisplayDriver", "vdd_settings.xml");
        
        private static string GetBasePath_CommonApplicationData()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "/var";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "/Library/Application Support";
            }
            else if (OperatingSystem.IsMacOS())
            {
                return "/Library/Application Support";
            }
            else
            {
                throw new NotSupportedException("Unsupported OS");
            }
        }

        private static string GetBasePath_ProgramFiles()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "/usr";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || OperatingSystem.IsMacOS())
            {
                return "/usr/local/bin";
            }
            else
            {
                throw new NotSupportedException("Unsupported OS");
            }
        }
    }
}

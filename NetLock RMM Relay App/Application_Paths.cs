using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NetLock_RMM_Relay_App
{
    public static class Application_Paths
    {
        public static readonly string application_data_directory = GetApplicationDataDirectory();
        public static readonly string NetLockUserDir = application_data_directory;
        public static readonly string LogsDir = Path.Combine(application_data_directory, "Logs");
        public static readonly string TempDir = Path.Combine(application_data_directory, "Temp");
        public static readonly string program_data_debug_txt = Path.Combine(application_data_directory, "debug.txt");

        private static string GetApplicationDataDirectory()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetLock RMM Relay App");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netlock-relay-app");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "NetLock RMM Relay App");
            }
            else
            {
                throw new NotSupportedException("Unsupported OS");
            }
        }
    }
}


using Global.Helper;
using System;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace _x101.HWID_System
{
    public static class ENGINE
    {
        [System.Reflection.ObfuscationAttribute(Feature = "Virtualization", Exclude = false)]

        public static string HW_UID { get; private set; }

        static ENGINE()
        {
            if (OperatingSystem.IsWindows())
            {
                // Check if x64 or arm64
                if (RuntimeInformation.ProcessArchitecture.Equals(Architecture.X64))
                {
                    var cpuId = CpuId.GetCpuId();
                    var windowsId = WindowsId.GetWindowsId();
                    HW_UID = windowsId + cpuId;
                }
                else if (RuntimeInformation.ProcessArchitecture.Equals(Architecture.Arm64))
                {
                    var windowsId = WindowsId.GetWindowsId();
                    HW_UID = windowsId;
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                HW_UID = LinuxId.GetLinuxId();
            }
            else if (OperatingSystem.IsMacOS())
            {
                HW_UID = MacOsId.GetMacOsId();
            }
            else
            {
                HW_UID = "unknown platform";
            }
        }
    }
}
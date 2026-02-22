using System;
using System.Runtime.InteropServices;

namespace NetLock_RMM_User_Process.Windows.Helper
{
    /// <summary>
    /// Provides functionality to blank/unblank the screen
    /// Useful for privacy during remote sessions
    /// </summary>
    public static class ScreenBlanker
    {
        // External C function - needs to be implemented in native code
        // For now, we use a Windows API approach
        
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_MONITORPOWER = 0xF170;
        private const int MONITOR_ON = -1;
        private const int MONITOR_OFF = 2;
        private const int MONITOR_STANDBY = 1;

        private static bool _isBlanked = false;

        /// <summary>
        /// Blanks (turns off) all monitors
        /// </summary>
        public static bool BlankScreen()
        {
            try
            {
                IntPtr hwnd = GetDesktopWindow();
                SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)MONITOR_OFF);
                _isBlanked = true;
                Console.WriteLine("Screen blanked successfully");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to blank screen: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Unblanks (turns on) all monitors
        /// </summary>
        public static bool UnblankScreen()
        {
            try
            {
                IntPtr hwnd = GetDesktopWindow();
                SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)MONITOR_ON);
                _isBlanked = false;
                Console.WriteLine("Screen unblanked successfully");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to unblank screen: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Toggles screen blank state
        /// </summary>
        public static bool ToggleBlank()
        {
            return _isBlanked ? UnblankScreen() : BlankScreen();
        }

        /// <summary>
        /// Gets current blank state
        /// </summary>
        public static bool IsBlanked => _isBlanked;

        /// <summary>
        /// Sets monitor to standby mode
        /// </summary>
        public static bool StandbyScreen()
        {
            try
            {
                IntPtr hwnd = GetDesktopWindow();
                SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)MONITOR_STANDBY);
                Console.WriteLine("Screen set to standby");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set screen to standby: {ex.Message}");
                return false;
            }
        }
    }
}


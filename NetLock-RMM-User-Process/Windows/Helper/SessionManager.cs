using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetLock_RMM_User_Process.Windows.Helper
{
    /// <summary>
    /// Manages Windows session changes and desktop switching 
    /// This allows seamless transition from login screen to user session without restart
    /// </summary>
    public class SessionManager
    {
        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint dwProcessId, ref uint pSessionId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(uint dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateDesktop(
            string lpszDesktop,
            IntPtr lpszDevice,
            IntPtr pDevmode,
            uint dwFlags,
            uint dwDesiredAccess,
            IntPtr lpsa);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SwitchDesktop(IntPtr hDesktop);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetUserObjectInformation(
            IntPtr hObj,
            int nIndex,
            [Out] byte[] pvInfo,
            uint nLength,
            out uint lpnLengthNeeded);

        private const int UOI_NAME = 2;

        private const uint DESKTOP_READOBJECTS = 0x0001;
        private const uint DESKTOP_CREATEWINDOW = 0x0002;
        private const uint DESKTOP_CREATEMENU = 0x0004;
        private const uint DESKTOP_HOOKCONTROL = 0x0008;
        private const uint DESKTOP_JOURNALRECORD = 0x0010;
        private const uint DESKTOP_JOURNALPLAYBACK = 0x0020;
        private const uint DESKTOP_ENUMERATE = 0x0040;
        private const uint DESKTOP_WRITEOBJECTS = 0x0080;
        private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
        private const uint GENERIC_ALL = 0x10000000;

        private const uint NOTIFY_FOR_THIS_SESSION = 0;
        private const uint NOTIFY_FOR_ALL_SESSIONS = 1;

        private static uint _currentSessionId = 0;
        private static IntPtr _currentDesktop = IntPtr.Zero;
        private static bool _isMonitoring = false;
        private static readonly object _lockObject = new object();

        public delegate void SessionChangedHandler(uint oldSessionId, uint newSessionId);
        public static event SessionChangedHandler OnSessionChanged;

        /// <summary>
        /// Gets the current process session ID
        /// </summary>
        public static uint GetCurrentProcessSessionId()
        {
            uint sessionId = 0;
            uint processId = GetCurrentProcessId();
            ProcessIdToSessionId(processId, ref sessionId);
            return sessionId;
        }

        /// <summary>
        /// Gets the active console session ID
        /// </summary>
        public static uint GetActiveConsoleSessionId()
        {
            return WTSGetActiveConsoleSessionId();
        }

        /// <summary>
        /// Switches to the input desktop (handles login screen, UAC, Ctrl+Alt+Del, etc.)
        /// This is critical for seeing the login screen and secure desktops
        /// </summary>
        public static bool TrySwitchToInputDesktop()
        {
            try
            {
                IntPtr inputDesktop = OpenInputDesktop(0, false, GENERIC_ALL);
                
                if (inputDesktop == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    Console.WriteLine($"Failed to open input desktop. Error: {error}");
                    return false;
                }

                IntPtr currentDesktop = GetThreadDesktop(GetCurrentThreadId());
                
                // Only switch if different
                if (currentDesktop != inputDesktop)
                {
                    bool success = SetThreadDesktop(inputDesktop);
                    
                    if (!success)
                    {
                        int error = Marshal.GetLastWin32Error();
                        Console.WriteLine($"Failed to set thread desktop. Error: {error}");
                        CloseDesktop(inputDesktop);
                        return false;
                    }

                    Console.WriteLine("Successfully switched to input desktop");
                    
                    // Close old desktop if we had one
                    if (_currentDesktop != IntPtr.Zero && _currentDesktop != currentDesktop)
                    {
                        CloseDesktop(_currentDesktop);
                    }
                    
                    _currentDesktop = inputDesktop;
                }
                else
                {
                    CloseDesktop(inputDesktop);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error switching to input desktop: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the name of a desktop from its handle
        /// </summary>
        private static string GetDesktopName(IntPtr hDesktop)
        {
            if (hDesktop == IntPtr.Zero)
                return null;

            byte[] buffer = new byte[256];
            if (GetUserObjectInformation(hDesktop, UOI_NAME, buffer, (uint)buffer.Length, out uint lengthNeeded))
            {
                return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)lengthNeeded - 2); // -2 for null terminator
            }
            return null;
        }

        /// <summary>
        /// Checks if the current desktop is the input desktop
        /// </summary>
        public static bool IsOnInputDesktop()
        {
            try
            {
                IntPtr currentDesktop = GetThreadDesktop(GetCurrentThreadId());
                IntPtr inputDesktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
                
                if (inputDesktop == IntPtr.Zero)
                {
                    return false;
                }

                // Compare desktop names instead of handles
                string currentName = GetDesktopName(currentDesktop);
                string inputName = GetDesktopName(inputDesktop);
                
                CloseDesktop(inputDesktop);
                
                bool isInput = !string.IsNullOrEmpty(currentName) && 
                               !string.IsNullOrEmpty(inputName) && 
                               string.Equals(currentName, inputName, StringComparison.OrdinalIgnoreCase);
                
                return isInput;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Starts monitoring session changes
        /// This allows seamless transition without process restart
        /// </summary>
        public static void StartSessionMonitoring(int checkIntervalMs = 1000)
        {
            if (_isMonitoring)
            {
                Console.WriteLine("Session monitoring already started");
                return;
            }

            _isMonitoring = true;
            _currentSessionId = GetCurrentProcessSessionId();
            
            Console.WriteLine($"Starting session monitoring. Current session: {_currentSessionId}");

            Task.Run(async () =>
            {
                while (_isMonitoring)
                {
                    try
                    {
                        uint activeSession = GetActiveConsoleSessionId();
                        
                        // Check if session changed
                        if (activeSession != 0xFFFFFFFF && activeSession != _currentSessionId)
                        {
                            Console.WriteLine($"Session changed from {_currentSessionId} to {activeSession}");
                            
                            uint oldSessionId = _currentSessionId;
                            _currentSessionId = activeSession;
                            
                            // Try to switch to input desktop immediately
                            TrySwitchToInputDesktop();
                            
                            // Notify listeners
                            OnSessionChanged?.Invoke(oldSessionId, activeSession);
                        }
                        else
                        {
                            // Even if session didn't change, periodically try to ensure we're on input desktop
                            // This handles cases like UAC prompts, Ctrl+Alt+Del, etc.
                            if (!IsOnInputDesktop())
                            {
                                Console.WriteLine("Not on input desktop, switching...");
                                TrySwitchToInputDesktop();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in session monitoring: {ex.Message}");
                    }

                    await Task.Delay(checkIntervalMs);
                }
            });
        }

        /// <summary>
        /// Stops session monitoring
        /// </summary>
        public static void StopSessionMonitoring()
        {
            _isMonitoring = false;
            
            if (_currentDesktop != IntPtr.Zero)
            {
                CloseDesktop(_currentDesktop);
                _currentDesktop = IntPtr.Zero;
            }
            
            Console.WriteLine("Session monitoring stopped");
        }

        /// <summary>
        /// Force refresh desktop - useful after session changes
        /// </summary>
        public static void RefreshDesktop()
        {
            TrySwitchToInputDesktop();
        }
    }
}


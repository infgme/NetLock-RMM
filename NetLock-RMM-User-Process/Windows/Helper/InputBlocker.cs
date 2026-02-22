using System;
using System.Runtime.InteropServices;

namespace NetLock_RMM_User_Process.Windows.Helper
{
    /// <summary>
    /// Provides functionality to block/unblock user input (keyboard and mouse)
    /// Useful for remote support scenarios where you need to prevent user interference
    /// </summary>
    public static class InputBlocker
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool fBlockIt);

        private static bool _isBlocked = false;

        /// <summary>
        /// Blocks all keyboard and mouse input events
        /// Returns tuple: (success, errorMessage)
        /// </summary>
        public static (bool success, string errorMessage) BlockUserInput()
        {
            try
            {
                if (BlockInput(true))
                {
                    _isBlocked = true;
                    Console.WriteLine("User input blocked successfully");
                    return (true, string.Empty);
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    string errorMsg = $"Failed to block input. Error code: {error}";
                    Console.WriteLine(errorMsg);
                    return (false, errorMsg);
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"Exception blocking input: {ex.Message}";
                Console.WriteLine(errorMsg);
                return (false, errorMsg);
            }
        }

        /// <summary>
        /// Unblocks all keyboard and mouse input events
        /// Returns tuple: (success, errorMessage)
        /// </summary>
        public static (bool success, string errorMessage) UnblockUserInput()
        {
            try
            {
                if (BlockInput(false))
                {
                    _isBlocked = false;
                    Console.WriteLine("User input unblocked successfully");
                    return (true, string.Empty);
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    string errorMsg = $"Failed to unblock input. Error code: {error}";
                    Console.WriteLine(errorMsg);
                    return (false, errorMsg);
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"Exception unblocking input: {ex.Message}";
                Console.WriteLine(errorMsg);
                return (false, errorMsg);
            }
        }

        /// <summary>
        /// Gets current block state
        /// </summary>
        public static bool IsBlocked => _isBlocked;

        /// <summary>
        /// Toggles input blocking state
        /// </summary>
        public static (bool success, string errorMessage) ToggleBlock()
        {
            return _isBlocked ? UnblockUserInput() : BlockUserInput();
        }
    }
}


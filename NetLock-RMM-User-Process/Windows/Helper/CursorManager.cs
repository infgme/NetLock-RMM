using System;
using System.Runtime.InteropServices;

namespace NetLock_RMM_User_Process.Windows.Helper
{
    /// <summary>
    /// Provides cursor visibility detection and manipulation
    /// </summary>
    public static class CursorManager
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CURSORINFO pci);

        [DllImport("user32.dll")]
        private static extern IntPtr GetCursor();

        [DllImport("user32.dll")]
        private static extern bool ShowCursor(bool bShow);

        private const int CURSOR_SHOWING = 0x00000001;
        private const int CURSOR_SUPPRESSED = 0x00000002;

        /// <summary>
        /// Returns null if cursor is hidden
        /// </summary>
        public static IntPtr? GetCurrentCursor()
        {
            try
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(ci);

                if (GetCursorInfo(ref ci))
                {
                    // Check if cursor is visible
                    if ((ci.flags & CURSOR_SHOWING) == 0)
                    {
                        return null; // Cursor is hidden
                    }
                    return ci.hCursor;
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    Console.WriteLine($"GetCursorInfo failed with error: {error}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting cursor: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets current cursor position
        /// Returns tuple (x, y) or null if failed
        /// </summary>
        public static (int x, int y)? GetCursorPosition()
        {
            try
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(ci);

                if (GetCursorInfo(ref ci))
                {
                    return (ci.ptScreenPos.X, ci.ptScreenPos.Y);
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting cursor position: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks if cursor is currently visible
        /// </summary>
        public static bool IsCursorVisible()
        {
            try
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(ci);

                if (GetCursorInfo(ref ci))
                {
                    return (ci.flags & CURSOR_SHOWING) != 0;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Shows or hides the cursor
        /// Note: This affects cursor display count, may need multiple calls to fully show/hide
        /// </summary>
        public static void SetCursorVisibility(bool visible)
        {
            try
            {
                ShowCursor(visible);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting cursor visibility: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets detailed cursor information
        /// </summary>
        public static CursorInfo? GetCursorInfo()
        {
            try
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(ci);

                if (GetCursorInfo(ref ci))
                {
                    return new CursorInfo
                    {
                        Handle = ci.hCursor,
                        X = ci.ptScreenPos.X,
                        Y = ci.ptScreenPos.Y,
                        IsVisible = (ci.flags & CURSOR_SHOWING) != 0,
                        IsSuppressed = (ci.flags & CURSOR_SUPPRESSED) != 0
                    };
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting cursor info: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Cursor information structure
    /// </summary>
    public class CursorInfo
    {
        public IntPtr Handle { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool IsVisible { get; set; }
        public bool IsSuppressed { get; set; }
    }
}


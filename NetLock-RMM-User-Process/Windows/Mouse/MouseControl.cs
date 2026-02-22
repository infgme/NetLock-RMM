using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using NetLock_RMM_User_Process.Windows.ScreenControl;

namespace NetLock_RMM_User_Process.Windows.Mouse
{
    internal class MouseControl
    {
        // P/Invoke für SetCursorPos
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        // P/Invoke für mouse_event (deprecated, but still used for compatibility)
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        // P/Invoke für SendInput (modern, preferred method)
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // P/Invoke für GetDoubleClickTime - returns system double-click time in milliseconds
        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        // Structures for SendInput
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // Input type constants
        private const uint INPUT_MOUSE = 0;

        // Konstanten für die Mausereignisse
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        
        // Timing-Konstanten für stabile Maus-Events
        private const int CLICK_DELAY_MS = 30;           // Delay zwischen Down und Up (reduziert für bessere Reaktion)
        private const int MOVE_SETTLE_DELAY_MS = 10;     // Kurze Pause nach Bewegung
        
        // Lazy initialization of system double-click time
        private static readonly Lazy<int> SystemDoubleClickTime = new Lazy<int>(() =>
        {
            uint time = GetDoubleClickTime();
            // Use half of system time for more reliable double-clicks
            // Windows default is usually 500ms, we use half to ensure we're within the window
            return (int)(time / 2);
        });
        
        // Thread-Synchronisation für sequentielle Maus-Events
        private static readonly SemaphoreSlim _mouseLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Modern way to send mouse events using SendInput (preferred over mouse_event)
        /// </summary>
        private static bool SendMouseInput(uint flags, uint mouseData = 0)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_MOUSE;
            inputs[0].mi.dx = 0;
            inputs[0].mi.dy = 0;
            inputs[0].mi.mouseData = mouseData;
            inputs[0].mi.dwFlags = flags;
            inputs[0].mi.time = 0;
            inputs[0].mi.dwExtraInfo = IntPtr.Zero;

            uint result = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            return result == 1;
        }

        // P/Invoke für Monitorinformationen
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public uint Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        public delegate bool MonitorEnumDelegate(nint hMonitor, nint hdcMonitor, ref Rect lprcMonitor, nint dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumDelegate lpfnEnum, nint dwData);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

        // Methode zum Abrufen aller Bildschirme
        public static Rect[] GetAllScreens()
        {
            var screens = new List<Rect>();

            EnumDisplayMonitors(nint.Zero, nint.Zero, (nint hMonitor, nint hdcMonitor, ref Rect lprcMonitor, nint dwData) =>
            {
                MonitorInfo mi = new MonitorInfo();
                mi.Size = (uint)Marshal.SizeOf(mi);
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    screens.Add(mi.Monitor);
                }
                return true;
            }, nint.Zero);

            return screens.ToArray();
        }

        // Customized method for moving the mouse on the correct screen (with lock for clicks)
        public static async Task MoveMouse(int x, int y, int screenIndex)
        {
            await _mouseLock.WaitAsync();
            try
            {
                // Ensure we're on the input desktop before mouse operations
                Helper.SessionManager.TrySwitchToInputDesktop();
                
                MoveMouseInternal(x, y, screenIndex);
                
                // Small delay to let the cursor settle at the new position
                await Task.Delay(MOVE_SETTLE_DELAY_MS);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to move mouse: {ex.Message}");
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        // Non-blocking move for continuous mouse tracking (no lock, no delay)
        public static async Task MoveMouseNoLock(int x, int y, int screenIndex)
        {
            try
            {
                MoveMouseInternal(x, y, screenIndex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to move mouse (no lock): {ex.Message}");
            }
        }

        // Internal method that does the actual move logic
        private static void MoveMouseInternal(int x, int y, int screenIndex)
        {
            var screens = GetAllScreens();

            if (screenIndex >= screens.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(screenIndex), "Ungültiger Bildschirmindex.");
            }

            var screen = screens[screenIndex];
            
            // Translate coordinates based on scaling factors
            (int actualX, int actualY) = DesktopDuplicationApiCapture.TranslateCoordinates(screenIndex, x, y);

            // Calculate the absolute coordinates for the specified screen
            int absoluteX = screen.Left + actualX;
            int absoluteY = screen.Top + actualY;

            // Place the mouse pointer on the calculated absolute coordinates
            SetCursorPos(absoluteX, absoluteY);
        }

        public static async Task LeftClickMouse()
        {
            await _mouseLock.WaitAsync();
            try
            {
                // Ensure we're on the input desktop before mouse operations
                Helper.SessionManager.TrySwitchToInputDesktop();
                
                // Simulate a mouse click with proper timing using modern SendInput API
                if (!SendMouseInput(MOUSEEVENTF_LEFTDOWN))
                {
                    Console.WriteLine("Warning: SendInput failed for left mouse down");
                }
                await Task.Delay(CLICK_DELAY_MS);
                if (!SendMouseInput(MOUSEEVENTF_LEFTUP))
                {
                    Console.WriteLine("Warning: SendInput failed for left mouse up");
                }
                
                Console.WriteLine("Left click executed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to left click mouse: {ex.Message}");
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        // Right click
        public static async Task RightClickMouse()
        {
            await _mouseLock.WaitAsync();
            try
            {
                // Ensure we're on the input desktop before mouse operations
                Helper.SessionManager.TrySwitchToInputDesktop();
                
                // Simulate a right mouse click with proper timing using modern SendInput API
                if (!SendMouseInput(MOUSEEVENTF_RIGHTDOWN))
                {
                    Console.WriteLine("Warning: SendInput failed for right mouse down");
                }
                await Task.Delay(CLICK_DELAY_MS);
                if (!SendMouseInput(MOUSEEVENTF_RIGHTUP))
                {
                    Console.WriteLine("Warning: SendInput failed for right mouse up");
                }
                
                Console.WriteLine("Right click executed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to right click mouse: {ex.Message}");
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        public static async Task LeftMouseDown()
        {
            await _mouseLock.WaitAsync();
            try
            {
                // Ensure we're on the input desktop before mouse operations
                Helper.SessionManager.TrySwitchToInputDesktop();
                
                // Press (hold) the left mouse button using modern SendInput API
                if (!SendMouseInput(MOUSEEVENTF_LEFTDOWN))
                {
                    Console.WriteLine("Warning: SendInput failed for left mouse down");
                }
                Console.WriteLine("Left mouse down (drag started)");
                
                // Keep a small delay to ensure the down event is registered
                await Task.Delay(MOVE_SETTLE_DELAY_MS);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to left mouse down: {ex.Message}");
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        public static async Task LeftMouseUp()
        {
            await _mouseLock.WaitAsync();
            try
            {
                // Ensure we're on the input desktop before mouse operations
                Helper.SessionManager.TrySwitchToInputDesktop();
                
                // Release left mouse button using modern SendInput API
                if (!SendMouseInput(MOUSEEVENTF_LEFTUP))
                {
                    Console.WriteLine("Warning: SendInput failed for left mouse up");
                }
                Console.WriteLine("Left mouse up (drag ended)");
                
                // Small delay to ensure the up event is registered
                await Task.Delay(MOVE_SETTLE_DELAY_MS);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to left mouse up: {ex.Message}");
            }
            finally
            {
                _mouseLock.Release();
            }
        }
        
        // Double click - uses system double-click time for reliability
        public static async Task DoubleClickMouse()
        {
            await _mouseLock.WaitAsync();
            try
            {
                // Ensure we're on the input desktop before mouse operations
                Helper.SessionManager.TrySwitchToInputDesktop();
                
                // Get system double-click time (lazy loaded)
                int doubleClickDelay = SystemDoubleClickTime.Value;
                
                Console.WriteLine($"Executing double-click with {doubleClickDelay}ms interval (system: {GetDoubleClickTime()}ms)");
                
                // First click using modern SendInput API
                if (!SendMouseInput(MOUSEEVENTF_LEFTDOWN))
                {
                    Console.WriteLine("Warning: SendInput failed for first click down");
                }
                await Task.Delay(CLICK_DELAY_MS);
                if (!SendMouseInput(MOUSEEVENTF_LEFTUP))
                {
                    Console.WriteLine("Warning: SendInput failed for first click up");
                }
                
                // Pause between clicks - use system double-click time for reliability
                await Task.Delay(doubleClickDelay);
                
                // Second click using modern SendInput API
                if (!SendMouseInput(MOUSEEVENTF_LEFTDOWN))
                {
                    Console.WriteLine("Warning: SendInput failed for second click down");
                }
                await Task.Delay(CLICK_DELAY_MS);
                if (!SendMouseInput(MOUSEEVENTF_LEFTUP))
                {
                    Console.WriteLine("Warning: SendInput failed for second click up");
                }
                
                Console.WriteLine("Double click executed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to double click mouse: {ex.Message}");
            }
            finally
            {
                _mouseLock.Release();
            }
        }
    }
}

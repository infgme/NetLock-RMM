using System;
using System.Threading;
using System.Threading.Tasks;
using NetLock_RMM_User_Process.Linux.Helper;
using NetLock_RMM_User_Process.Linux.ScreenControl;

namespace NetLock_RMM_User_Process.Linux.Mouse
{
    /// <summary>
    /// Linux mouse control implementation using X11 XTest or CLI tools for Wayland
    /// </summary>
    internal class MouseControlLinux
    {
        private static IntPtr _display = IntPtr.Zero;
        private static readonly object _displayLock = new object();
        private static readonly SemaphoreSlim _mouseLock = new SemaphoreSlim(1, 1);
        
        // Click timing
        private const int CLICK_DELAY_MS = 30;
        private const int MOVE_SETTLE_DELAY_MS = 10;

        // Input tool for Wayland fallback
        private static string _inputTool;

        /// <summary>
        /// Initializes the mouse control
        /// </summary>
        private static bool EnsureInitialized()
        {
            lock (_displayLock)
            {
                if (DisplayServerDetector.IsX11)
                {
                    if (_display == IntPtr.Zero)
                    {
                        _display = LibX11.XOpenDisplay(null);
                        if (_display == IntPtr.Zero)
                        {
                            Console.WriteLine("[MouseControl] Failed to open X11 display");
                            return false;
                        }

                        if (!LibXtst.IsXTestAvailable(_display))
                        {
                            Console.WriteLine("[MouseControl] XTest extension not available");
                            LibX11.XCloseDisplay(_display);
                            _display = IntPtr.Zero;
                            return false;
                        }

                        Console.WriteLine("[MouseControl] X11 XTest initialized");
                    }
                    return true;
                }
                else if (DisplayServerDetector.IsWayland)
                {
                    if (string.IsNullOrEmpty(_inputTool))
                    {
                        _inputTool = DisplayServerDetector.GetAvailableInputTool();
                        if (string.IsNullOrEmpty(_inputTool))
                        {
                            Console.WriteLine("[MouseControl] No input tool found for Wayland");
                            Console.WriteLine("[MouseControl] Please install ydotool for Wayland support");
                            return false;
                        }
                        Console.WriteLine($"[MouseControl] Wayland initialized with tool: {_inputTool}");
                    }
                    return true;
                }
                
                return false;
            }
        }

        /// <summary>
        /// Translates scaled coordinates back to original screen coordinates
        /// </summary>
        private static (int x, int y) TranslateCoordinates(int x, int y, int screenIndex)
        {
            var scaling = ScreenCaptureLinux.GetScalingInfo(screenIndex);
            if (scaling.ScaleX > 0 && scaling.ScaleX < 1.0)
            {
                x = (int)(x / scaling.ScaleX);
                y = (int)(y / scaling.ScaleY);
            }
            return (x, y);
        }

        /// <summary>
        /// Moves the mouse to absolute coordinates
        /// </summary>
        public static async Task MoveMouse(int x, int y, int screenIndex)
        {
            await _mouseLock.WaitAsync();
            try
            {
                MoveMouseInternal(x, y, screenIndex);
                await Task.Delay(MOVE_SETTLE_DELAY_MS);
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        /// <summary>
        /// Moves the mouse without acquiring a lock (for move-only operations)
        /// </summary>
        public static async Task MoveMouseNoLock(int x, int y, int screenIndex)
        {
            MoveMouseInternal(x, y, screenIndex);
            await Task.CompletedTask;
        }

        private static void MoveMouseInternal(int x, int y, int screenIndex)
        {
            if (!EnsureInitialized())
                return;

            (x, y) = TranslateCoordinates(x, y, screenIndex);

            if (DisplayServerDetector.IsX11)
            {
                MoveMouseX11(x, y, screenIndex);
            }
            else if (DisplayServerDetector.IsWayland)
            {
                MoveMouseWayland(x, y);
            }
        }

        private static void MoveMouseX11(int x, int y, int screenIndex)
        {
            lock (_displayLock)
            {
                if (_display != IntPtr.Zero)
                {
                    LibXtst.XTestFakeMotionEvent(_display, screenIndex, x, y, 0);
                    LibX11.XFlush(_display);
                }
            }
        }

        private static void MoveMouseWayland(int x, int y)
        {
            // Try PipeWire RemoteDesktop portal first (most reliable for native Wayland)
            var pipeWire = ScreenCaptureLinux.ActivePipeWireCapture;
            if (pipeWire != null && pipeWire.SupportsRemoteInput)
            {
                pipeWire.MoveMouse(x, y);
                return;
            }
            
            // Fallback to CLI tools
            switch (_inputTool)
            {
                case "ydotool":
                    // ydotool uses absolute positioning with mousemove --absolute
                    Bash.ExecuteCommandAsync($"ydotool mousemove --absolute -x {x} -y {y}");
                    break;
                case "xdotool":
                    // xdotool works on XWayland
                    Bash.ExecuteCommandAsync($"xdotool mousemove {x} {y}");
                    break;
                default:
                    Console.WriteLine($"[MouseControl] Unsupported tool for mouse move: {_inputTool}");
                    break;
            }
        }

        /// <summary>
        /// Performs a left mouse click
        /// </summary>
        public static async Task LeftClickMouse()
        {
            await _mouseLock.WaitAsync();
            try
            {
                if (!EnsureInitialized())
                    return;

                if (DisplayServerDetector.IsX11)
                {
                    ClickX11(LibXtst.Button1);
                }
                else if (DisplayServerDetector.IsWayland)
                {
                    ClickWayland(1);
                }
                
                await Task.Delay(CLICK_DELAY_MS);
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        /// <summary>
        /// Performs a right mouse click
        /// </summary>
        public static async Task RightClickMouse()
        {
            await _mouseLock.WaitAsync();
            try
            {
                if (!EnsureInitialized())
                    return;

                if (DisplayServerDetector.IsX11)
                {
                    ClickX11(LibXtst.Button3);
                }
                else if (DisplayServerDetector.IsWayland)
                {
                    ClickWayland(3);
                }
                
                await Task.Delay(CLICK_DELAY_MS);
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        /// <summary>
        /// Performs a middle mouse click
        /// </summary>
        public static async Task MiddleClickMouse()
        {
            await _mouseLock.WaitAsync();
            try
            {
                if (!EnsureInitialized())
                    return;

                if (DisplayServerDetector.IsX11)
                {
                    ClickX11(LibXtst.Button2);
                }
                else if (DisplayServerDetector.IsWayland)
                {
                    ClickWayland(2);
                }
                
                await Task.Delay(CLICK_DELAY_MS);
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        /// <summary>
        /// Performs a double click
        /// </summary>
        public static async Task DoubleClickMouse()
        {
            await _mouseLock.WaitAsync();
            try
            {
                if (!EnsureInitialized())
                    return;

                if (DisplayServerDetector.IsX11)
                {
                    ClickX11(LibXtst.Button1);
                    Thread.Sleep(50);
                    ClickX11(LibXtst.Button1);
                }
                else if (DisplayServerDetector.IsWayland)
                {
                    switch (_inputTool)
                    {
                        case "ydotool":
                            Bash.ExecuteCommand("ydotool click 0xC0; sleep 0.05; ydotool click 0xC0");
                            break;
                        case "xdotool":
                            Bash.ExecuteCommand("xdotool click --repeat 2 --delay 50 1");
                            break;
                    }
                }
                
                await Task.Delay(CLICK_DELAY_MS);
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        /// <summary>
        /// Presses the left mouse button down
        /// </summary>
        public static async Task LeftMouseDown()
        {
            await _mouseLock.WaitAsync();
            try
            {
                if (!EnsureInitialized())
                    return;

                if (DisplayServerDetector.IsX11)
                {
                    MouseDownX11(LibXtst.Button1);
                }
                else if (DisplayServerDetector.IsWayland)
                {
                    MouseDownWayland(1);
                }
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        /// <summary>
        /// Releases the left mouse button
        /// </summary>
        public static async Task LeftMouseUp()
        {
            await _mouseLock.WaitAsync();
            try
            {
                if (!EnsureInitialized())
                    return;

                if (DisplayServerDetector.IsX11)
                {
                    MouseUpX11(LibXtst.Button1);
                }
                else if (DisplayServerDetector.IsWayland)
                {
                    MouseUpWayland(1);
                }
            }
            finally
            {
                _mouseLock.Release();
            }
        }

        private static void ClickX11(uint button)
        {
            lock (_displayLock)
            {
                if (_display != IntPtr.Zero)
                {
                    LibXtst.XTestFakeButtonEvent(_display, button, true, 0);
                    LibX11.XFlush(_display);
                    Thread.Sleep(CLICK_DELAY_MS);
                    LibXtst.XTestFakeButtonEvent(_display, button, false, 0);
                    LibX11.XFlush(_display);
                }
            }
        }

        private static void MouseDownX11(uint button)
        {
            lock (_displayLock)
            {
                if (_display != IntPtr.Zero)
                {
                    LibXtst.XTestFakeButtonEvent(_display, button, true, 0);
                    LibX11.XFlush(_display);
                }
            }
        }

        private static void MouseUpX11(uint button)
        {
            lock (_displayLock)
            {
                if (_display != IntPtr.Zero)
                {
                    LibXtst.XTestFakeButtonEvent(_display, button, false, 0);
                    LibX11.XFlush(_display);
                }
            }
        }

        private static void ClickWayland(int button)
        {
            // Try PipeWire RemoteDesktop portal first
            var pipeWire = ScreenCaptureLinux.ActivePipeWireCapture;
            if (pipeWire != null && pipeWire.SupportsRemoteInput)
            {
                pipeWire.ClickMouse(button);
                return;
            }
            
            // Fallback to CLI tools
            switch (_inputTool)
            {
                case "ydotool":
                    // ydotool button codes: 0xC0 = left, 0xC1 = right, 0xC2 = middle
                    string ydoButton = button switch
                    {
                        1 => "0xC0",  // Left
                        2 => "0xC2",  // Middle
                        3 => "0xC1",  // Right
                        _ => "0xC0"
                    };
                    Bash.ExecuteCommandAsync($"ydotool click {ydoButton}");
                    break;
                case "xdotool":
                    Bash.ExecuteCommandAsync($"xdotool click {button}");
                    break;
            }
        }

        private static void MouseDownWayland(int button)
        {
            // Try PipeWire RemoteDesktop portal first
            var pipeWire = ScreenCaptureLinux.ActivePipeWireCapture;
            if (pipeWire != null && pipeWire.SupportsRemoteInput)
            {
                pipeWire.MouseDown(button);
                return;
            }
            
            // Fallback to CLI tools
            switch (_inputTool)
            {
                case "ydotool":
                    // ydotool uses different syntax for down/up
                    Bash.ExecuteCommandAsync($"ydotool click --down 0xC{button - 1}");
                    break;
                case "xdotool":
                    Bash.ExecuteCommandAsync($"xdotool mousedown {button}");
                    break;
            }
        }

        private static void MouseUpWayland(int button)
        {
            // Try PipeWire RemoteDesktop portal first
            var pipeWire = ScreenCaptureLinux.ActivePipeWireCapture;
            if (pipeWire != null && pipeWire.SupportsRemoteInput)
            {
                pipeWire.MouseUp(button);
                return;
            }
            
            // Fallback to CLI tools
            switch (_inputTool)
            {
                case "ydotool":
                    Bash.ExecuteCommandAsync($"ydotool click --up 0xC{button - 1}");
                    break;
                case "xdotool":
                    Bash.ExecuteCommandAsync($"xdotool mouseup {button}");
                    break;
            }
        }

        /// <summary>
        /// Scrolls the mouse wheel
        /// </summary>
        public static async Task ScrollMouse(int deltaX, int deltaY)
        {
            if (!EnsureInitialized())
                return;

            if (DisplayServerDetector.IsX11)
            {
                lock (_displayLock)
                {
                    if (_display != IntPtr.Zero)
                    {
                        // Vertical scroll
                        if (deltaY != 0)
                        {
                            uint button = deltaY > 0 ? LibXtst.Button4 : LibXtst.Button5;
                            int clicks = Math.Abs(deltaY);
                            for (int i = 0; i < clicks; i++)
                            {
                                LibXtst.XTestFakeButtonEvent(_display, button, true, 0);
                                LibXtst.XTestFakeButtonEvent(_display, button, false, 0);
                            }
                        }

                        // Horizontal scroll
                        if (deltaX != 0)
                        {
                            uint button = deltaX > 0 ? LibXtst.Button7 : LibXtst.Button6;
                            int clicks = Math.Abs(deltaX);
                            for (int i = 0; i < clicks; i++)
                            {
                                LibXtst.XTestFakeButtonEvent(_display, button, true, 0);
                                LibXtst.XTestFakeButtonEvent(_display, button, false, 0);
                            }
                        }

                        LibX11.XFlush(_display);
                    }
                }
            }
            else if (DisplayServerDetector.IsWayland)
            {
                // ydotool scroll support
                if (_inputTool == "ydotool" || _inputTool == "xdotool")
                {
                    if (deltaY != 0)
                    {
                        string direction = deltaY > 0 ? "up" : "down";
                        int clicks = Math.Abs(deltaY);
                        if (_inputTool == "xdotool")
                        {
                            uint button = deltaY > 0 ? 4U : 5U;
                            Bash.ExecuteCommandAsync($"xdotool click --repeat {clicks} {button}");
                        }
                    }
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public static void Cleanup()
        {
            lock (_displayLock)
            {
                if (_display != IntPtr.Zero)
                {
                    LibX11.XCloseDisplay(_display);
                    _display = IntPtr.Zero;
                }
            }
        }
    }
}


using System;

namespace NetLock_RMM_User_Process.Linux.Helper
{
    /// <summary>
    /// Utility class for detecting display server type and environment
    /// </summary>
    internal static class DisplayServerDetector
    {
        private static DisplayServerType? _cachedType;

        public enum DisplayServerType
        {
            X11,
            Wayland,
            Unknown
        }

        /// <summary>
        /// Gets the current display server type (X11 or Wayland)
        /// </summary>
        public static DisplayServerType GetDisplayServerType()
        {
            if (_cachedType.HasValue)
            {
                return _cachedType.Value;
            }

            // Check XDG_SESSION_TYPE environment variable (most reliable)
            string sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            
            if (!string.IsNullOrEmpty(sessionType))
            {
                if (sessionType.Equals("wayland", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[DisplayServer] Detected: Wayland");
                    _cachedType = DisplayServerType.Wayland;
                    return _cachedType.Value;
                }
                if (sessionType.Equals("x11", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[DisplayServer] Detected: X11");
                    _cachedType = DisplayServerType.X11;
                    return _cachedType.Value;
                }
            }

            // Check WAYLAND_DISPLAY environment variable
            string waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            
            if (!string.IsNullOrEmpty(waylandDisplay))
            {
                Console.WriteLine("[DisplayServer] Detected: Wayland (via WAYLAND_DISPLAY)");
                _cachedType = DisplayServerType.Wayland;
                return _cachedType.Value;
            }

            // Check DISPLAY environment variable (X11)
            string display = Environment.GetEnvironmentVariable("DISPLAY");
            
            if (!string.IsNullOrEmpty(display))
            {
                Console.WriteLine("[DisplayServer] Detected: X11 (via DISPLAY)");
                _cachedType = DisplayServerType.X11;
                return _cachedType.Value;
            }

            // Check if we can actually connect to X11
            if (LibX11.IsX11Available())
            {
                Console.WriteLine("[DisplayServer] Detected: X11 (via LibX11)");
                _cachedType = DisplayServerType.X11;
                return _cachedType.Value;
            }

            Console.WriteLine("[DisplayServer] Detected: Unknown");
            _cachedType = DisplayServerType.Unknown;
            return _cachedType.Value;
        }

        /// <summary>
        /// Checks if running under X11
        /// </summary>
        public static bool IsX11 => GetDisplayServerType() == DisplayServerType.X11;

        /// <summary>
        /// Checks if running under Wayland
        /// </summary>
        public static bool IsWayland => GetDisplayServerType() == DisplayServerType.Wayland;

        /// <summary>
        /// Gets the DISPLAY environment variable (X11)
        /// </summary>
        public static string GetX11Display()
        {
            return Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
        }

        /// <summary>
        /// Gets the WAYLAND_DISPLAY environment variable
        /// </summary>
        public static string GetWaylandDisplay()
        {
            return Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0";
        }

        /// <summary>
        /// Checks which screenshot tools are available for Wayland fallback
        /// </summary>
        public static string GetAvailableScreenshotTool()
        {
            // Check in order of preference - grim is fastest (stdout support), gnome-screenshot is slowest
            string[] tools = { "grim", "spectacle", "scrot", "gnome-screenshot", "import" };
            
            foreach (var tool in tools)
            {
                string result = Bash.ExecuteCommand($"which {tool} 2>/dev/null");
                if (!string.IsNullOrEmpty(result))
                {
                    Console.WriteLine($"[DisplayServer] Found screenshot tool: {tool}");
                    return tool;
                }
            }

            return null;
        }

        /// <summary>
        /// Checks which input simulation tools are available for Wayland fallback
        /// </summary>
        public static string GetAvailableInputTool()
        {
            // ydotool works on both X11 and Wayland via uinput
            // xdotool only works on X11
            string[] tools = { "ydotool", "xdotool", "wtype" };

            foreach (var tool in tools)
            {
                string result = Bash.ExecuteCommand($"which {tool} 2>/dev/null");
                if (!string.IsNullOrEmpty(result))
                {
                    Console.WriteLine($"[DisplayServer] Found input tool: {tool}");
                    return tool;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets information about the current desktop environment
        /// </summary>
        public static string GetDesktopEnvironment()
        {
            // Try various environment variables
            string de = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
            if (!string.IsNullOrEmpty(de))
                return de;

            de = Environment.GetEnvironmentVariable("DESKTOP_SESSION");
            if (!string.IsNullOrEmpty(de))
                return de;

            de = Environment.GetEnvironmentVariable("GNOME_DESKTOP_SESSION_ID");
            if (!string.IsNullOrEmpty(de))
                return "GNOME";

            de = Environment.GetEnvironmentVariable("KDE_FULL_SESSION");
            if (!string.IsNullOrEmpty(de))
                return "KDE";

            return "Unknown";
        }
    }
}


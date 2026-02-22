using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Global.Helper;
using Global.Configuration;

namespace Linux.Helper
{
    /// <summary>
    /// Helper class for Linux session management.
    /// Allows starting processes in logged-in user sessions.
    /// </summary>
    public static class LinuxSession
    {
        /// <summary>
        /// Represents a logged-in user session on Linux
        /// </summary>
        public class UserSession
        {
            public uint SessionId { get; set; }
            public string SessionName { get; set; } = string.Empty;
            public uint UserId { get; set; }
            public string Username { get; set; } = string.Empty;
            public string State { get; set; } = string.Empty;
            public string Display { get; set; } = string.Empty;
            public string WaylandDisplay { get; set; } = string.Empty;
            public string RuntimeDir { get; set; } = string.Empty;
            public string SessionType { get; set; } = string.Empty; // "x11" or "wayland"
        }

        /// <summary>
        /// Gets all active user sessions using loginctl (systemd-logind)
        /// </summary>
        public static List<UserSession> GetActiveSessions()
        {
            var sessions = new List<UserSession>();

            try
            {
                Console.WriteLine("[LinuxSession] GetActiveSessions: Starting session enumeration...");
                
                // Use loginctl to list sessions
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/loginctl",
                        Arguments = "list-sessions --no-legend --no-pager",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                Console.WriteLine("[LinuxSession] GetActiveSessions: Executing loginctl list-sessions...");
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                
                Console.WriteLine($"[LinuxSession] GetActiveSessions: loginctl output:\n{output}");

                if (string.IsNullOrWhiteSpace(output))
                {
                    Console.WriteLine("[LinuxSession] GetActiveSessions: No sessions found (empty output)");
                    if (Agent.debug_mode)
                        Logging.Debug("LinuxSession.GetActiveSessions", "No sessions found", "loginctl returned empty output");
                    return sessions;
                }

                // Parse output: SESSION UID USER SEAT TTY
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Console.WriteLine($"[LinuxSession] GetActiveSessions: Found {lines.Length} lines to parse");
                
                foreach (var line in lines)
                {
                    Console.WriteLine($"[LinuxSession] GetActiveSessions: Parsing line: '{line}'");
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        var session = new UserSession
                        {
                            SessionName = parts[0],
                            Username = parts[2]
                        };

                        if (uint.TryParse(parts[0], out uint sessionId))
                            session.SessionId = sessionId;
                        if (uint.TryParse(parts[1], out uint uid))
                            session.UserId = uid;
                            
                        Console.WriteLine($"[LinuxSession] GetActiveSessions: Parsed - SessionName={session.SessionName}, Username={session.Username}, UID={session.UserId}");

                        // Get more details about this session
                        var details = GetSessionDetails(session.SessionName);
                        if (details != null)
                        {
                            session.State = details.State;
                            session.Display = details.Display;
                            session.RuntimeDir = details.RuntimeDir;
                            session.SessionType = details.SessionType;
                            session.WaylandDisplay = details.WaylandDisplay;
                            Console.WriteLine($"[LinuxSession] GetActiveSessions: Details - State={session.State}, Type={session.SessionType}, Display={session.Display}, WaylandDisplay={session.WaylandDisplay}, RuntimeDir={session.RuntimeDir}");
                        }

                        // Only include active sessions
                        if (session.State == "active" || session.State == "online")
                        {
                            sessions.Add(session);
                            Console.WriteLine($"[LinuxSession] GetActiveSessions: Added session for user {session.Username}");

                            if (Agent.debug_mode)
                                Logging.Debug("LinuxSession.GetActiveSessions", "Found session",
                                    $"SessionId: {session.SessionName}, User: {session.Username}, UID: {session.UserId}, State: {session.State}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("LinuxSession.GetActiveSessions", "Error enumerating sessions", ex.ToString());
            }

            return sessions;
        }

        /// <summary>
        /// Gets detailed information about a specific session
        /// </summary>
        private static UserSession? GetSessionDetails(string sessionName)
        {
            try
            {
                Console.WriteLine($"[LinuxSession] GetSessionDetails: Getting details for session '{sessionName}'...");
                
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/loginctl",
                        Arguments = $"show-session {sessionName} --no-pager",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                
                Console.WriteLine($"[LinuxSession] GetSessionDetails: Raw output:\n{output}");

                var session = new UserSession();

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        switch (parts[0])
                        {
                            case "State":
                                session.State = parts[1];
                                break;
                            case "Display":
                                session.Display = parts[1];
                                break;
                            case "Type":
                                session.SessionType = parts[1];
                                Console.WriteLine($"[LinuxSession] GetSessionDetails: SessionType = '{parts[1]}'");
                                break;
                            case "User":
                                if (uint.TryParse(parts[1], out uint uid))
                                    session.UserId = uid;
                                break;
                            case "Name":
                                session.Username = parts[1];
                                break;
                        }
                    }
                }

                // Set runtime directory based on UID
                if (session.UserId > 0)
                {
                    session.RuntimeDir = $"/run/user/{session.UserId}";
                    
                    // For Wayland sessions, try to find WAYLAND_DISPLAY
                    if (session.SessionType == "wayland")
                    {
                        session.WaylandDisplay = GetWaylandDisplayForUser(session.UserId);
                        Console.WriteLine($"[LinuxSession] GetSessionDetails: WaylandDisplay = '{session.WaylandDisplay}'");
                    }
                }
                
                Console.WriteLine($"[LinuxSession] GetSessionDetails: Parsed session - State={session.State}, Type={session.SessionType}, Display={session.Display}, WaylandDisplay={session.WaylandDisplay}");

                return session;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LinuxSession] GetSessionDetails: ERROR - {ex.Message}");
                Logging.Error("LinuxSession.GetSessionDetails", "Error getting session details", ex.ToString());
                return null;
            }
        }
        
        /// <summary>
        /// Gets the WAYLAND_DISPLAY for a user (typically "wayland-0")
        /// </summary>
        private static string GetWaylandDisplayForUser(uint uid)
        {
            try
            {
                string runtimeDir = $"/run/user/{uid}";
                
                // Check for wayland-0, wayland-1, etc.
                for (int i = 0; i <= 3; i++)
                {
                    string waylandSocket = Path.Combine(runtimeDir, $"wayland-{i}");
                    if (File.Exists(waylandSocket))
                    {
                        Console.WriteLine($"[LinuxSession] GetWaylandDisplayForUser: Found {waylandSocket}");
                        return $"wayland-{i}";
                    }
                }
                
                Console.WriteLine($"[LinuxSession] GetWaylandDisplayForUser: No wayland socket found in {runtimeDir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LinuxSession] GetWaylandDisplayForUser: ERROR - {ex.Message}");
            }
            
            return string.Empty;
        }

        /// <summary>
        /// Gets the UID for a username
        /// </summary>
        public static uint GetUidForUser(string username)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/id",
                        Arguments = $"-u {username}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);

                if (uint.TryParse(output, out uint uid))
                    return uid;
            }
            catch (Exception ex)
            {
                Logging.Error("LinuxSession.GetUidForUser", $"Error getting UID for {username}", ex.ToString());
            }

            return 0;
        }

        /// <summary>
        /// Creates a process in a specific user's session context.
        /// Uses sudo -u to run as the user and sets up necessary environment variables.
        /// Supports both X11 and Wayland sessions.
        /// </summary>
        public static bool CreateProcessInUserSession(
            string commandPath,
            string username,
            uint uid,
            string display,
            bool hiddenWindow,
            out int processId,
            string sessionType = "",
            string waylandDisplay = "")
        {
            processId = 0;

            try
            {
                Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: Starting...");
                Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: commandPath = '{commandPath}'");
                Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: username = '{username}', uid = {uid}, display = '{display}', hiddenWindow = {hiddenWindow}");
                Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: sessionType = '{sessionType}', waylandDisplay = '{waylandDisplay}'");
                
                if (Agent.debug_mode)
                    Logging.Debug("LinuxSession.CreateProcessInUserSession",
                        "Starting process in user session",
                        $"Command: {commandPath}, User: {username}, UID: {uid}, Display: {display}, SessionType: {sessionType}");

                // Build environment variables for the user session
                var envVars = new StringBuilder();
                
                // Set XDG_RUNTIME_DIR for D-Bus and other services
                string runtimeDir = $"/run/user/{uid}";
                if (Directory.Exists(runtimeDir))
                {
                    envVars.Append($"XDG_RUNTIME_DIR={runtimeDir} ");
                    Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: XDG_RUNTIME_DIR={runtimeDir}");
                }
                else
                {
                    Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: WARNING - {runtimeDir} does not exist");
                }

                // Handle display based on session type
                if (sessionType == "wayland")
                {
                    // For Wayland sessions
                    if (!string.IsNullOrEmpty(waylandDisplay))
                    {
                        envVars.Append($"WAYLAND_DISPLAY={waylandDisplay} ");
                        Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: WAYLAND_DISPLAY={waylandDisplay}");
                    }
                    else
                    {
                        // Try to auto-detect wayland display
                        string autoWayland = GetWaylandDisplayForUser(uid);
                        if (!string.IsNullOrEmpty(autoWayland))
                        {
                            envVars.Append($"WAYLAND_DISPLAY={autoWayland} ");
                            Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: WAYLAND_DISPLAY={autoWayland} (auto-detected)");
                        }
                    }
                    
                    // Some apps also need XDG_SESSION_TYPE
                    envVars.Append("XDG_SESSION_TYPE=wayland ");
                }
                
                // Set DISPLAY for X11 or XWayland applications
                if (!string.IsNullOrEmpty(display))
                {
                    envVars.Append($"DISPLAY={display} ");
                    Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: DISPLAY={display}");
                }
                else
                {
                    // Try to find the display from environment
                    string defaultDisplay = GetDisplayForUser(username, uid);
                    if (!string.IsNullOrEmpty(defaultDisplay))
                    {
                        envVars.Append($"DISPLAY={defaultDisplay} ");
                        Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: DISPLAY={defaultDisplay} (auto-detected)");
                    }
                    else
                    {
                        Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: WARNING - No DISPLAY found");
                    }
                }

                // Set DBUS_SESSION_BUS_ADDRESS
                string dbusAddress = GetDbusAddressForUser(uid);
                if (!string.IsNullOrEmpty(dbusAddress))
                {
                    envVars.Append($"DBUS_SESSION_BUS_ADDRESS={dbusAddress} ");
                    Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: DBUS_SESSION_BUS_ADDRESS={dbusAddress}");
                }
                else
                {
                    Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: WARNING - No DBUS address found");
                }

                // Build the command to run as the target user
                // Using sudo -u to switch to the user
                string bashCommand;
                if (hiddenWindow)
                {
                    // Run in background without window
                    bashCommand = $"sudo -u {username} {envVars}nohup {commandPath} > /dev/null 2>&1 &";
                }
                else
                {
                    // Run with window/terminal
                    bashCommand = $"sudo -u {username} {envVars}{commandPath} &";
                }
                
                Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: bashCommand = '{bashCommand}'");

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"{bashCommand}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                Console.WriteLine("[LinuxSession] CreateProcessInUserSession: Starting bash process...");
                process.Start();
                process.WaitForExit(5000);

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                
                Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: ExitCode = {process.ExitCode}");
                Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: stdout = '{stdout}'");
                Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: stderr = '{stderr}'");

                if (process.ExitCode == 0)
                {
                    // Wait a moment for the process to start
                    Console.WriteLine("[LinuxSession] CreateProcessInUserSession: Waiting 500ms for process to start...");
                    System.Threading.Thread.Sleep(500);
                    
                    // Try to find the PID of the started process
                    string processName = Path.GetFileNameWithoutExtension(commandPath);
                    Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: Searching for process '{processName}' owned by '{username}'...");
                    processId = FindProcessByNameAndUser(processName, username);
                    
                    Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: Found PID = {processId}");

                    if (Agent.debug_mode)
                        Logging.Debug("LinuxSession.CreateProcessInUserSession",
                            "Process started successfully",
                            $"PID: {processId}, User: {username}");

                    bool success = processId > 0;
                    Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: Returning success = {success}");
                    return success;
                }
                else
                {
                    Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: FAILED - ExitCode != 0");
                    Logging.Error("LinuxSession.CreateProcessInUserSession",
                        "Failed to start process",
                        $"ExitCode: {process.ExitCode}, StdErr: {stderr}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: EXCEPTION - {ex.Message}");
                Console.WriteLine($"[LinuxSession] CreateProcessInUserSession: StackTrace - {ex.StackTrace}");
                Logging.Error("LinuxSession.CreateProcessInUserSession", "Exception starting process", ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Gets the D-Bus session bus address for a user
        /// </summary>
        private static string GetDbusAddressForUser(uint uid)
        {
            try
            {
                string runtimeDir = $"/run/user/{uid}";
                string busPath = Path.Combine(runtimeDir, "bus");
                
                if (File.Exists(busPath))
                {
                    return $"unix:path={busPath}";
                }
            }
            catch { }

            return string.Empty;
        }

        /// <summary>
        /// Gets the DISPLAY environment variable for a user session
        /// </summary>
        private static string GetDisplayForUser(string username, uint uid)
        {
            try
            {
                // Try to read from /proc for any process owned by this user
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"cat /proc/*/environ 2>/dev/null | tr '\\0' '\\n' | grep '^DISPLAY=' | head -1\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(3000);

                if (output.StartsWith("DISPLAY="))
                {
                    return output.Substring(8);
                }
            }
            catch { }

            // Default display
            return ":0";
        }

        /// <summary>
        /// Finds a process by name and owner
        /// </summary>
        public static int FindProcessByNameAndUser(string processName, string username)
        {
            try
            {
                Console.WriteLine($"[LinuxSession] FindProcessByNameAndUser: Searching for process='{processName}', user='{username}'");
                
                // Use pgrep with -f flag for full command line matching
                // Format: pgrep -u username -f processName -n (newest)
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/pgrep",
                        Arguments = $"-f -u {username} {processName}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                Console.WriteLine($"[LinuxSession] FindProcessByNameAndUser: Executing: pgrep -f -u {username} {processName}");
                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                string stderr = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit(3000);
                
                Console.WriteLine($"[LinuxSession] FindProcessByNameAndUser: output='{output}', stderr='{stderr}', exitCode={process.ExitCode}");

                // pgrep may return multiple PIDs, take the first one
                if (!string.IsNullOrEmpty(output))
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out int pid))
                    {
                        Console.WriteLine($"[LinuxSession] FindProcessByNameAndUser: Found PID = {pid}");
                        return pid;
                    }
                }
                
                Console.WriteLine($"[LinuxSession] FindProcessByNameAndUser: No PID found");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LinuxSession] FindProcessByNameAndUser: ERROR - {ex.Message}");
            }

            return 0;
        }

        /// <summary>
        /// Checks if a process with the given name is running for a specific user
        /// </summary>
        public static bool IsProcessRunningForUser(string processName, string username)
        {
            Console.WriteLine($"[LinuxSession] IsProcessRunningForUser: Checking if '{processName}' is running for user '{username}'");
            int pid = FindProcessByNameAndUser(processName, username);
            bool isRunning = pid > 0;
            Console.WriteLine($"[LinuxSession] IsProcessRunningForUser: Result = {isRunning} (PID = {pid})");
            return isRunning;
        }

        /// <summary>
        /// Gets the session ID for a process
        /// </summary>
        public static string GetProcessSessionId(int processId)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/loginctl",
                        Arguments = $"session-status --no-pager",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);

                // Parse output to find the session containing this PID
                // This is a simplified approach
                if (output.Contains(processId.ToString()))
                {
                    var match = Regex.Match(output, @"^(\d+)", RegexOptions.Multiline);
                    if (match.Success)
                        return match.Groups[1].Value;
                }
            }
            catch { }

            return string.Empty;
        }
    }
}


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Global.Helper;
using Global.Configuration;

namespace MacOS.Helper
{
    /// <summary>
    /// Helper class for macOS session management.
    /// Allows starting processes in logged-in user sessions using launchctl.
    /// </summary>
    public static class MacOSSession
    {
        /// <summary>
        /// Represents a logged-in user session on macOS
        /// </summary>
        public class UserSession
        {
            public uint UserId { get; set; }
            public string Username { get; set; } = string.Empty;
            public bool IsConsoleUser { get; set; }
            public bool IsGuiSession { get; set; }
        }

        /// <summary>
        /// Gets all active user sessions on macOS.
        /// Uses 'who' command and checks for GUI sessions.
        /// </summary>
        public static List<UserSession> GetActiveSessions()
        {
            var sessions = new List<UserSession>();
            var processedUsers = new HashSet<string>();

            try
            {
                Console.WriteLine("[MacOSSession] GetActiveSessions: Starting session enumeration...");
                
                // Get console user using scutil
                string consoleUser = GetConsoleUser();
                Console.WriteLine($"[MacOSSession] GetActiveSessions: Console user = '{consoleUser}'");
                
                // Use 'who' command to get logged-in users
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/who",
                        Arguments = "",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                Console.WriteLine("[MacOSSession] GetActiveSessions: Executing 'who' command...");
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                Console.WriteLine($"[MacOSSession] GetActiveSessions: 'who' output:\n{output}");

                // Parse output: username console|ttys### date time
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Console.WriteLine($"[MacOSSession] GetActiveSessions: Found {lines.Length} lines to parse");
                
                foreach (var line in lines)
                {
                    Console.WriteLine($"[MacOSSession] GetActiveSessions: Parsing line: '{line}'");
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string username = parts[0];
                        string terminal = parts[1];
                        Console.WriteLine($"[MacOSSession] GetActiveSessions: Found user='{username}', terminal='{terminal}'");

                        // Skip if already processed
                        if (processedUsers.Contains(username))
                        {
                            Console.WriteLine($"[MacOSSession] GetActiveSessions: User '{username}' already processed, skipping");
                            continue;
                        }

                        processedUsers.Add(username);

                        uint uid = GetUidForUser(username);
                        Console.WriteLine($"[MacOSSession] GetActiveSessions: UID for '{username}' = {uid}");
                        
                        if (uid == 0 && username != "root")
                        {
                            Console.WriteLine($"[MacOSSession] GetActiveSessions: Skipping user '{username}' (UID=0 and not root)");
                            continue; // Skip if we can't get UID
                        }

                        var session = new UserSession
                        {
                            Username = username,
                            UserId = uid,
                            IsConsoleUser = terminal == "console" || username == consoleUser,
                            IsGuiSession = terminal == "console"
                        };

                        sessions.Add(session);
                        Console.WriteLine($"[MacOSSession] GetActiveSessions: Added session - User: {username}, UID: {uid}, IsConsole: {session.IsConsoleUser}, IsGui: {session.IsGuiSession}");

                        if (Agent.debug_mode)
                            Logging.Debug("MacOSSession.GetActiveSessions", "Found session",
                                $"User: {username}, UID: {uid}, Console: {session.IsConsoleUser}, Terminal: {terminal}");
                    }
                }

                // Also check for GUI sessions via launchctl
                AddGuiSessions(sessions, processedUsers);
                
                Console.WriteLine($"[MacOSSession] GetActiveSessions: Completed. Total sessions found: {sessions.Count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOSSession] GetActiveSessions: ERROR - {ex.Message}");
                Logging.Error("MacOSSession.GetActiveSessions", "Error enumerating sessions", ex.ToString());
            }

            return sessions;
        }

        /// <summary>
        /// Gets the currently logged-in console user.
        /// Tries multiple methods: scutil, stat /dev/console, and fallback to 'who' parsing.
        /// </summary>
        private static string GetConsoleUser()
        {
            try
            {
                Console.WriteLine("[MacOSSession] GetConsoleUser: Starting...");
                
                // Method 1: Try scutil (may not exist on all systems)
                string consoleUser = TryGetConsoleUserViaScutil();
                if (!string.IsNullOrEmpty(consoleUser))
                {
                    Console.WriteLine($"[MacOSSession] GetConsoleUser: Found via scutil: '{consoleUser}'");
                    return consoleUser;
                }
                
                // Method 2: Try stat /dev/console (works on most macOS)
                consoleUser = TryGetConsoleUserViaStat();
                if (!string.IsNullOrEmpty(consoleUser))
                {
                    Console.WriteLine($"[MacOSSession] GetConsoleUser: Found via stat: '{consoleUser}'");
                    return consoleUser;
                }
                
                // Method 3: Fallback - parse 'who' output for console user
                consoleUser = TryGetConsoleUserViaWho();
                if (!string.IsNullOrEmpty(consoleUser))
                {
                    Console.WriteLine($"[MacOSSession] GetConsoleUser: Found via who: '{consoleUser}'");
                    return consoleUser;
                }
                
                Console.WriteLine("[MacOSSession] GetConsoleUser: No console user found with any method");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOSSession] GetConsoleUser: ERROR - {ex.Message}");
            }

            return string.Empty;
        }
        
        /// <summary>
        /// Try to get console user via scutil (may not exist on all systems)
        /// </summary>
        private static string TryGetConsoleUserViaScutil()
        {
            try
            {
                // Check if scutil exists
                if (!File.Exists("/usr/bin/scutil"))
                {
                    Console.WriteLine("[MacOSSession] TryGetConsoleUserViaScutil: /usr/bin/scutil does not exist");
                    return string.Empty;
                }
                
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/scutil",
                        Arguments = "",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.StandardInput.WriteLine("show State:/Users/ConsoleUser");
                process.StandardInput.Close();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                
                Console.WriteLine($"[MacOSSession] TryGetConsoleUserViaScutil: output:\n{output}");

                // Parse output to find Name key
                var match = Regex.Match(output, @"Name\s*:\s*(\S+)");
                if (match.Success)
                {
                    string username = match.Groups[1].Value;
                    if (username != "loginwindow")
                    {
                        return username;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOSSession] TryGetConsoleUserViaScutil: ERROR - {ex.Message}");
            }

            return string.Empty;
        }
        
        /// <summary>
        /// Try to get console user via stat /dev/console (more reliable)
        /// </summary>
        private static string TryGetConsoleUserViaStat()
        {
            try
            {
                Console.WriteLine("[MacOSSession] TryGetConsoleUserViaStat: Trying stat -f %Su /dev/console...");
                
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/stat",
                        Arguments = "-f %Su /dev/console",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                string stderr = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit(3000);
                
                Console.WriteLine($"[MacOSSession] TryGetConsoleUserViaStat: output='{output}', stderr='{stderr}', exitCode={process.ExitCode}");

                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output) && output != "root")
                {
                    return output;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOSSession] TryGetConsoleUserViaStat: ERROR - {ex.Message}");
            }

            return string.Empty;
        }
        
        /// <summary>
        /// Fallback: Get console user from 'who' output
        /// </summary>
        private static string TryGetConsoleUserViaWho()
        {
            try
            {
                Console.WriteLine("[MacOSSession] TryGetConsoleUserViaWho: Trying who | grep console...");
                
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/who",
                        Arguments = "",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                
                Console.WriteLine($"[MacOSSession] TryGetConsoleUserViaWho: who output:\n{output}");

                // Find the line with "console"
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.Contains("console"))
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 1)
                        {
                            Console.WriteLine($"[MacOSSession] TryGetConsoleUserViaWho: Found console user = '{parts[0]}'");
                            return parts[0];
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOSSession] TryGetConsoleUserViaWho: ERROR - {ex.Message}");
            }

            return string.Empty;
        }

        /// <summary>
        /// Adds GUI sessions by checking launchctl for user domains
        /// </summary>
        private static void AddGuiSessions(List<UserSession> sessions, HashSet<string> processedUsers)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/launchctl",
                        Arguments = "print system",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);

                // Look for GUI users in output
                // Format varies by macOS version
            }
            catch { }
        }

        /// <summary>
        /// Gets the UID for a username
        /// </summary>
        public static uint GetUidForUser(string username)
        {
            try
            {
                Console.WriteLine($"[MacOSSession] GetUidForUser: Getting UID for '{username}'...");
                
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
                string stderr = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit(5000);
                
                Console.WriteLine($"[MacOSSession] GetUidForUser: id -u output = '{output}', stderr = '{stderr}', exitCode = {process.ExitCode}");

                if (uint.TryParse(output, out uint uid))
                {
                    Console.WriteLine($"[MacOSSession] GetUidForUser: Parsed UID = {uid}");
                    return uid;
                }
                
                Console.WriteLine($"[MacOSSession] GetUidForUser: Failed to parse UID from '{output}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOSSession] GetUidForUser: ERROR - {ex.Message}");
                Logging.Error("MacOSSession.GetUidForUser", $"Error getting UID for {username}", ex.ToString());
            }

            return 0;
        }

        /// <summary>
        /// Creates a process in a specific user's session context.
        /// Uses launchctl asuser to run as the user in their GUI session.
        /// </summary>
        public static bool CreateProcessInUserSession(
            string commandPath,
            string username,
            uint uid,
            bool hiddenWindow,
            out int processId)
        {
            processId = 0;

            try
            {
                Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: Starting...");
                Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: commandPath = '{commandPath}'");
                Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: username = '{username}', uid = {uid}, hiddenWindow = {hiddenWindow}");
                
                if (Agent.debug_mode)
                    Logging.Debug("MacOSSession.CreateProcessInUserSession",
                        "Starting process in user session",
                        $"Command: {commandPath}, User: {username}, UID: {uid}");

                // Use launchctl asuser to start process in user's context
                // This properly attaches the process to the user's GUI session
                string bashCommand;
                if (hiddenWindow)
                {
                    // Run in background without window
                    bashCommand = $"/bin/launchctl asuser {uid} {commandPath} &";
                }
                else
                {
                    // Run with window - use open command for GUI apps
                    if (commandPath.EndsWith(".app"))
                    {
                        bashCommand = $"/bin/launchctl asuser {uid} /usr/bin/open -a \"{commandPath}\"";
                    }
                    else
                    {
                        bashCommand = $"/bin/launchctl asuser {uid} {commandPath} &";
                    }
                }
                
                Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: bashCommand = '{bashCommand}'");

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

                Console.WriteLine("[MacOSSession] CreateProcessInUserSession: Starting bash process...");
                process.Start();
                process.WaitForExit(5000);

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                
                Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: ExitCode = {process.ExitCode}");
                Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: stdout = '{stdout}'");
                Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: stderr = '{stderr}'");

                if (process.ExitCode == 0)
                {
                    // Wait a moment for the process to start
                    Console.WriteLine("[MacOSSession] CreateProcessInUserSession: Waiting 500ms for process to start...");
                    Thread.Sleep(500);

                    // Try to find the PID of the started process
                    string processName = Path.GetFileNameWithoutExtension(commandPath);
                    Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: Searching for process '{processName}' owned by '{username}'...");
                    processId = FindProcessByNameAndUser(processName, username);
                    
                    Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: Found PID = {processId}");

                    if (Agent.debug_mode)
                        Logging.Debug("MacOSSession.CreateProcessInUserSession",
                            "Process started successfully",
                            $"PID: {processId}, User: {username}");

                    bool success = processId > 0;
                    Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: Returning success = {success}");
                    return success;
                }
                else
                {
                    Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: FAILED - ExitCode != 0");
                    Logging.Error("MacOSSession.CreateProcessInUserSession",
                        "Failed to start process",
                        $"ExitCode: {process.ExitCode}, StdErr: {stderr}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: EXCEPTION - {ex.Message}");
                Console.WriteLine($"[MacOSSession] CreateProcessInUserSession: StackTrace - {ex.StackTrace}");
                Logging.Error("MacOSSession.CreateProcessInUserSession", "Exception starting process", ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Finds a process by name and owner using pgrep
        /// </summary>
        public static int FindProcessByNameAndUser(string processName, string username)
        {
            try
            {
                Console.WriteLine($"[MacOSSession] FindProcessByNameAndUser: Searching for process='{processName}', user='{username}'");
                
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/bin/pgrep",
                        Arguments = $"-u {username} -n {processName}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                Console.WriteLine($"[MacOSSession] FindProcessByNameAndUser: Executing: pgrep -u {username} -n {processName}");
                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                string stderr = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit(3000);
                
                Console.WriteLine($"[MacOSSession] FindProcessByNameAndUser: output='{output}', stderr='{stderr}', exitCode={process.ExitCode}");

                if (int.TryParse(output, out int pid))
                {
                    Console.WriteLine($"[MacOSSession] FindProcessByNameAndUser: Found PID = {pid}");
                    return pid;
                }
                
                Console.WriteLine($"[MacOSSession] FindProcessByNameAndUser: No PID found (could not parse '{output}')");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOSSession] FindProcessByNameAndUser: ERROR - {ex.Message}");
            }

            return 0;
        }

        /// <summary>
        /// Checks if a process with the given name is running for a specific user
        /// </summary>
        public static bool IsProcessRunningForUser(string processName, string username)
        {
            Console.WriteLine($"[MacOSSession] IsProcessRunningForUser: Checking if '{processName}' is running for user '{username}'");
            int pid = FindProcessByNameAndUser(processName, username);
            bool isRunning = pid > 0;
            Console.WriteLine($"[MacOSSession] IsProcessRunningForUser: Result = {isRunning} (PID = {pid})");
            return isRunning;
        }

        /// <summary>
        /// Gets the session UID that owns a process
        /// </summary>
        public static uint GetProcessOwnerUid(int processId)
        {
            try
            {
                Console.WriteLine($"[MacOSSession] GetProcessOwnerUid: Getting owner UID for PID {processId}");
                
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/ps",
                        Arguments = $"-o uid= -p {processId}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(3000);
                
                Console.WriteLine($"[MacOSSession] GetProcessOwnerUid: ps output = '{output}'");

                if (uint.TryParse(output, out uint uid))
                {
                    Console.WriteLine($"[MacOSSession] GetProcessOwnerUid: Parsed UID = {uid}");
                    return uid;
                }
                
                Console.WriteLine($"[MacOSSession] GetProcessOwnerUid: Failed to parse UID from '{output}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOSSession] GetProcessOwnerUid: ERROR - {ex.Message}");
            }

            return 0;
        }
    }
}









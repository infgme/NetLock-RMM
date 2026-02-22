using System;
using System.Diagnostics;
using System.Text;

namespace NetLock_RMM_User_Process.Linux.Helper
{
    /// <summary>
    /// Helper class for executing bash commands on Linux
    /// </summary>
    internal static class Bash
    {
        /// <summary>
        /// Executes a bash script and returns the output
        /// </summary>
        public static string ExecuteScript(string script, bool decode = false, int timeoutMs = 120000)
        {
            try
            {
                if (string.IsNullOrEmpty(script))
                {
                    Console.WriteLine("[Bash] Script is empty");
                    return string.Empty;
                }

                // Decode from Base64 if needed
                if (decode)
                {
                    byte[] scriptData = Convert.FromBase64String(script);
                    script = Encoding.UTF8.GetString(scriptData);
                    // Convert Windows line endings to Unix
                    script = script.Replace("\r\n", "\n");
                }

                using (var process = new Process())
                {
                    process.StartInfo.FileName = "/bin/bash";
                    process.StartInfo.Arguments = "-s";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardInput = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;

                    process.Start();

                    using (var writer = process.StandardInput)
                    {
                        writer.Write(script);
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();

                    process.WaitForExit(timeoutMs);

                    if (!string.IsNullOrEmpty(error))
                    {
                        Console.WriteLine($"[Bash] Error: {error}");
                        return $"Output:\n{output}\n\nError:\n{error}";
                    }

                    return output;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bash] Exception: {ex.Message}");
                return ex.Message;
            }
        }

        /// <summary>
        /// Executes a single command and returns the output
        /// </summary>
        public static string ExecuteCommand(string command, int timeoutMs = 30000)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = "/bin/bash";
                    process.StartInfo.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;

                    process.Start();

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(timeoutMs);

                    return output.Trim();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bash] Command error: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Executes a command asynchronously without waiting for output
        /// </summary>
        public static void ExecuteCommandAsync(string command)
        {
            try
            {
                var process = new Process();
                process.StartInfo.FileName = "/bin/bash";
                process.StartInfo.Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bash] Async command error: {ex.Message}");
            }
        }
    }
}


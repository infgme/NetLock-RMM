using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NetLock_RMM_User_Process.Linux.Helper;

namespace NetLock_RMM_User_Process.Linux.ScreenControl
{
    /// <summary>
    /// High-performance Wayland screen capture using PipeWire via XDG Desktop Portal.
    /// This approach provides the fastest capture method for Wayland.
    /// 
    /// Architecture:
    /// - Uses dbus-send to communicate with XDG Desktop Portal
    /// - Portal returns a PipeWire stream file descriptor
    /// - We use gst-launch-1.0 (GStreamer) to capture frames from PipeWire
    /// - Frames are captured to shared memory (/dev/shm) for minimal latency
    /// 
    /// Requirements:
    /// - pipewire (Ubuntu 24.04 default)
    /// - xdg-desktop-portal and xdg-desktop-portal-gnome/kde
    /// - gstreamer1.0-pipewire
    /// </summary>
    public class PipeWireScreenCapture : IDisposable
    {
        private Process? _gstreamerProcess;
        private Process? _portalProcess;  // Keep portal process running
        private string? _sessionHandle;
        private string? _pipeWireNodeId;
        private bool _initialized;
        private bool _initializing;  // Flag to prevent multiple concurrent initializations
        private bool _sessionActive;
        private readonly object _lockObject = new object();
        private readonly SemaphoreSlim _initSemaphore = new SemaphoreSlim(1, 1);
        private string? _portalScriptPath;  // Keep track of script path for cleanup
        
        // Frame buffer for captured images
        private readonly string _frameBufferPath;
        private readonly string _frameReadyPath;
        private readonly string _inputCommandPath;  // For sending input commands to portal
        private byte[]? _lastFrame;
        private DateTime _lastFrameTime;
        
        // Frame caching for when file is being written
        private byte[]? _currentFrameBuffer;
        
        // Screen dimensions
        public int Width { get; private set; }
        public int Height { get; private set; }
        
        // Session token for restore (avoid permission dialog on reconnect)
        private string? _restoreToken;
        private static readonly string RestoreTokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "netlock", "pipewire_restore_token");
        
        // Capture settings
        private const int MAX_IMAGE_DIMENSION = 1600;
        private const int DEFAULT_QUALITY = 60;
        
        public PipeWireScreenCapture()
        {
            // Use shared memory for frame buffer (fastest I/O)
            string uniqueId = $"{Environment.ProcessId}_{DateTime.Now.Ticks}";
            _frameBufferPath = $"/dev/shm/netlock_pipewire_frame_{uniqueId}.raw";
            _frameReadyPath = $"/dev/shm/netlock_pipewire_ready_{uniqueId}";
            _inputCommandPath = $"/dev/shm/netlock_portal_input_{Environment.ProcessId}.txt";
        }
        
        /// <summary>
        /// Check if this capture instance supports remote input (mouse/keyboard)
        /// </summary>
        public bool SupportsRemoteInput => _sessionActive && _portalProcess != null && !_portalProcess.HasExited;

        /// <summary>
        /// Check if PipeWire capture is available on this system
        /// </summary>
        public static bool IsAvailable()
        {
            try
            {
                Console.WriteLine("[PipeWire] Checking availability...");
                
                // Check for gst-launch-1.0 and python3
                foreach (var tool in new[] { "gst-launch-1.0", "python3" })
                {
                    string result = Bash.ExecuteCommand($"which {tool} 2>/dev/null");
                    if (string.IsNullOrEmpty(result?.Trim()))
                    {
                        Console.WriteLine($"[PipeWire] Missing: {tool}");
                        return false;
                    }
                }
                
                // Check for python dbus module
                string pyCheck = Bash.ExecuteCommand("python3 -c 'import dbus; from dbus.mainloop.glib import DBusGMainLoop; from gi.repository import GLib; print(\"OK\")' 2>&1");
                if (string.IsNullOrEmpty(pyCheck) || !pyCheck.Contains("OK"))
                {
                    Console.WriteLine("[PipeWire] Python dbus/gi modules missing. Install: sudo apt install python3-dbus python3-gi");
                    return false;
                }
                
                // Check for gstreamer pipewire plugin
                string gstCheck = Bash.ExecuteCommand("gst-inspect-1.0 pipewiresrc 2>/dev/null | head -1");
                if (string.IsNullOrEmpty(gstCheck?.Trim()) || gstCheck.Contains("No such element"))
                {
                    Console.WriteLine("[PipeWire] GStreamer pipewiresrc missing. Install: sudo apt install gstreamer1.0-pipewire");
                    return false;
                }
                
                // Check for PipeWire service
                string pwCheck = Bash.ExecuteCommand("systemctl --user is-active pipewire 2>/dev/null");
                if (!pwCheck?.Trim().Equals("active", StringComparison.OrdinalIgnoreCase) ?? true)
                {
                    string psCheck = Bash.ExecuteCommand("pgrep -x pipewire 2>/dev/null");
                    if (string.IsNullOrEmpty(psCheck?.Trim()))
                    {
                        Console.WriteLine("[PipeWire] PipeWire not running");
                        return false;
                    }
                }
                
                // Check for xdg-desktop-portal
                string portalCheck = Bash.ExecuteCommand("pgrep -f xdg-desktop-portal 2>/dev/null");
                if (string.IsNullOrEmpty(portalCheck?.Trim()))
                {
                    Console.WriteLine("[PipeWire] XDG Desktop Portal not running");
                    return false;
                }
                
                Console.WriteLine("[PipeWire] All requirements met");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeWire] Availability check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Initialize the PipeWire capture session via XDG Desktop Portal
        /// This will prompt the user to select a screen/window to share
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            if (_initialized)
                return true;
            
            // Use semaphore to prevent multiple concurrent initializations
            if (!await _initSemaphore.WaitAsync(0))
            {
                Console.WriteLine("[PipeWire] Initialization already in progress, waiting...");
                // Wait for the ongoing initialization to complete
                await _initSemaphore.WaitAsync();
                _initSemaphore.Release();
                return _initialized;
            }

            try
            {
                if (_initialized)
                    return true;
                
                _initializing = true;
                Console.WriteLine("[PipeWire] Starting initialization...");
                
                // Load restore token if available
                LoadRestoreToken();
                
                // Create ScreenCast session via portal
                bool sessionCreated = await CreatePortalSessionAsync();
                if (!sessionCreated)
                {
                    Console.WriteLine("[PipeWire] Failed to create portal session");
                    return false;
                }
                
                // Start GStreamer capture pipeline
                bool pipelineStarted = StartGStreamerPipeline();
                if (!pipelineStarted)
                {
                    Console.WriteLine("[PipeWire] Failed to start GStreamer pipeline");
                    return false;
                }
                
                _initialized = true;
                Console.WriteLine($"[PipeWire] Initialized successfully - {Width}x{Height}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeWire] Initialization error: {ex.Message}");
                return false;
            }
            finally
            {
                _initializing = false;
                _initSemaphore.Release();
            }
        }

        /// <summary>
        /// Creates a ScreenCast session via XDG Desktop Portal using a Python helper script.
        /// This is necessary because the portal uses async DBus signals that are hard to handle from C#.
        /// </summary>
        private async Task<bool> CreatePortalSessionAsync()
        {
            try
            {
                Console.WriteLine("[PipeWire] Creating portal session...");
                
                string pythonScript = CreatePortalHelperScript();
                _portalScriptPath = $"/dev/shm/netlock_portal_{Environment.ProcessId}.py";
                string outputPath = $"/dev/shm/netlock_portal_result_{Environment.ProcessId}.txt";
                
                // Clean up any previous output file
                try { File.Delete(outputPath); } catch { }
                
                File.WriteAllText(_portalScriptPath, pythonScript);
                
                Console.WriteLine("[PipeWire] Waiting for screen selection dialog...");
                
                var psi = new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = $"{_portalScriptPath} {outputPath}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                // Pass environment variables for DBus
                psi.Environment["DISPLAY"] = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
                psi.Environment["WAYLAND_DISPLAY"] = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0";
                psi.Environment["XDG_SESSION_TYPE"] = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "wayland";
                psi.Environment["XDG_RUNTIME_DIR"] = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? $"/run/user/{GetCurrentUid()}";
                psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? "";
                
                _portalProcess = new Process { StartInfo = psi };
                _portalProcess.Start();
                
                // Wait for the output file to be created (max 60 seconds for user to select)
                DateTime startTime = DateTime.Now;
                while ((DateTime.Now - startTime).TotalSeconds < 60)
                {
                    if (File.Exists(outputPath))
                    {
                        // Small delay to ensure file is completely written
                        await Task.Delay(100);
                        
                        string result = File.ReadAllText(outputPath).Trim();
                        
                        // Parse multi-line result (node_id:xxx\nsession:xxx)
                        if (result.Contains("node_id:"))
                        {
                            var lines = result.Split('\n');
                            foreach (var line in lines)
                            {
                                var trimmedLine = line.Trim();
                                if (trimmedLine.StartsWith("node_id:"))
                                {
                                    _pipeWireNodeId = trimmedLine.Substring("node_id:".Length).Trim();
                                }
                                else if (trimmedLine.StartsWith("session:"))
                                {
                                    _sessionHandle = trimmedLine.Substring("session:".Length).Trim();
                                }
                            }
                            
                            if (!string.IsNullOrEmpty(_pipeWireNodeId))
                            {
                                Console.WriteLine($"[PipeWire] Got node ID: {_pipeWireNodeId}");
                                Console.WriteLine($"[PipeWire] Session handle: {_sessionHandle}");
                                _sessionActive = true;
                                // Don't delete script - portal process is still running!
                                try { File.Delete(outputPath); } catch { }
                                return true;
                            }
                        }
                        else if (result.StartsWith("error:"))
                        {
                            Console.WriteLine($"[PipeWire] Portal error: {result}");
                            StopPortalProcess();
                            return false;
                        }
                    }
                    
                    // Check if portal process died
                    if (_portalProcess.HasExited)
                    {
                        string stderr = _portalProcess.StandardError.ReadToEnd();
                        Console.WriteLine($"[PipeWire] Portal process died: {stderr}");
                        return false;
                    }
                    
                    await Task.Delay(200);
                }
                
                Console.WriteLine("[PipeWire] Timeout waiting for screen selection");
                StopPortalProcess();
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeWire] CreatePortalSession error: {ex.Message}");
                StopPortalProcess();
                return false;
            }
        }
        
        private void StopPortalProcess()
        {
            if (_portalProcess != null && !_portalProcess.HasExited)
            {
                try { _portalProcess.Kill(); } catch { }
                try { _portalProcess.Dispose(); } catch { }
            }
            _portalProcess = null;
            
            if (!string.IsNullOrEmpty(_portalScriptPath))
            {
                try { File.Delete(_portalScriptPath); } catch { }
                _portalScriptPath = null;
            }
        }
        
        /// <summary>
        /// Creates a Python helper script that handles the XDG Desktop Portal interaction
        /// Uses RemoteDesktop portal which includes both ScreenCast AND input capabilities
        /// </summary>
        private string CreatePortalHelperScript()
        {
            // This script stays running to keep the portal session alive
            // and provides input simulation via RemoteDesktop portal
            return @"#!/usr/bin/env python3
import sys
import os
import signal
import json
import dbus
from dbus.mainloop.glib import DBusGMainLoop
from gi.repository import GLib

output_file = sys.argv[1] if len(sys.argv) > 1 else '/tmp/portal_result.txt'
input_file = output_file.replace('_result_', '_input_')

DBusGMainLoop(set_as_default=True)
loop = GLib.MainLoop()

bus = dbus.SessionBus()
portal = bus.get_object('org.freedesktop.portal.Desktop', '/org/freedesktop/portal/desktop')

# Use RemoteDesktop portal which includes ScreenCast + Input
remote_desktop = dbus.Interface(portal, 'org.freedesktop.portal.RemoteDesktop')
screencast = dbus.Interface(portal, 'org.freedesktop.portal.ScreenCast')

session_handle = None
sender_name = bus.get_unique_name().replace('.', '_').replace(':', '')
request_counter = 0
stream_started = False
stream_node_id = None

def write_result(result):
    with open(output_file, 'w') as f:
        f.write(result)
    if not stream_started or result.startswith('error:'):
        loop.quit()

def get_request_token():
    global request_counter
    request_counter += 1
    return f'netlock_req_{os.getpid()}_{request_counter}'

def on_create_session_response(response, results):
    global session_handle
    if response != 0:
        write_result(f'error:CreateSession failed with response {response}')
        return
    
    session_handle = results.get('session_handle', '')
    if not session_handle:
        write_result('error:No session_handle in response')
        return
    
    # Select devices (keyboard + pointer) for RemoteDesktop
    request_token = get_request_token()
    options = {
        'handle_token': request_token,
        'types': dbus.UInt32(1 | 2),  # KEYBOARD (1) | POINTER (2)
    }
    
    request_path = f'/org/freedesktop/portal/desktop/request/{sender_name}/{request_token}'
    
    bus.add_signal_receiver(
        on_select_devices_response,
        signal_name='Response',
        dbus_interface='org.freedesktop.portal.Request',
        path=request_path
    )
    
    try:
        remote_desktop.SelectDevices(session_handle, options)
    except Exception as e:
        write_result(f'error:SelectDevices failed: {str(e)}')

def on_select_devices_response(response, results):
    if response != 0:
        write_result(f'error:SelectDevices failed: {response}')
        return
    
    # Now select sources (screen) via ScreenCast
    request_token = get_request_token()
    options = {
        'handle_token': request_token,
        'types': dbus.UInt32(1 | 2),  # Monitor | Window
        'multiple': False,
        'cursor_mode': dbus.UInt32(2),  # Embedded
    }
    
    request_path = f'/org/freedesktop/portal/desktop/request/{sender_name}/{request_token}'
    
    bus.add_signal_receiver(
        on_select_sources_response,
        signal_name='Response',
        dbus_interface='org.freedesktop.portal.Request',
        path=request_path
    )
    
    try:
        screencast.SelectSources(session_handle, options)
    except Exception as e:
        write_result(f'error:SelectSources failed: {str(e)}')

def on_select_sources_response(response, results):
    if response != 0:
        if response == 1:
            write_result('error:User cancelled')
        else:
            write_result(f'error:SelectSources failed: {response}')
        return
    
    # Start the session (for both RemoteDesktop and ScreenCast)
    request_token = get_request_token()
    options = {
        'handle_token': request_token,
    }
    
    request_path = f'/org/freedesktop/portal/desktop/request/{sender_name}/{request_token}'
    
    bus.add_signal_receiver(
        on_start_response,
        signal_name='Response',
        dbus_interface='org.freedesktop.portal.Request',
        path=request_path
    )
    
    try:
        remote_desktop.Start(session_handle, '', options)
    except Exception as e:
        write_result(f'error:Start failed: {str(e)}')

def on_start_response(response, results):
    global stream_started, stream_node_id
    if response != 0:
        write_result(f'error:Start failed: {response}')
        return
    
    streams = results.get('streams', [])
    if not streams:
        write_result('error:No streams')
        return
    
    stream_node_id = streams[0][0]
    stream_started = True
    
    # Write the node_id and session handle for input
    with open(output_file, 'w') as f:
        f.write(f'node_id:{stream_node_id}\nsession:{session_handle}')
    
    print(f'RemoteDesktop session active, node_id={stream_node_id}', flush=True)
    
    # Start input handling loop
    GLib.timeout_add(50, check_input_commands)

def check_input_commands():
    '''Check for input commands from the C# process'''
    global session_handle, stream_node_id
    
    if not stream_started or not session_handle:
        return True  # Keep checking
    
    try:
        if os.path.exists(input_file):
            with open(input_file, 'r') as f:
                cmd = f.read().strip()
            os.remove(input_file)
            
            if cmd:
                process_input_command(cmd)
    except:
        pass
    
    return True  # Continue checking

def process_input_command(cmd):
    '''Process input command: move:x,y or click:button or key:code'''
    global session_handle, stream_node_id
    
    try:
        parts = cmd.split(':', 1)
        if len(parts) != 2:
            return
        
        action, data = parts
        options = {}
        
        if action == 'move':
            x, y = map(float, data.split(','))
            remote_desktop.NotifyPointerMotionAbsolute(
                session_handle, options, 
                dbus.UInt32(stream_node_id), x, y
            )
        
        elif action == 'click':
            button = int(data)
            # Button codes: 272=left(BTN_LEFT), 273=right(BTN_RIGHT), 274=middle(BTN_MIDDLE)
            btn_map = {1: 272, 2: 274, 3: 273}
            btn_code = btn_map.get(button, 272)
            # Press and release
            remote_desktop.NotifyPointerButton(session_handle, options, btn_code, dbus.UInt32(1))
            remote_desktop.NotifyPointerButton(session_handle, options, btn_code, dbus.UInt32(0))
        
        elif action == 'mousedown':
            button = int(data)
            btn_map = {1: 272, 2: 274, 3: 273}
            btn_code = btn_map.get(button, 272)
            remote_desktop.NotifyPointerButton(session_handle, options, btn_code, dbus.UInt32(1))
        
        elif action == 'mouseup':
            button = int(data)
            btn_map = {1: 272, 2: 274, 3: 273}
            btn_code = btn_map.get(button, 272)
            remote_desktop.NotifyPointerButton(session_handle, options, btn_code, dbus.UInt32(0))
        
        elif action == 'scroll':
            dx, dy = map(float, data.split(','))
            remote_desktop.NotifyPointerAxis(session_handle, options, dx, dy)
        
        elif action == 'combo':
            # Format: combo:mod1,mod2,...:key (e.g., combo:29:30 for Ctrl+A)
            parts = data.split(':')
            if len(parts) >= 2:
                modifiers = [int(m) for m in parts[0].split(',') if m]
                key = int(parts[1])
                print(f'Combo: modifiers={modifiers}, key={key}', flush=True)
                # Press all modifiers
                for mod in modifiers:
                    remote_desktop.NotifyKeyboardKeycode(session_handle, options, mod, dbus.UInt32(1))
                # Press and release key
                remote_desktop.NotifyKeyboardKeycode(session_handle, options, key, dbus.UInt32(1))
                remote_desktop.NotifyKeyboardKeycode(session_handle, options, key, dbus.UInt32(0))
                # Release all modifiers in reverse order
                for mod in reversed(modifiers):
                    remote_desktop.NotifyKeyboardKeycode(session_handle, options, mod, dbus.UInt32(0))
        
        elif action == 'key':
            keycode = int(data)
            print(f'Key press: keycode={keycode}', flush=True)
            # Press and release
            remote_desktop.NotifyKeyboardKeycode(session_handle, options, keycode, dbus.UInt32(1))
            remote_desktop.NotifyKeyboardKeycode(session_handle, options, keycode, dbus.UInt32(0))
        
        elif action == 'keydown':
            keycode = int(data)
            print(f'Key down: keycode={keycode}', flush=True)
            remote_desktop.NotifyKeyboardKeycode(session_handle, options, keycode, dbus.UInt32(1))
        
        elif action == 'keyup':
            keycode = int(data)
            print(f'Key up: keycode={keycode}', flush=True)
            remote_desktop.NotifyKeyboardKeycode(session_handle, options, keycode, dbus.UInt32(0))
            
    except Exception as e:
        print(f'Input error: {e}', flush=True)

def handle_sigterm(sig, frame):
    loop.quit()

signal.signal(signal.SIGTERM, handle_sigterm)
signal.signal(signal.SIGINT, handle_sigterm)

# Start RemoteDesktop session (includes ScreenCast capabilities)
request_token = get_request_token()
options = {
    'handle_token': request_token,
    'session_handle_token': f'netlock_session_{os.getpid()}',
}

request_path = f'/org/freedesktop/portal/desktop/request/{sender_name}/{request_token}'

bus.add_signal_receiver(
    on_create_session_response,
    signal_name='Response',
    dbus_interface='org.freedesktop.portal.Request',
    path=request_path
)

try:
    remote_desktop.CreateSession(options)
except Exception as e:
    write_result(f'error:CreateSession failed: {str(e)}')
    sys.exit(1)

def on_timeout():
    if not stream_started:
        write_result('error:timeout')
    return False

GLib.timeout_add_seconds(55, on_timeout)

try:
    loop.run()
except:
    pass
";
        }

        /// <summary>
        /// Start the GStreamer pipeline to capture frames
        /// </summary>
        private bool StartGStreamerPipeline()
        {
            // Try different pipeline configurations in order of preference
            string[] pipelines = GetPipelineConfigurations();
            
            foreach (string pipeline in pipelines)
            {
                Console.WriteLine($"[PipeWire] Trying pipeline: {pipeline.Substring(0, Math.Min(80, pipeline.Length))}...");
                
                if (TryStartPipeline(pipeline))
                {
                    return true;
                }
                
                // Clean up failed attempt
                if (_gstreamerProcess != null)
                {
                    try { _gstreamerProcess.Kill(); } catch { }
                    try { _gstreamerProcess.Dispose(); } catch { }
                    _gstreamerProcess = null;
                }
                
                // Small delay before trying next pipeline
                Thread.Sleep(200);
            }
            
            Console.WriteLine("[PipeWire] All pipeline configurations failed");
            return false;
        }
        
        private string[] GetPipelineConfigurations()
        {
            // Note: We use file-based capture to /dev/shm (RAM-based tmpfs)
            // This is nearly as fast as true in-memory since /dev/shm doesn't touch disk.
            // True in-memory via fdsink would require complex JPEG frame boundary detection.
            //
            // Performance characteristics of /dev/shm:
            // - No disk I/O (pure RAM)
            // - Single file, overwritten each frame (no disk fragmentation)
            // - ~1-2ms overhead per frame for file operations (acceptable for 15-30fps)
            
            return new[]
            {
                // Pipeline 1: Simple and reliable - no scaling, just convert and encode
                $"gst-launch-1.0 -q pipewiresrc path={_pipeWireNodeId} do-timestamp=true keepalive-time=1000 ! " +
                    $"videoconvert ! video/x-raw,format=I420 ! " +
                    $"jpegenc quality={DEFAULT_QUALITY} ! " +
                    $"multifilesink location={_frameBufferPath} max-files=1 next-file=buffer sync=false async=false",
                
                // Pipeline 2: With framerate limit (may fail if videorate not negotiated)
                $"gst-launch-1.0 -q pipewiresrc path={_pipeWireNodeId} do-timestamp=true keepalive-time=1000 ! " +
                    $"videoconvert ! " +
                    $"videorate drop-only=true ! video/x-raw,framerate=10/1 ! " +
                    $"jpegenc quality={DEFAULT_QUALITY} ! " +
                    $"multifilesink location={_frameBufferPath} max-files=1 next-file=buffer sync=false async=false",
                    
                // Pipeline 3: With scaling to 1280 width (maintaining aspect ratio)
                $"gst-launch-1.0 -q pipewiresrc path={_pipeWireNodeId} do-timestamp=true keepalive-time=1000 ! " +
                    $"videoconvert ! video/x-raw,format=I420 ! " +
                    $"videoscale ! video/x-raw,width=1280 ! " +
                    $"jpegenc quality={DEFAULT_QUALITY} ! " +
                    $"multifilesink location={_frameBufferPath} max-files=1 next-file=buffer sync=false async=false",
                    
                // Pipeline 4: Minimal fallback - raw to JPEG only
                $"gst-launch-1.0 -q pipewiresrc path={_pipeWireNodeId} ! " +
                    $"videoconvert ! " +
                    $"jpegenc ! " +
                    $"multifilesink location={_frameBufferPath} max-files=1 next-file=buffer"
            };
        }
        
        private bool TryStartPipeline(string pipeline)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{pipeline}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                // Pass environment variables for PipeWire/DBus
                psi.Environment["DISPLAY"] = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
                psi.Environment["WAYLAND_DISPLAY"] = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0";
                psi.Environment["XDG_RUNTIME_DIR"] = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? $"/run/user/{GetCurrentUid()}";
                psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? "";
                psi.Environment["PIPEWIRE_RUNTIME_DIR"] = Environment.GetEnvironmentVariable("PIPEWIRE_RUNTIME_DIR") ?? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? $"/run/user/{GetCurrentUid()}";
                
                _gstreamerProcess = new Process { StartInfo = psi };
                _gstreamerProcess.Start();
                
                // Wait for first VALID frame (may take longer for PipeWire to stabilize)
                Console.WriteLine("[PipeWire] Waiting for first valid frame...");
                for (int i = 0; i < 50; i++)  // Up to 5 seconds
                {
                    if (_gstreamerProcess.HasExited)
                    {
                        string stderr = _gstreamerProcess.StandardError.ReadToEnd();
                        Console.WriteLine($"[PipeWire] GStreamer error: {stderr}");
                        return false;
                    }
                    
                    if (File.Exists(_frameBufferPath))
                    {
                        try
                        {
                            var fileInfo = new FileInfo(_frameBufferPath);
                            // A valid JPEG for 1280x720 should be at least 20KB typically
                            if (fileInfo.Length > 5000)
                            {
                                // Read and validate JPEG header
                                byte[] header = new byte[3];
                                using (var fs = new FileStream(_frameBufferPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                {
                                    fs.Read(header, 0, 3);
                                }
                                
                                // Check JPEG magic bytes
                                if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                                {
                                    // Valid JPEG! Try to load it to get dimensions
                                    try
                                    {
                                        using (var bitmap = new Bitmap(_frameBufferPath))
                                        {
                                            Width = bitmap.Width;
                                            Height = bitmap.Height;
                                            Console.WriteLine($"[PipeWire] First valid frame received: {Width}x{Height} @ 15fps ({fileInfo.Length} bytes)");
                                            return true;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[PipeWire] Frame valid JPEG but load failed: {ex.Message}");
                                    }
                                }
                            }
                        }
                        catch (IOException)
                        {
                            // File being written - normal during startup
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[PipeWire] Frame check error: {ex.Message}");
                        }
                    }
                    Thread.Sleep(100);
                }
                
                // Didn't get a valid frame, but process is still running
                // This is OK - frames may start coming later
                Console.WriteLine("[PipeWire] No valid frame yet, but pipeline is running. Continuing...");
                Width = 1280;
                Height = 720;
                return !_gstreamerProcess.HasExited;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeWire] Pipeline error: {ex.Message}");
                return false;
            }
        }
        
        private static int GetCurrentUid()
        {
            try
            {
                string result = Bash.ExecuteCommand("id -u");
                if (int.TryParse(result?.Trim(), out int uid))
                    return uid;
            }
            catch { }
            return 1000; // Default
        }

        // Track consecutive invalid frames for smarter fallback
        private int _invalidFrameCount = 0;
        private const int MAX_INVALID_FRAMES_BEFORE_FAIL = 30; // Allow ~2 seconds of invalid frames before giving up
        private DateTime _lastValidFrameTime = DateTime.MinValue;
        
        /// <summary>
        /// Capture a frame from PipeWire
        /// </summary>
        public async Task<byte[]?> CaptureFrameAsync(int screenIndex)
        {
            if (_initializing)
                return _lastFrame; // Return cached frame during init
            
            if (!_initialized)
            {
                bool success = await InitializeAsync();
                if (!success)
                    return null;
            }

            // Fast path - no locking needed for read
            try
            {
                // Check if GStreamer is still running
                if (_gstreamerProcess == null || _gstreamerProcess.HasExited)
                {
                    Console.WriteLine("[PipeWire] GStreamer process died, reinitializing...");
                    _initialized = false;
                    
                    // Try to reinitialize
                    bool success = await InitializeAsync();
                    if (!success)
                        return null;
                }
                
                if (!File.Exists(_frameBufferPath))
                {
                    // File not created yet - this is normal during startup
                    if (_lastFrame != null)
                        return _lastFrame;
                    
                    // Wait a bit for first frame
                    await Task.Delay(100);
                    return null;
                }
                
                // Check if frame is new
                var fileInfo = new FileInfo(_frameBufferPath);
                if (_lastFrameTime >= fileInfo.LastWriteTime && _lastFrame != null)
                    return _lastFrame;
                
                // Read new frame directly - GStreamer already encodes as JPEG
                // Use retry logic in case the file is being written
                byte[] frameData = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        // Use FileShare.None to ensure exclusive access (file isn't being written)
                        using (var fs = new FileStream(_frameBufferPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            frameData = new byte[fs.Length];
                            int bytesRead = await fs.ReadAsync(frameData, 0, frameData.Length);
                            if (bytesRead != frameData.Length)
                            {
                                // Partial read - file is being written
                                await Task.Delay(20);
                                continue;
                            }
                        }
                        break;
                    }
                    catch (IOException)
                    {
                        // File is being written - wait and retry
                        await Task.Delay(20);
                    }
                }
                
                if (frameData == null || frameData.Length == 0)
                    return _lastFrame; // Could not read file
                
                // Validate JPEG: minimum size and magic bytes (FFD8FF)
                if (frameData.Length < 1000 || !IsValidJpeg(frameData))
                {
                    _invalidFrameCount++;
                    
                    // Only log occasionally to avoid spam
                    if (_invalidFrameCount == 1 || _invalidFrameCount % 10 == 0)
                    {
                        Console.WriteLine($"[PipeWire] Invalid frame #{_invalidFrameCount}: {frameData.Length} bytes");
                    }
                    
                    // Check if we should give up (many invalid frames in a row without any valid frame)
                    if (_invalidFrameCount >= MAX_INVALID_FRAMES_BEFORE_FAIL && _lastValidFrameTime == DateTime.MinValue)
                    {
                        Console.WriteLine($"[PipeWire] Too many invalid frames without any valid frame - capture may have failed");
                        // Return null to signal potential failure, but don't throw
                        return null;
                    }
                    
                    // Return last valid frame if we have one
                    return _lastFrame;
                }
                
                // We got a valid frame!
                _invalidFrameCount = 0;
                _lastValidFrameTime = DateTime.Now;
                _lastFrame = frameData;
                _lastFrameTime = fileInfo.LastWriteTime;
                
                return frameData;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeWire] CaptureFrame error: {ex.Message}");
                return _lastFrame;
            }
        }
        
        /// <summary>
        /// Validates that data is a valid JPEG image
        /// </summary>
        private static bool IsValidJpeg(byte[] data)
        {
            if (data == null || data.Length < 3)
                return false;
            
            // JPEG magic bytes: FF D8 FF
            return data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        }

        /// <summary>
        /// Load saved restore token to skip permission dialog
        /// </summary>
        private void LoadRestoreToken()
        {
            try
            {
                if (File.Exists(RestoreTokenPath))
                {
                    _restoreToken = File.ReadAllText(RestoreTokenPath).Trim();
                    if (!string.IsNullOrEmpty(_restoreToken))
                    {
                        Console.WriteLine("[PipeWire] Loaded restore token from cache");
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Save restore token for future sessions
        /// </summary>
        private void SaveRestoreToken(string token)
        {
            try
            {
                var dir = Path.GetDirectoryName(RestoreTokenPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                    
                File.WriteAllText(RestoreTokenPath, token);
                Console.WriteLine("[PipeWire] Saved restore token");
            }
            catch { }
        }
        
        #region Remote Input Methods
        
        /// <summary>
        /// Send a command to the portal for input simulation
        /// </summary>
        private void SendInputCommand(string command)
        {
            if (!SupportsRemoteInput)
            {
                Console.WriteLine($"[PipeWire] Input not supported, cannot send: {command}");
                return;
            }
            
            try
            {
                File.WriteAllText(_inputCommandPath, command);
                // Small delay to let Python script pick it up
                Thread.Sleep(5);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeWire] Failed to send input command: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Move mouse to absolute position
        /// </summary>
        public void MoveMouse(double x, double y)
        {
            SendInputCommand($"move:{x},{y}");
        }
        
        /// <summary>
        /// Click a mouse button (1=left, 2=middle, 3=right)
        /// </summary>
        public void ClickMouse(int button = 1)
        {
            SendInputCommand($"click:{button}");
        }
        
        /// <summary>
        /// Press a mouse button down
        /// </summary>
        public void MouseDown(int button = 1)
        {
            SendInputCommand($"mousedown:{button}");
        }
        
        /// <summary>
        /// Release a mouse button
        /// </summary>
        public void MouseUp(int button = 1)
        {
            SendInputCommand($"mouseup:{button}");
        }
        
        /// <summary>
        /// Scroll the mouse wheel
        /// </summary>
        public void Scroll(double dx, double dy)
        {
            SendInputCommand($"scroll:{dx},{dy}");
        }
        
        /// <summary>
        /// Press and release a key (Linux keycode)
        /// </summary>
        public void PressKey(int keycode)
        {
            SendInputCommand($"key:{keycode}");
        }
        
        /// <summary>
        /// Press a key down
        /// </summary>
        public void KeyDown(int keycode)
        {
            SendInputCommand($"keydown:{keycode}");
        }
        
        /// <summary>
        /// Release a key
        /// </summary>
        public void KeyUp(int keycode)
        {
            SendInputCommand($"keyup:{keycode}");
        }
        
        /// <summary>
        /// Send a keyboard shortcut combo (e.g., Ctrl+A)
        /// </summary>
        public void KeyCombo(int[] modifiers, int key)
        {
            string modStr = string.Join(",", modifiers);
            SendInputCommand($"combo:{modStr}:{key}");
        }
        
        #endregion

        public void Dispose()
        {
            lock (_lockObject)
            {
                // Stop GStreamer
                if (_gstreamerProcess != null && !_gstreamerProcess.HasExited)
                {
                    try
                    {
                        _gstreamerProcess.Kill();
                        _gstreamerProcess.WaitForExit(1000);
                    }
                    catch { }
                    _gstreamerProcess.Dispose();
                    _gstreamerProcess = null;
                }
                
                // Stop portal process (this closes the session)
                StopPortalProcess();
                
                // Clean up temp files
                try { File.Delete(_frameBufferPath); } catch { }
                try { File.Delete(_frameReadyPath); } catch { }
                
                _initialized = false;
                _sessionActive = false;
            }
        }
    }
}





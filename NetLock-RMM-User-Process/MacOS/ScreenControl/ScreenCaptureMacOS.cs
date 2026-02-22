using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NetLock_RMM_User_Process.MacOS.Helper;

namespace NetLock_RMM_User_Process.MacOS.ScreenControl
{
    /// <summary>
    /// macOS screen capture implementation using screencapture CLI tool
    /// Uses JPEG compression directly without System.Drawing (which doesn't work on macOS)
    /// </summary>
    public class ScreenCaptureMacOS : IDisposable
    {
        private bool _initialized;
        private readonly object _lockObject = new object();
        private int _screenIndex;
        
        // Cached screen info
        private int _screenWidth = 1920;
        private int _screenHeight = 1080;
        private uint _displayId;
        
        // Processed frame cache (JPEG compressed)
        private byte[]? _lastProcessedFrame;
        private DateTime _lastProcessTime = DateTime.MinValue;
        
        // Temp file for screenshots
        private readonly string _screenshotPath;
        
        // Capture settings
        private const int MIN_CAPTURE_INTERVAL_MS = 33; // ~30fps max

        // Scaling information for coordinate translation
        public class ScalingInfo
        {
            public double ScaleX { get; set; } = 1.0;
            public double ScaleY { get; set; } = 1.0;
            public int OriginalWidth { get; set; }
            public int OriginalHeight { get; set; }
            public int ScaledWidth { get; set; }
            public int ScaledHeight { get; set; }
        }

        private static readonly System.Collections.Generic.Dictionary<int, ScalingInfo> ScalingFactors = 
            new System.Collections.Generic.Dictionary<int, ScalingInfo>();
        private static readonly object ScalingLock = new object();

        public ScreenCaptureMacOS(int screenIndex = 0)
        {
            _screenIndex = screenIndex;
            string uniqueId = $"{Environment.ProcessId}_{DateTime.Now.Ticks}";
            _screenshotPath = Path.Combine(Path.GetTempPath(), $"netlock_capture_{uniqueId}.jpg");
        }

        /// <summary>
        /// Initialize screen capture
        /// </summary>
        public bool Initialize()
        {
            lock (_lockObject)
            {
                if (_initialized)
                    return true;

                try
                {
                    // Get display info
                    var (displayId, x, y, width, height) = CoreGraphics.GetDisplayInfo(_screenIndex);
                    _displayId = displayId;
                    _screenWidth = width > 0 ? width : 1920;
                    _screenHeight = height > 0 ? height : 1080;
                    
                    Console.WriteLine($"[MacOS ScreenCapture] Display {_screenIndex}: ID={_displayId}, Size={_screenWidth}x{_screenHeight}");
                    Console.WriteLine("[MacOS ScreenCapture] Using screencapture CLI (no GDI+ dependency)");
                    
                    _initialized = true;
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MacOS ScreenCapture] Initialization error: {ex.Message}");
                    _initialized = true; // Still mark as initialized
                    return true;
                }
            }
        }

        /// <summary>
        /// Get the number of available screens
        /// </summary>
        public int GetScreenCount()
        {
            return CoreGraphics.GetDisplayCount();
        }

        /// <summary>
        /// Get scaling info for coordinate translation
        /// </summary>
        public ScalingInfo GetScalingInfo(int screenIndex)
        {
            lock (ScalingLock)
            {
                if (ScalingFactors.TryGetValue(screenIndex, out var info))
                    return info;

                // Return default scaling
                return new ScalingInfo
                {
                    ScaleX = 1.0,
                    ScaleY = 1.0,
                    OriginalWidth = _screenWidth,
                    OriginalHeight = _screenHeight,
                    ScaledWidth = _screenWidth,
                    ScaledHeight = _screenHeight
                };
            }
        }

        /// <summary>
        /// Capture screen and return compressed JPEG bytes
        /// </summary>
        public async Task<byte[]?> CaptureScreenToBytes(int screenIndex)
        {
            if (!_initialized && !Initialize())
            {
                Console.WriteLine("[MacOS ScreenCapture] Not initialized");
                return null;
            }

            // Rate limiting
            var now = DateTime.Now;
            var timeSinceLastProcess = (now - _lastProcessTime).TotalMilliseconds;
            if (timeSinceLastProcess < MIN_CAPTURE_INTERVAL_MS && _lastProcessedFrame != null)
            {
                return _lastProcessedFrame;
            }

            try
            {
                return await CaptureWithScreencapture(screenIndex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS ScreenCapture] Capture error: {ex.Message}");
                return _lastProcessedFrame; // Return cached frame on error
            }
        }

        /// <summary>
        /// Capture using screencapture CLI - produces JPEG directly
        /// </summary>
        private async Task<byte[]?> CaptureWithScreencapture(int screenIndex)
        {
            return await Task.Run(() =>
            {
                lock (_lockObject)
                {
                    try
                    {
                        // screencapture uses 1-based display numbering
                        int displayNum = screenIndex + 1;
                        
                        // Use screencapture with JPEG output
                        // -x = no sound, -t jpg = JPEG format, -D = display number
                        var psi = new ProcessStartInfo
                        {
                            FileName = "screencapture",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        psi.ArgumentList.Add("-x");
                        psi.ArgumentList.Add("-t");
                        psi.ArgumentList.Add("jpg");
                        psi.ArgumentList.Add("-D");
                        psi.ArgumentList.Add(displayNum.ToString());
                        psi.ArgumentList.Add(_screenshotPath);

                        using var process = Process.Start(psi);
                        if (process == null)
                        {
                            Console.WriteLine("[MacOS ScreenCapture] Failed to start screencapture");
                            return null;
                        }

                        if (!process.WaitForExit(3000))
                        {
                            try { process.Kill(); } catch { }
                            Console.WriteLine("[MacOS ScreenCapture] screencapture timeout");
                            return null;
                        }

                        if (!File.Exists(_screenshotPath))
                        {
                            var stderr = process.StandardError.ReadToEnd();
                            Console.WriteLine($"[MacOS ScreenCapture] Screenshot file not created. stderr: {stderr}");
                            return null;
                        }

                        // Read the JPEG file directly - no processing needed
                        byte[] imageBytes = File.ReadAllBytes(_screenshotPath);
                        
                        // Store scaling info (assuming no scaling for now)
                        lock (ScalingLock)
                        {
                            ScalingFactors[screenIndex] = new ScalingInfo
                            {
                                ScaleX = 1.0,
                                ScaleY = 1.0,
                                OriginalWidth = _screenWidth,
                                OriginalHeight = _screenHeight,
                                ScaledWidth = _screenWidth,
                                ScaledHeight = _screenHeight
                            };
                        }
                        
                        if (imageBytes.Length > 0)
                        {
                            _lastProcessedFrame = imageBytes;
                            _lastProcessTime = DateTime.Now;
                        }

                        return imageBytes;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MacOS ScreenCapture] Screencapture error: {ex.Message}");
                        return null;
                    }
                }
            });
        }

        /// <summary>
        /// Translate coordinates from scaled remote coordinates to actual screen coordinates
        /// </summary>
        public (int x, int y) TranslateCoordinates(int screenIndex, int x, int y)
        {
            var info = GetScalingInfo(screenIndex);
            
            // Scale up from the transmitted (scaled) coordinates to actual screen coordinates
            int actualX = (int)(x / info.ScaleX);
            int actualY = (int)(y / info.ScaleY);
            
            return (actualX, actualY);
        }

        public void Dispose()
        {
            lock (_lockObject)
            {
                // Clean up temp file
                try
                {
                    if (File.Exists(_screenshotPath))
                        File.Delete(_screenshotPath);
                }
                catch { }
                
                _initialized = false;
            }
        }
    }
}


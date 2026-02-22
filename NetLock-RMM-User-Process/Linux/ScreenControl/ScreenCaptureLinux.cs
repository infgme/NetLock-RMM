using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NetLock_RMM_User_Process.Linux.Helper;

namespace NetLock_RMM_User_Process.Linux.ScreenControl
{
    /// <summary>
    /// Linux screen capture implementation supporting both X11 and Wayland
    /// Uses native X11 calls for X11 sessions, CLI tools for Wayland
    /// </summary>
    public class ScreenCaptureLinux : IDisposable
    {
        private IntPtr _display;
        private bool _initialized;
        private readonly object _lockObject = new object();
        private int _screenIndex;
        
        // Cached screen info
        private int _screenWidth;
        private int _screenHeight;
        private IntPtr _rootWindow;

        // Screenshot tool for Wayland fallback
        private string? _screenshotTool;
        
        // PipeWire capture (fastest method for Wayland)
        private PipeWireScreenCapture? _pipeWireCapture;
        private bool _usePipeWire;
        
        // Static reference for input control access
        private static PipeWireScreenCapture? _activePipeWireCapture;
        
        /// <summary>
        /// Gets the active PipeWire capture instance for input simulation (used by MouseControlLinux)
        /// </summary>
        public static PipeWireScreenCapture? ActivePipeWireCapture => _activePipeWireCapture;

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

        // Maximum allowed image dimension before automatic downscaling
        private const int MAX_IMAGE_DIMENSION = 1600;
        private const int DEFAULT_QUALITY = 60;

        public ScreenCaptureLinux(int screenIndex = 0)
        {
            _screenIndex = screenIndex;
        }

        /// <summary>
        /// Initializes the screen capture for X11
        /// </summary>
        public bool Initialize()
        {
            lock (_lockObject)
            {
                if (_initialized)
                    return true;

                try
                {
                    Console.WriteLine($"[ScreenCapture] IsX11={DisplayServerDetector.IsX11}, IsWayland={DisplayServerDetector.IsWayland}");
                    
                    if (DisplayServerDetector.IsWayland)
                    {
                        Console.WriteLine("[ScreenCapture] Using Wayland capture path");
                        return InitializeWayland();
                    }
                    else if (DisplayServerDetector.IsX11)
                    {
                        Console.WriteLine("[ScreenCapture] Using X11 capture path");
                        return InitializeX11();
                    }
                    else
                    {
                        Console.WriteLine("[ScreenCapture] Unknown display server");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ScreenCapture] Initialization failed: {ex.Message}");
                    return false;
                }
            }
        }

        private bool InitializeX11()
        {
            try
            {
                string displayName = DisplayServerDetector.GetX11Display();
                _display = LibX11.XOpenDisplay(displayName);

                if (_display == IntPtr.Zero)
                {
                    Console.WriteLine($"[ScreenCapture] Failed to open X11 display: {displayName}");
                    return false;
                }

                int screenCount = LibX11.XScreenCount(_display);
                if (_screenIndex >= screenCount)
                {
                    Console.WriteLine($"[ScreenCapture] Screen index {_screenIndex} out of range (max: {screenCount - 1})");
                    _screenIndex = 0;
                }

                _screenWidth = LibX11.XDisplayWidth(_display, _screenIndex);
                _screenHeight = LibX11.XDisplayHeight(_display, _screenIndex);
                _rootWindow = LibX11.XRootWindow(_display, _screenIndex);

                Console.WriteLine($"[ScreenCapture] X11 initialized - Screen {_screenIndex}: {_screenWidth}x{_screenHeight}");
                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] X11 initialization error: {ex.Message}");
                return false;
            }
        }

        private bool InitializeWayland()
        {
            try
            {
                // Always detect a fallback screenshot tool first
                _screenshotTool = DisplayServerDetector.GetAvailableScreenshotTool();
                Console.WriteLine($"[ScreenCapture] Fallback tool: {_screenshotTool ?? "none"}");
                
                // Try PipeWire first (fastest method for Wayland)
                if (PipeWireScreenCapture.IsAvailable())
                {
                    Console.WriteLine("[ScreenCapture] Using PipeWire for Wayland capture");
                    _pipeWireCapture = new PipeWireScreenCapture();
                    _activePipeWireCapture = _pipeWireCapture;  // Set static reference for input
                    _usePipeWire = true;
                    _screenWidth = 1920;
                    _screenHeight = 1080;
                    _initialized = true;
                    return true;
                }
                
                Console.WriteLine("[ScreenCapture] PipeWire not available, using CLI tools");
                
                if (string.IsNullOrEmpty(_screenshotTool))
                {
                    Console.WriteLine("[ScreenCapture] No screenshot tool found for Wayland");
                    return false;
                }

                // Get screen resolution
                string resolution = Bash.ExecuteCommand("xrandr 2>/dev/null | grep '*' | head -1 | awk '{print $1}'");
                if (!string.IsNullOrEmpty(resolution) && resolution.Contains("x"))
                {
                    var parts = resolution.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                    {
                        _screenWidth = w;
                        _screenHeight = h;
                    }
                }

                if (_screenWidth == 0 || _screenHeight == 0)
                {
                    _screenWidth = 1920;
                    _screenHeight = 1080;
                }

                Console.WriteLine($"[ScreenCapture] Wayland with {_screenshotTool}, {_screenWidth}x{_screenHeight}");
                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] Wayland init error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Captures the screen and returns compressed JPEG bytes
        /// </summary>
        public async Task<byte[]> CaptureScreenToBytes(int screenIndex)
        {
            if (!_initialized && !Initialize())
            {
                Console.WriteLine("[ScreenCapture] Not initialized");
                return null;
            }

            try
            {
                if (DisplayServerDetector.IsX11)
                {
                    return await CaptureX11(screenIndex);
                }
                else if (DisplayServerDetector.IsWayland)
                {
                    return await CaptureWayland(screenIndex);
                }
                else
                {
                    Console.WriteLine("[ScreenCapture] Unknown display server");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] Capture error: {ex.Message}");
                return null;
            }
        }

        private async Task<byte[]> CaptureX11(int screenIndex)
        {
            return await Task.Run(() =>
            {
                lock (_lockObject)
                {
                    try
                    {
                        // Get fresh screen dimensions
                        int width = LibX11.XDisplayWidth(_display, screenIndex);
                        int height = LibX11.XDisplayHeight(_display, screenIndex);
                        IntPtr rootWindow = LibX11.XRootWindow(_display, screenIndex);

                        // Capture the screen using XGetImage
                        IntPtr xImage = LibX11.XGetImage(
                            _display,
                            rootWindow,
                            0, 0,
                            (uint)width, (uint)height,
                            LibX11.AllPlanes,
                            LibX11.ZPixmap
                        );

                        if (xImage == IntPtr.Zero)
                        {
                            Console.WriteLine("[ScreenCapture] XGetImage failed");
                            return null;
                        }

                        try
                        {
                            // Read the XImage structure
                            var imageStruct = Marshal.PtrToStructure<LibX11.XImage>(xImage);
                            
                            // Convert to Bitmap
                            using (var bitmap = ConvertXImageToBitmap(imageStruct, width, height))
                            {
                                if (bitmap == null)
                                    return null;

                                // Scale if needed and compress
                                return CompressAndScale(bitmap, screenIndex);
                            }
                        }
                        finally
                        {
                            // Free the XImage
                            LibX11.XDestroyImage(xImage);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ScreenCapture] X11 capture error: {ex.Message}");
                        return null;
                    }
                }
            });
        }

        private Bitmap ConvertXImageToBitmap(LibX11.XImage xImage, int width, int height)
        {
            try
            {
                // Create bitmap based on bits per pixel
                PixelFormat pixelFormat;
                switch (xImage.bits_per_pixel)
                {
                    case 32:
                        pixelFormat = PixelFormat.Format32bppRgb;
                        break;
                    case 24:
                        pixelFormat = PixelFormat.Format24bppRgb;
                        break;
                    default:
                        Console.WriteLine($"[ScreenCapture] Unsupported bits per pixel: {xImage.bits_per_pixel}");
                        return null;
                }

                var bitmap = new Bitmap(width, height, pixelFormat);
                var bitmapData = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    pixelFormat
                );

                try
                {
                    int bytesPerPixel = xImage.bits_per_pixel / 8;
                    int srcStride = xImage.bytes_per_line;
                    int dstStride = bitmapData.Stride;

                    // Copy pixel data row by row
                    // X11 uses BGRA format typically, which matches .NET's format
                    for (int y = 0; y < height; y++)
                    {
                        IntPtr srcRow = IntPtr.Add(xImage.data, y * srcStride);
                        IntPtr dstRow = IntPtr.Add(bitmapData.Scan0, y * dstStride);

                        // Copy the row (handle stride differences)
                        int copyBytes = Math.Min(srcStride, Math.Abs(dstStride));
                        copyBytes = Math.Min(copyBytes, width * bytesPerPixel);

                        unsafe
                        {
                            Buffer.MemoryCopy(
                                srcRow.ToPointer(),
                                dstRow.ToPointer(),
                                copyBytes,
                                copyBytes
                            );
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] Bitmap conversion error: {ex.Message}");
                return null;
            }
        }

        // Track PipeWire consecutive failures before falling back
        private int _pipeWireFailCount = 0;
        private const int MAX_PIPEWIRE_FAILS_BEFORE_FALLBACK = 50; // Allow ~3-5 seconds of failures during startup

        private async Task<byte[]> CaptureWayland(int screenIndex)
        {
            // Try PipeWire first (fastest method)
            if (_usePipeWire && _pipeWireCapture != null)
            {
                try
                {
                    byte[] frame = await _pipeWireCapture.CaptureFrameAsync(screenIndex);
                    if (frame != null && frame.Length > 0)
                    {
                        // Success! Reset fail count
                        _pipeWireFailCount = 0;
                        
                        // Update screen dimensions from PipeWire
                        _screenWidth = _pipeWireCapture.Width;
                        _screenHeight = _pipeWireCapture.Height;
                        return frame;
                    }
                    
                    // No frame yet - this is normal during startup
                    _pipeWireFailCount++;
                    
                    // Only log occasionally to avoid spam
                    if (_pipeWireFailCount == 1 || _pipeWireFailCount % 20 == 0)
                    {
                        Console.WriteLine($"[ScreenCapture] PipeWire capture returned null (attempt {_pipeWireFailCount})");
                    }
                    
                    // Check if we should give up on PipeWire
                    if (_pipeWireFailCount >= MAX_PIPEWIRE_FAILS_BEFORE_FALLBACK)
                    {
                        Console.WriteLine("[ScreenCapture] PipeWire capture failed too many times, falling back to CLI tools");
                        _usePipeWire = false;
                        _activePipeWireCapture = null;
                        _pipeWireCapture?.Dispose();
                        _pipeWireCapture = null;
                        
                        // Initialize CLI tool fallback
                        _screenshotTool = DisplayServerDetector.GetAvailableScreenshotTool();
                        if (string.IsNullOrEmpty(_screenshotTool))
                        {
                            Console.WriteLine("[ScreenCapture] No screenshot tool available for fallback");
                            return null;
                        }
                        // Fall through to CLI tool capture below
                    }
                    else
                    {
                        // Not yet at threshold - return null and let caller retry
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    _pipeWireFailCount++;
                    Console.WriteLine($"[ScreenCapture] PipeWire error (attempt {_pipeWireFailCount}): {ex.Message}");
                    
                    if (_pipeWireFailCount >= MAX_PIPEWIRE_FAILS_BEFORE_FALLBACK)
                    {
                        Console.WriteLine("[ScreenCapture] PipeWire failed, falling back to CLI tools");
                        _usePipeWire = false;
                        _activePipeWireCapture = null;
                        _pipeWireCapture?.Dispose();
                        _pipeWireCapture = null;
                        _screenshotTool = DisplayServerDetector.GetAvailableScreenshotTool();
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            
            // Fallback to CLI tools
            return await Task.Run(() =>
            {
                try
                {
                    // Priority: Use tools that can output directly to stdout for best performance
                    byte[] imageData = null;
                    
                    switch (_screenshotTool)
                    {
                        case "grim":
                            // grim can output PNG directly to stdout - fastest option!
                            imageData = CaptureWithGrimStdout(screenIndex);
                            break;
                        case "gnome-screenshot":
                            // gnome-screenshot is slow but we can try to optimize
                            imageData = CaptureWithGnomeScreenshot(screenIndex);
                            break;
                        case "spectacle":
                            imageData = CaptureWithSpectacle(screenIndex);
                            break;
                        case "scrot":
                            imageData = CaptureWithScrot(screenIndex);
                            break;
                        case "import":
                            imageData = CaptureWithImport(screenIndex);
                            break;
                        default:
                            Console.WriteLine($"[ScreenCapture] Unknown tool: {_screenshotTool}");
                            return null;
                    }
                    
                    return imageData;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ScreenCapture] Wayland capture error: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// Capture using grim with stdout pipe - fastest Wayland method
        /// </summary>
        private byte[] CaptureWithGrimStdout(int screenIndex)
        {
            try
            {
                // grim outputs PNG to stdout when using "-" as filename
                // We pipe it directly to memory without disk I/O
                var psi = new ProcessStartInfo
                {
                    FileName = "grim",
                    Arguments = "-t png -", // Output to stdout
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return null;

                    using (var ms = new MemoryStream())
                    {
                        process.StandardOutput.BaseStream.CopyTo(ms);
                        process.WaitForExit(2000); // 2 second timeout
                        
                        if (ms.Length > 0)
                        {
                            ms.Position = 0;
                            using (var bitmap = new Bitmap(ms))
                            {
                                return CompressAndScale(bitmap, screenIndex);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] grim stdout capture failed: {ex.Message}");
            }
            
            // Fallback to file-based capture
            return CaptureWithFileFallback("grim", screenIndex);
        }

        /// <summary>
        /// gnome-screenshot is slow - use shared memory file for speed
        /// </summary>
        private byte[] CaptureWithGnomeScreenshot(int screenIndex)
        {
            // Use /dev/shm (RAM-based tmpfs) to avoid disk I/O
            string tempFile = $"/dev/shm/netlock_screenshot_{Environment.ProcessId}.png";
            
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "gnome-screenshot",
                    Arguments = $"-f {tempFile}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return null;
                    // gnome-screenshot is slow, give it more time but not too much
                    if (!process.WaitForExit(3000))
                    {
                        try { process.Kill(); } catch { }
                        Console.WriteLine("[ScreenCapture] gnome-screenshot timeout");
                        return null;
                    }
                }

                if (File.Exists(tempFile))
                {
                    using (var bitmap = new Bitmap(tempFile))
                    {
                        return CompressAndScale(bitmap, screenIndex);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] gnome-screenshot failed: {ex.Message}");
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
            
            return null;
        }

        private byte[] CaptureWithSpectacle(int screenIndex)
        {
            return CaptureWithFileFallback("spectacle -b -n -o", screenIndex);
        }

        private byte[] CaptureWithScrot(int screenIndex)
        {
            return CaptureWithFileFallback("scrot", screenIndex);
        }

        private byte[] CaptureWithImport(int screenIndex)
        {
            return CaptureWithFileFallback("import -window root", screenIndex);
        }

        /// <summary>
        /// Generic file-based capture fallback using /dev/shm for speed
        /// </summary>
        private byte[] CaptureWithFileFallback(string toolCommand, int screenIndex)
        {
            // Use shared memory for faster I/O
            string tempFile = $"/dev/shm/netlock_screenshot_{Environment.ProcessId}.png";
            
            try
            {
                string command = $"{toolCommand} {tempFile} 2>/dev/null";
                Bash.ExecuteCommand(command);

                if (File.Exists(tempFile))
                {
                    using (var bitmap = new Bitmap(tempFile))
                    {
                        return CompressAndScale(bitmap, screenIndex);
                    }
                }
                else
                {
                    Console.WriteLine("[ScreenCapture] Screenshot file not created");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] File-based capture error: {ex.Message}");
                return null;
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        private byte[] CompressAndScale(Bitmap bitmap, int screenIndex)
        {
            try
            {
                int originalWidth = bitmap.Width;
                int originalHeight = bitmap.Height;

                // Calculate scale if image is too large
                double scale = 1.0;
                if (originalWidth > MAX_IMAGE_DIMENSION || originalHeight > MAX_IMAGE_DIMENSION)
                {
                    double scaleX = (double)MAX_IMAGE_DIMENSION / originalWidth;
                    double scaleY = (double)MAX_IMAGE_DIMENSION / originalHeight;
                    scale = Math.Min(scaleX, scaleY);
                }

                int newWidth = (int)(originalWidth * scale);
                int newHeight = (int)(originalHeight * scale);

                // Store scaling info for coordinate translation
                lock (ScalingLock)
                {
                    ScalingFactors[screenIndex] = new ScalingInfo
                    {
                        ScaleX = scale,
                        ScaleY = scale,
                        OriginalWidth = originalWidth,
                        OriginalHeight = originalHeight,
                        ScaledWidth = newWidth,
                        ScaledHeight = newHeight
                    };
                }

                Bitmap outputBitmap = bitmap;
                bool needsDispose = false;

                // Resize if needed
                if (scale < 1.0)
                {
                    outputBitmap = new Bitmap(bitmap, new Size(newWidth, newHeight));
                    needsDispose = true;
                }

                try
                {
                    using (var ms = new MemoryStream())
                    {
                        // Get JPEG encoder
                        var jpegEncoder = GetJpegEncoder();
                        if (jpegEncoder != null)
                        {
                            var encoderParams = new EncoderParameters(1);
                            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)DEFAULT_QUALITY);
                            outputBitmap.Save(ms, jpegEncoder, encoderParams);
                        }
                        else
                        {
                            outputBitmap.Save(ms, ImageFormat.Jpeg);
                        }

                        return ms.ToArray();
                    }
                }
                finally
                {
                    if (needsDispose)
                        outputBitmap.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] Compression error: {ex.Message}");
                return null;
            }
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            var encoders = ImageCodecInfo.GetImageEncoders();
            foreach (var encoder in encoders)
            {
                if (encoder.MimeType == "image/jpeg")
                    return encoder;
            }
            return null;
        }

        /// <summary>
        /// Gets the scaling info for coordinate translation
        /// </summary>
        public static ScalingInfo GetScalingInfo(int screenIndex)
        {
            lock (ScalingLock)
            {
                if (ScalingFactors.TryGetValue(screenIndex, out var info))
                    return info;
                return new ScalingInfo();
            }
        }

        /// <summary>
        /// Gets the number of screens
        /// </summary>
        public int GetScreenCount()
        {
            if (DisplayServerDetector.IsX11 && _display != IntPtr.Zero)
            {
                return LibX11.XScreenCount(_display);
            }

            // For Wayland, try to get from xrandr
            string output = Bash.ExecuteCommand("xrandr 2>/dev/null | grep ' connected' | wc -l");
            if (int.TryParse(output.Trim(), out int count) && count > 0)
                return count;

            return 1; // Default to 1 screen
        }

        public void Dispose()
        {
            lock (_lockObject)
            {
                // Dispose PipeWire capture
                _pipeWireCapture?.Dispose();
                _pipeWireCapture = null;
                
                if (_display != IntPtr.Zero)
                {
                    LibX11.XCloseDisplay(_display);
                    _display = IntPtr.Zero;
                }
                _initialized = false;
            }
        }
    }
}


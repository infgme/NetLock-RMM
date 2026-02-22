using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace NetLock_RMM_User_Process.Windows.ScreenControl
{
    /// <summary>
    /// GPU-accelerated screen capture using Desktop Duplication API.
    /// Much faster than BitBlt with lower CPU usage.
    /// </summary>
    public class DesktopDuplicationApiCapture : IDisposable
    {
        private Device _device;
        private OutputDuplication _outputDuplication;
        private Texture2D _screenTexture;
        private Output1 _output1;
        private readonly int _adapterIndex;
        private readonly int _outputIndex;
        private bool _initialized;
        private readonly object _lockObject = new object();
        
        // Pool for staging textures to avoid constant reallocation
        private Texture2D _stagingTexture;
        private int _lastWidth;
        private int _lastHeight;

        // Maximum allowed image dimension before automatic downscaling
        private const int MAX_IMAGE_DIMENSION = 1600;

        // Lower default quality for initial compression guess
        private const int DEFAULT_QUALITY = 60;

        // Maximum attempts for resizing/compression to meet size limit
        private const int MAX_COMPRESSION_ATTEMPTS = 4;

        // Frame caching for rate limiting
        private DateTime _lastCaptureTime = DateTime.MinValue;
        private byte[] _lastCompressedFrame;
        private const int MIN_FRAME_INTERVAL_MS = 16; // ~60 FPS max

        // Adaptive timeout based on activity
        private int _currentTimeout = 100;
        private const int MIN_TIMEOUT = 50;
        private const int MAX_TIMEOUT = 500;
        private int _consecutiveNoChanges = 0;

        // Debug logging control - disable to reduce CPU overhead from Console.WriteLine
        private static bool _enableDebugLogging = false;
        
        private static void DebugLog(string message)
        {
            if (_enableDebugLogging)
            {
                Console.WriteLine(message);
            }
        }

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
        
        // Pool for EncoderParameters to reduce GC pressure
        [ThreadStatic]
        private static EncoderParameters _encoderParamsPool;
        
        // MemoryStream pool to reduce allocations
        [ThreadStatic]
        private static MemoryStream _memoryStreamPool;
        
        private static EncoderParameters GetPooledEncoderParams(long quality)
        {
            if (_encoderParamsPool == null)
            {
                _encoderParamsPool = new EncoderParameters(1);
            }
            
            // EncoderParameter is immutable, must recreate each time
            // But we reuse the EncoderParameters container
            _encoderParamsPool.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            return _encoderParamsPool;
        }
        
        private static MemoryStream GetPooledMemoryStream()
        {
            if (_memoryStreamPool == null)
            {
                _memoryStreamPool = new MemoryStream(1024 * 512); // 512KB initial capacity
            }
            else
            {
                _memoryStreamPool.Position = 0;
                _memoryStreamPool.SetLength(0);
            }
            return _memoryStreamPool;
        }

        public DesktopDuplicationApiCapture(int adapterIndex = 0, int outputIndex = 0)
        {
            _adapterIndex = adapterIndex;
            _outputIndex = outputIndex;
        }

        /// <summary>
        /// Find the correct adapter and output indices for a given screen index.
        /// Returns (adapterIndex, outputIndex) tuple.
        /// </summary>
        public static (int adapterIndex, int outputIndex) FindAdapterForScreen(int screenIndex)
        {
            try
            {
                using var factory = new Factory1();
                int currentScreenIndex = 0;
                int adapterCount = factory.GetAdapterCount1();
                
                Console.WriteLine($"=== Finding screen {screenIndex} ===");
                Console.WriteLine($"Total adapters: {adapterCount}");
                
                // Enumerate all adapters
                for (int adapterIdx = 0; adapterIdx < adapterCount; adapterIdx++)
                {
                    using var adapter = factory.GetAdapter1(adapterIdx);
                    int outputCount = adapter.GetOutputCount();

                    Console.WriteLine($"Adapter {adapterIdx}: {outputCount} outputs");

                    // Enumerate all outputs on this adapter
                    for (int outputIdx = 0; outputIdx < outputCount; outputIdx++)
                    {
                        try
                        {
                            using var output = adapter.GetOutput(outputIdx);
                            var desc = output.Description;
                            Console.WriteLine($"  Output {outputIdx}: {desc.DeviceName} (Attached: {desc.IsAttachedToDesktop})");

                            // Only count outputs that are actually attached to the desktop
                            if (desc.IsAttachedToDesktop)
                            {
                                if (currentScreenIndex == screenIndex)
                                {
                                    // Found the matching screen!
                                    Console.WriteLine($">>> Screen {screenIndex} mapped to Adapter {adapterIdx}, Output {outputIdx}");
                                    return (adapterIdx, outputIdx);
                                }
                                currentScreenIndex++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  Output {outputIdx}: Error getting info - {ex.Message}");
                        }
                    }
                }
                
                // Not found, return default
                Console.WriteLine($"Screen {screenIndex} not found (only {currentScreenIndex} screens detected), using Adapter 0, Output {screenIndex}");
                return (0, screenIndex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error finding adapter for screen {screenIndex}: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return (0, screenIndex);
            }
        }

        /// <summary>
        /// Initialize the Desktop Duplication API
        /// </summary>
        public bool Initialize()
        {
            lock (_lockObject)
            {
                try
                {
                    if (_initialized)
                        return true;

                    // Create DXGI Factory
                    using var factory = new Factory1();
                    
                    // Get adapter
                    var adapter = factory.GetAdapter1(_adapterIndex);
                    if (adapter == null)
                    {
                        Console.WriteLine($"Adapter {_adapterIndex} not found");
                        return false;
                    }

                    // Create D3D11 Device
                    _device = new Device(adapter, DeviceCreationFlags.BgraSupport);

                    // Get output (monitor)
                    var output = adapter.GetOutput(_outputIndex);
                    if (output == null)
                    {
                        Console.WriteLine($"Output {_outputIndex} not found");
                        adapter.Dispose();
                        return false;
                    }

                    _output1 = output.QueryInterface<Output1>();
                    output.Dispose();

                    // Create Desktop Duplication
                    _outputDuplication = _output1.DuplicateOutput(_device);

                    _initialized = true;
                    Console.WriteLine($"Desktop Duplication initialized for adapter {_adapterIndex}, output {_outputIndex}");
                    return true;
                }
                catch (SharpDXException ex)
                {
                    Console.WriteLine($"Failed to initialize Desktop Duplication: {ex.Message}");
                    Console.WriteLine($"HRESULT: 0x{ex.HResult:X8}");
                    
                    // Cleanup on failure
                    Cleanup();
                    return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error initializing Desktop Duplication: {ex.Message}");
                    Cleanup();
                    return false;
                }
            }
        }

        /// <summary>
        /// Capture a frame from the duplicated output
        /// </summary>
        public Bitmap CaptureFrame()
        {
            // Ensure we're on the input desktop before capturing
            Helper.SessionManager.TrySwitchToInputDesktop();
            
            // Use shorter timeout for lock to prevent hanging
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(_lockObject, 100, ref lockTaken);
                if (!lockTaken)
                {
                    DebugLog("Could not acquire lock, another capture in progress");
                    return null;
                }

                if (!_initialized)
                {
                    DebugLog("Desktop Duplication not initialized");
                    return null;
                }

                SharpDX.DXGI.Resource screenResource = null;
                OutputDuplicateFrameInformation frameInfo;

                try
                {
                    // Use adaptive timeout - longer when screen is idle
                    var result = _outputDuplication.TryAcquireNextFrame(_currentTimeout, out frameInfo, out screenResource);
                    
                    if (result.Failure)
                    {
                        // Timeout or no frame available - this is normal if screen hasn't changed
                        if (result.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Code)
                        {
                            // Increase timeout for next capture (screen is idle)
                            _consecutiveNoChanges++;
                            _currentTimeout = Math.Min(_currentTimeout + 50, MAX_TIMEOUT);
                            
                            // Try to get the current desktop image without waiting for update
                            return CaptureCurrentFrame();
                        }
                        
                        // Access lost - need to reinitialize
                        if (result.Code == SharpDX.DXGI.ResultCode.AccessLost.Code)
                        {
                            Console.WriteLine("Access lost, reinitializing...");
                            Cleanup();
                            Initialize();
                            return null;
                        }
                        
                        DebugLog($"Failed to acquire frame: 0x{result.Code:X8}");
                        return null;
                    }

                    // Frame acquired successfully - reset adaptive timeout (screen is active)
                    _consecutiveNoChanges = 0;
                    _currentTimeout = Math.Max(_currentTimeout - 25, MIN_TIMEOUT);

                    // Get texture from resource
                    using var texture2D = screenResource.QueryInterface<Texture2D>();
                    
                    // Create bitmap from texture
                    var bitmap = TextureToBitmap(texture2D);
                    return bitmap;
                }
                catch (SharpDXException ex)
                {
                    Console.WriteLine($"SharpDX error capturing frame: {ex.Message} (0x{ex.HResult:X8})");
                    
                    // Handle access lost
                    if (ex.ResultCode == SharpDX.DXGI.ResultCode.AccessLost)
                    {
                        Console.WriteLine("Access lost, will reinitialize on next call");
                        Cleanup();
                    }
                    
                    return null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error capturing frame: {ex.Message}");
                    return null;
                }
                finally
                {
                    // Always release the frame - CRITICAL to prevent hanging!
                    try
                    {
                        screenResource?.Dispose();
                        if (_outputDuplication != null)
                        {
                            _outputDuplication.ReleaseFrame();
                        }
                    }
                    catch (SharpDXException ex) when (ex.ResultCode.Code == -2005270523) // DXGI_ERROR_INVALID_CALL
                    {
                        // Frame was already released or not acquired - ignore
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Error releasing frame: {ex.Message}");
                    }
                }
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(_lockObject);
                }
            }
        }

        /// <summary>
        /// Capture current frame without waiting for screen update
        /// </summary>
        private Bitmap CaptureCurrentFrame()
        {
            // When timeout occurs, we can still capture by acquiring with 0ms timeout
            // This gets the last frame even if screen hasn't changed
            SharpDX.DXGI.Resource screenResource = null;
            OutputDuplicateFrameInformation frameInfo;
            
            try
            {
                var result = _outputDuplication.TryAcquireNextFrame(0, out frameInfo, out screenResource);
                
                if (result.Success && screenResource != null)
                {
                    using var texture2D = screenResource.QueryInterface<Texture2D>();
                    return TextureToBitmap(texture2D);
                }
            }
            catch (Exception)
            {
                // Ignore errors in fallback capture
            }
            finally
            {
                try
                {
                    screenResource?.Dispose();
                    if (_outputDuplication != null)
                    {
                        _outputDuplication.ReleaseFrame();
                    }
                }
                catch (Exception)
                {
                    // Ignore
                }
            }
            
            return null;
        }

        /// <summary>
        /// Convert DirectX Texture to Bitmap (Optimized with texture pooling)
        /// </summary>
        private Bitmap TextureToBitmap(Texture2D texture2D)
        {
            var textureDesc = texture2D.Description;

            // Check if we need to recreate staging texture (size changed)
            if (_stagingTexture == null || _lastWidth != textureDesc.Width || _lastHeight != textureDesc.Height)
            {
                _stagingTexture?.Dispose();
                
                var stagingTextureDesc = new Texture2DDescription
                {
                    Width = textureDesc.Width,
                    Height = textureDesc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = textureDesc.Format,
                    Usage = ResourceUsage.Staging,
                    SampleDescription = new SampleDescription(1, 0),
                    BindFlags = BindFlags.None,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    OptionFlags = ResourceOptionFlags.None
                };

                _stagingTexture = new Texture2D(_device, stagingTextureDesc);
                _lastWidth = textureDesc.Width;
                _lastHeight = textureDesc.Height;
            }
            
            // Copy texture to staging (reuse existing staging texture)
            _device.ImmediateContext.CopyResource(texture2D, _stagingTexture);

            // Map the staging texture to read pixels
            var dataBox = _device.ImmediateContext.MapSubresource(
                _stagingTexture, 
                0, 
                MapMode.Read, 
                MapFlags.None);

            try
            {
                // Create bitmap from texture data
                var bitmap = new Bitmap(textureDesc.Width, textureDesc.Height, PixelFormat.Format32bppArgb);
                
                var bitmapData = bitmap.LockBits(
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    ImageLockMode.WriteOnly,
                    bitmap.PixelFormat);

                try
                {
                    // Optimized memory copy - use bulk copy when strides match
                    var sourcePtr = dataBox.DataPointer;
                    var destPtr = bitmapData.Scan0;
                    int copyWidth = Math.Min(bitmapData.Stride, dataBox.RowPitch);
                    
                    // Fast path: if strides match perfectly, copy everything at once
                    if (bitmapData.Stride == dataBox.RowPitch)
                    {
                        Utilities.CopyMemory(destPtr, sourcePtr, textureDesc.Height * bitmapData.Stride);
                    }
                    else
                    {
                        // Slower path: copy line by line
                        for (int y = 0; y < textureDesc.Height; y++)
                        {
                            Utilities.CopyMemory(
                                destPtr + y * bitmapData.Stride,
                                sourcePtr + y * dataBox.RowPitch,
                                copyWidth);
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                return bitmap;
            }
            finally
            {
                _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
            }
        }

        /// <summary>
        /// Capture screen and return as byte array with compression
        /// </summary>
        public async Task<byte[]> CaptureScreenToBytes(int screenIndex, int maxFileSizeKB = 150)
        {
            DebugLog($"Capturing screen index: {screenIndex} using Desktop Duplication API, max size: {maxFileSizeKB}KB");

            try
            {
                // Ensure we're on the input desktop before capturing
                // Critical for capturing login screen, UAC prompts, etc.
                Helper.SessionManager.TrySwitchToInputDesktop();
                
                // Simple rate limiting to prevent excessive CPU usage
                var now = DateTime.Now;
                var timeSinceLastCapture = (now - _lastCaptureTime).TotalMilliseconds;
                if (timeSinceLastCapture < MIN_FRAME_INTERVAL_MS && _lastCompressedFrame != null)
                {
                    DebugLog($"Rate limited: Only {timeSinceLastCapture:F0}ms since last capture");
                    return _lastCompressedFrame;
                }
                _lastCaptureTime = now;

                // Ensure initialized
                if (!_initialized)
                {
                    if (!Initialize())
                    {
                        Console.WriteLine("Failed to initialize Desktop Duplication");
                        return null;
                    }
                }

                // Capture frame - the Desktop Duplication API already handles change detection
                // TryAcquireNextFrame will timeout if nothing changed, and CaptureCurrentFrame
                // will return the last frame in that case
                using var bitmap = CaptureFrame();
                
                if (bitmap == null)
                {
                    DebugLog("Failed to capture frame, reusing last frame if available");
                    return _lastCompressedFrame;
                }

                DebugLog($"Captured frame: {bitmap.Width}x{bitmap.Height}");

                // Initialize scaling info
                var scalingInfo = new ScalingInfo 
                { 
                    ScaleX = 1.0, 
                    ScaleY = 1.0,
                    OriginalWidth = bitmap.Width,
                    OriginalHeight = bitmap.Height,
                    ScaledWidth = bitmap.Width,
                    ScaledHeight = bitmap.Height
                };

                // Process the image for network transmission
                byte[] bytesResult = await Task.Run(() => ProcessImageForTransmission(bitmap, scalingInfo, screenIndex, maxFileSizeKB));
                
                if (bytesResult == null || bytesResult.Length == 0)
                {
                    Console.WriteLine("Image processing failed, couldn't meet size requirements");
                    return _lastCompressedFrame; // Return last successful frame
                }
                
                DebugLog($"Processed image size: {bytesResult.Length / 1024}KB");
                
                // Cache the compressed frame
                _lastCompressedFrame = bytesResult;
                
                return bytesResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error capturing screen: {ex.Message}");
                return _lastCompressedFrame; // Return cached frame on error
            }
        }


        /// <summary>
        /// Process image for network transmission with compression
        /// </summary>
        private static byte[] ProcessImageForTransmission(Bitmap original, ScalingInfo scalingInfo, int screenIndex, int maxFileSizeKB)
        {
            Bitmap processedImage = null;
            bool needsDispose = false;
            
            try
            {
                int targetWidth = original.Width;
                int targetHeight = original.Height;
                
                // More aggressive downscaling for large screens
                if (original.Width > MAX_IMAGE_DIMENSION || original.Height > MAX_IMAGE_DIMENSION)
                {
                    double scale = Math.Min(
                        (double)MAX_IMAGE_DIMENSION / original.Width,
                        (double)MAX_IMAGE_DIMENSION / original.Height);
                    
                    targetWidth = (int)(original.Width * scale);
                    targetHeight = (int)(original.Height * scale);
                    
                    // Update scaling info
                    scalingInfo.ScaleX = (double)targetWidth / original.Width;
                    scalingInfo.ScaleY = (double)targetHeight / original.Height;
                    scalingInfo.ScaledWidth = targetWidth;
                    scalingInfo.ScaledHeight = targetHeight;
                    
                    // Store scaling info for coordinate translation
                    lock (ScalingLock)
                    {
                        ScalingFactors[screenIndex] = scalingInfo;
                    }
                    
                    DebugLog($"Downscaling to: {targetWidth}x{targetHeight}, scale factors: X={scalingInfo.ScaleX:F2}, Y={scalingInfo.ScaleY:F2}");

                    // Resize the image
                    processedImage = ResizeBitmap(original, targetWidth, targetHeight);
                    needsDispose = true;
                }
                else
                {
                    // Use the original image
                    processedImage = original;

                    // Update cache with 1:1 mapping
                    lock (ScalingLock)
                    {
                        ScalingFactors[screenIndex] = scalingInfo;
                    }
                }

                // Start with adaptive compression based on image size
                int initialQuality = CalculateInitialQuality(processedImage.Width, processedImage.Height);
                
                // Try to compress the image
                byte[] compressedBytes = CompressImageToTargetSize(processedImage, maxFileSizeKB, initialQuality);

                if (compressedBytes == null)
                {
                    Console.WriteLine("Initial compression failed, trying progressive fallback...");
                    compressedBytes = FallbackCompression(processedImage, maxFileSizeKB);
                }

                if (compressedBytes == null)
                {
                    Console.WriteLine("All compression attempts failed");
                    return null;
                }

                return compressedBytes;
            }
            finally
            {
                if (needsDispose && processedImage != null && processedImage != original)
                {
                    processedImage.Dispose();
                }
            }
        }

        /// <summary>
        /// Calculate initial quality based on image dimensions
        /// </summary>
        private static int CalculateInitialQuality(int width, int height)
        {
            int pixelCount = width * height;

            if (pixelCount > 1920 * 1080)
                return 40;
            else if (pixelCount > 1280 * 720)
                return 50;
            else
                return DEFAULT_QUALITY;
        }

        /// <summary>
        /// Progressive fallback for challenging compression cases
        /// </summary>
        private static byte[] FallbackCompression(Bitmap image, int maxFileSizeKB)
        {
            byte[] result = CompressWithQuality(image, 15);
            if (result != null && result.Length <= maxFileSizeKB * 1024)
                return result;

            using (var halfSized = ResizeBitmap(image, image.Width / 2, image.Height / 2))
            {
                result = CompressWithQuality(halfSized, 40);
                if (result != null && result.Length <= maxFileSizeKB * 1024)
                    return result;

                result = CompressWithQuality(halfSized, 15);
                if (result != null && result.Length <= maxFileSizeKB * 1024)
                    return result;
            }

            return null;
        }

        /// <summary>
        /// Compress image with specific quality using pooled resources
        /// </summary>
        private static byte[] CompressWithQuality(Bitmap image, int quality)
        {
            try
            {
                var ms = GetPooledMemoryStream();
                var encoderParams = GetPooledEncoderParams(quality);
                image.Save(ms, JpegEncoder, encoderParams);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error compressing image: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Cached JPEG encoder
        /// </summary>
        private static readonly Lazy<ImageCodecInfo> LazyJpegEncoder = new Lazy<ImageCodecInfo>(() => 
            ImageCodecInfo.GetImageDecoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid));
        
        private static ImageCodecInfo JpegEncoder => LazyJpegEncoder.Value;


        /// <summary>
        /// Compress image to target size with binary search (optimized)
        /// </summary>
        private static byte[] CompressImageToTargetSize(Bitmap image, int maxFileSizeKB, int initialQuality = DEFAULT_QUALITY)
        {
            try
            {
                int targetSize = maxFileSizeKB * 1024;
                int minQ = 10;
                int maxQ = 90;
                int initialQ = Math.Min(Math.Max(initialQuality, minQ), maxQ);

                byte[] bestBytes = null;
                int attempts = 0;

                // First try with initial quality using pooled resources
                var ms = GetPooledMemoryStream();
                var encoderParams = GetPooledEncoderParams(initialQ);

                try
                {
                    image.Save(ms, JpegEncoder, encoderParams);
                    var bytes = ms.ToArray();

                    if (bytes.Length <= targetSize)
                    {
                        bestBytes = bytes;
                        minQ = initialQ;
                    }
                    else
                    {
                        maxQ = initialQ;
                    }
                }
                catch (Exception ex)
                {
                    DebugLog($"Initial compression failed: {ex.Message}");
                }

                int lastMidQ = -1;
                while (minQ <= maxQ && attempts < MAX_COMPRESSION_ATTEMPTS)
                {
                    attempts++;
                    int midQ = (minQ + maxQ) / 2;

                    if (midQ == lastMidQ)
                        break;

                    lastMidQ = midQ;

                    // Reuse pooled resources
                    ms = GetPooledMemoryStream();
                    encoderParams = GetPooledEncoderParams(midQ);

                    try
                    {
                        image.Save(ms, JpegEncoder, encoderParams);
                        var currentBytes = ms.ToArray();

                        DebugLog($"Compression attempt {attempts}: Quality {midQ}, Size: {currentBytes.Length / 1024}KB (Limit: {maxFileSizeKB}KB)");

                        if (currentBytes.Length <= targetSize)
                        {
                            bestBytes = currentBytes;
                            minQ = midQ + 1;
                        }
                        else
                        {
                            maxQ = midQ - 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Compression error at quality {midQ}: {ex.Message}");
                        maxQ = midQ - 1;
                    }
                }
                return bestBytes;

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        /// <summary>
        /// Resize bitmap with high quality (optimized for speed)
        /// </summary>
        private static Bitmap ResizeBitmap(Bitmap source, int maxWidth, int maxHeight)
        {
            if (source == null) return null;
            
            double ratioX = (double)maxWidth / source.Width;
            double ratioY = (double)maxHeight / source.Height;
            double ratio = Math.Min(ratioX, ratioY);

            int newWidth = Math.Max(1, (int)(source.Width * ratio));
            int newHeight = Math.Max(1, (int)(source.Height * ratio));

            // Use 24bpp for smaller file size (no alpha channel needed for screenshots)
            var dest = new Bitmap(newWidth, newHeight, PixelFormat.Format24bppRgb);

            try
            {
                using var g = Graphics.FromImage(dest);
                
                // Optimized settings for speed
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low; // Faster than Bilinear
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed; // Changed from None
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;

                // Disable everything that slows down rendering
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixel;

                var destRect = new Rectangle(0, 0, newWidth, newHeight);
                var srcRect = new Rectangle(0, 0, source.Width, source.Height);
                g.DrawImage(source, destRect, srcRect, GraphicsUnit.Pixel);

                return dest;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error resizing bitmap: {ex.Message}");
                dest.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Get scaling information for coordinate translation
        /// </summary>
        public static ScalingInfo GetScalingInfo(int screenIndex)
        {
            lock (ScalingLock)
            {
                if (ScalingFactors.TryGetValue(screenIndex, out ScalingInfo info))
                {
                    return info;
                }
                
                return new ScalingInfo { ScaleX = 1.0, ScaleY = 1.0 };
            }
        }

        /// <summary>
        /// Convert web console coordinates to actual screen coordinates
        /// </summary>
        public static (int x, int y) TranslateCoordinates(int screenIndex, int x, int y)
        {
            var scalingInfo = GetScalingInfo(screenIndex);
            
            int actualX = (int)Math.Round(x / scalingInfo.ScaleX);
            int actualY = (int)Math.Round(y / scalingInfo.ScaleY);
            
            return (actualX, actualY);
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        private void Cleanup()
        {
            lock (_lockObject)
            {
                _stagingTexture?.Dispose();
                _stagingTexture = null;
                
                _screenTexture?.Dispose();
                _screenTexture = null;
                
                _outputDuplication?.Dispose();
                _outputDuplication = null;
                
                _output1?.Dispose();
                _output1 = null;
                
                _device?.Dispose();
                _device = null;
                
                _initialized = false;
            }
        }

        public void Dispose()
        {
            Cleanup();
        }
    }
}


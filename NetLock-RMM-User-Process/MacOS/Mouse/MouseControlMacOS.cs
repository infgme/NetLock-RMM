using System;
using System.Threading.Tasks;
using NetLock_RMM_User_Process.MacOS.Helper;
using static NetLock_RMM_User_Process.MacOS.Helper.CoreGraphics;

namespace NetLock_RMM_User_Process.MacOS.Mouse
{
    /// <summary>
    /// macOS mouse control implementation using CoreGraphics CGEvent API
    /// </summary>
    internal static class MouseControlMacOS
    {
        private static IntPtr _eventSource = IntPtr.Zero;
        private static readonly object _lockObject = new object();
        
        // Track current position for relative movements
        private static double _currentX = 0;
        private static double _currentY = 0;
        private static bool _positionInitialized = false;
        
        // Double-click tracking
        private static DateTime _lastClickTime = DateTime.MinValue;
        private static int _clickCount = 1;
        private const int DOUBLE_CLICK_INTERVAL_MS = 500;

        // Scaling info cache
        private static int _screenWidth = 0;
        private static int _screenHeight = 0;
        private static int _screenOffsetX = 0;
        private static int _screenOffsetY = 0;

        /// <summary>
        /// Initialize the event source
        /// </summary>
        private static IntPtr GetEventSource()
        {
            if (_eventSource == IntPtr.Zero)
            {
                _eventSource = CGEventSourceCreate(CGEventSourceStateID.CombinedSession);
                if (_eventSource == IntPtr.Zero)
                {
                    Console.WriteLine("[MacOS Mouse] Failed to create CGEventSource, using null source");
                }
            }
            return _eventSource;
        }

        /// <summary>
        /// Set scaling information for coordinate translation
        /// </summary>
        public static void SetScalingInfo(int screenIndex, int width, int height, int offsetX, int offsetY)
        {
            _screenWidth = width;
            _screenHeight = height;
            _screenOffsetX = offsetX;
            _screenOffsetY = offsetY;
        }

        /// <summary>
        /// Translate coordinates from remote client to local screen
        /// </summary>
        private static (double x, double y) TranslateCoordinates(int x, int y, int screenIndex)
        {
            // Get display info for this screen
            var (displayId, offsetX, offsetY, width, height) = CoreGraphics.GetDisplayInfo(screenIndex);
            
            // For now, use direct coordinates (the remote client should already send scaled coordinates)
            // Add screen offset for multi-monitor setups
            double translatedX = x + offsetX;
            double translatedY = y + offsetY;
            
            return (translatedX, translatedY);
        }

        /// <summary>
        /// Move mouse to absolute position (with lock for click operations)
        /// </summary>
        public static Task MoveMouse(int x, int y, int screenIndex = 0)
        {
            lock (_lockObject)
            {
                return MoveMouseInternal(x, y, screenIndex);
            }
        }

        /// <summary>
        /// Move mouse to absolute position (without lock for continuous tracking)
        /// </summary>
        public static Task MoveMouseNoLock(int x, int y, int screenIndex = 0)
        {
            return MoveMouseInternal(x, y, screenIndex);
        }

        private static Task MoveMouseInternal(int x, int y, int screenIndex)
        {
            try
            {
                var (translatedX, translatedY) = TranslateCoordinates(x, y, screenIndex);
                
                CGPoint point = new CGPoint(translatedX, translatedY);
                IntPtr source = GetEventSource();
                
                IntPtr eventRef = CGEventCreateMouseEvent(
                    source,
                    CGEventType.MouseMoved,
                    point,
                    CGMouseButton.Left);

                if (eventRef != IntPtr.Zero)
                {
                    CGEventPost(CGEventTapLocation.HID, eventRef);
                    CFRelease(eventRef);
                    
                    _currentX = translatedX;
                    _currentY = translatedY;
                    _positionInitialized = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Mouse] Move error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Perform left mouse click
        /// </summary>
        public static Task LeftClickMouse()
        {
            return ClickMouse(CGMouseButton.Left, CGEventType.LeftMouseDown, CGEventType.LeftMouseUp);
        }

        /// <summary>
        /// Perform right mouse click
        /// </summary>
        public static Task RightClickMouse()
        {
            return ClickMouse(CGMouseButton.Right, CGEventType.RightMouseDown, CGEventType.RightMouseUp);
        }

        /// <summary>
        /// Perform middle mouse click
        /// </summary>
        public static Task MiddleClickMouse()
        {
            return ClickMouse(CGMouseButton.Center, CGEventType.OtherMouseDown, CGEventType.OtherMouseUp);
        }

        /// <summary>
        /// Perform double-click
        /// </summary>
        public static async Task DoubleClickMouse()
        {
            try
            {
                CGPoint point = GetMouseLocation();
                IntPtr source = GetEventSource();

                // First click
                IntPtr downEvent = CGEventCreateMouseEvent(source, CGEventType.LeftMouseDown, point, CGMouseButton.Left);
                IntPtr upEvent = CGEventCreateMouseEvent(source, CGEventType.LeftMouseUp, point, CGMouseButton.Left);

                if (downEvent != IntPtr.Zero && upEvent != IntPtr.Zero)
                {
                    CGEventSetIntegerValueField(downEvent, CGEventField.MouseEventClickState, 1);
                    CGEventSetIntegerValueField(upEvent, CGEventField.MouseEventClickState, 1);
                    
                    CGEventPost(CGEventTapLocation.HID, downEvent);
                    CGEventPost(CGEventTapLocation.HID, upEvent);
                    
                    CFRelease(downEvent);
                    CFRelease(upEvent);
                }

                await Task.Delay(10);

                // Second click with clickState = 2 for double-click
                downEvent = CGEventCreateMouseEvent(source, CGEventType.LeftMouseDown, point, CGMouseButton.Left);
                upEvent = CGEventCreateMouseEvent(source, CGEventType.LeftMouseUp, point, CGMouseButton.Left);

                if (downEvent != IntPtr.Zero && upEvent != IntPtr.Zero)
                {
                    CGEventSetIntegerValueField(downEvent, CGEventField.MouseEventClickState, 2);
                    CGEventSetIntegerValueField(upEvent, CGEventField.MouseEventClickState, 2);
                    
                    CGEventPost(CGEventTapLocation.HID, downEvent);
                    CGEventPost(CGEventTapLocation.HID, upEvent);
                    
                    CFRelease(downEvent);
                    CFRelease(upEvent);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Mouse] Double-click error: {ex.Message}");
            }
        }

        private static Task ClickMouse(CGMouseButton button, CGEventType downType, CGEventType upType)
        {
            try
            {
                CGPoint point = GetMouseLocation();
                IntPtr source = GetEventSource();

                // Track click count for multi-click detection
                var now = DateTime.Now;
                if ((now - _lastClickTime).TotalMilliseconds <= DOUBLE_CLICK_INTERVAL_MS)
                {
                    _clickCount++;
                }
                else
                {
                    _clickCount = 1;
                }
                _lastClickTime = now;

                IntPtr downEvent = CGEventCreateMouseEvent(source, downType, point, button);
                IntPtr upEvent = CGEventCreateMouseEvent(source, upType, point, button);

                if (downEvent != IntPtr.Zero && upEvent != IntPtr.Zero)
                {
                    // Set click count for proper double/triple click detection
                    CGEventSetIntegerValueField(downEvent, CGEventField.MouseEventClickState, _clickCount);
                    CGEventSetIntegerValueField(upEvent, CGEventField.MouseEventClickState, _clickCount);

                    CGEventPost(CGEventTapLocation.HID, downEvent);
                    CGEventPost(CGEventTapLocation.HID, upEvent);

                    CFRelease(downEvent);
                    CFRelease(upEvent);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Mouse] Click error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Press left mouse button down
        /// </summary>
        public static Task LeftMouseDown()
        {
            return MouseButton(CGMouseButton.Left, CGEventType.LeftMouseDown, true);
        }

        /// <summary>
        /// Release left mouse button
        /// </summary>
        public static Task LeftMouseUp()
        {
            return MouseButton(CGMouseButton.Left, CGEventType.LeftMouseUp, false);
        }

        /// <summary>
        /// Press right mouse button down
        /// </summary>
        public static Task RightMouseDown()
        {
            return MouseButton(CGMouseButton.Right, CGEventType.RightMouseDown, true);
        }

        /// <summary>
        /// Release right mouse button
        /// </summary>
        public static Task RightMouseUp()
        {
            return MouseButton(CGMouseButton.Right, CGEventType.RightMouseUp, false);
        }

        private static Task MouseButton(CGMouseButton button, CGEventType eventType, bool isDown)
        {
            try
            {
                CGPoint point = GetMouseLocation();
                IntPtr source = GetEventSource();

                IntPtr eventRef = CGEventCreateMouseEvent(source, eventType, point, button);

                if (eventRef != IntPtr.Zero)
                {
                    CGEventPost(CGEventTapLocation.HID, eventRef);
                    CFRelease(eventRef);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Mouse] Button error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Scroll the mouse wheel
        /// </summary>
        public static Task Scroll(int deltaX, int deltaY)
        {
            try
            {
                IntPtr source = GetEventSource();
                
                // macOS uses inverted Y direction for natural scrolling
                IntPtr eventRef = CGEventCreateScrollWheelEvent(
                    source,
                    CGScrollEventUnit.Line,
                    2,  // wheelCount
                    -deltaY,  // vertical (inverted for natural scroll)
                    deltaX);  // horizontal

                if (eventRef != IntPtr.Zero)
                {
                    CGEventPost(CGEventTapLocation.HID, eventRef);
                    CFRelease(eventRef);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Mouse] Scroll error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public static void Cleanup()
        {
            if (_eventSource != IntPtr.Zero)
            {
                try
                {
                    CFRelease(_eventSource);
                }
                catch { }
                _eventSource = IntPtr.Zero;
            }
        }
    }
}


using System;
using System.Runtime.InteropServices;

namespace NetLock_RMM_User_Process.MacOS.Helper
{
    /// <summary>
    /// P/Invoke bindings for macOS CoreGraphics framework
    /// Used for mouse and keyboard input simulation
    /// </summary>
    internal static class CoreGraphics
    {
        private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string ApplicationServicesLib = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

        #region CGEvent Types
        
        public enum CGEventType : uint
        {
            Null = 0,
            LeftMouseDown = 1,
            LeftMouseUp = 2,
            RightMouseDown = 3,
            RightMouseUp = 4,
            MouseMoved = 5,
            LeftMouseDragged = 6,
            RightMouseDragged = 7,
            KeyDown = 10,
            KeyUp = 11,
            FlagsChanged = 12,
            ScrollWheel = 22,
            OtherMouseDown = 25,
            OtherMouseUp = 26,
            OtherMouseDragged = 27
        }

        public enum CGMouseButton : uint
        {
            Left = 0,
            Right = 1,
            Center = 2
        }

        public enum CGEventTapLocation : uint
        {
            HID = 0,
            Session = 1,
            AnnotatedSession = 2
        }

        public enum CGEventSourceStateID : int
        {
            Private = -1,
            CombinedSession = 0,
            HidSystem = 1
        }

        public enum CGScrollEventUnit : uint
        {
            Pixel = 0,
            Line = 1
        }

        [Flags]
        public enum CGEventFlags : ulong
        {
            None = 0,
            AlphaShift = 0x00010000,  // Caps Lock
            Shift = 0x00020000,
            Control = 0x00040000,
            Alternate = 0x00080000,   // Option/Alt
            Command = 0x00100000,     // Command/Meta
            NumericPad = 0x00200000,
            Help = 0x00400000,
            SecondaryFn = 0x00800000
        }

        public enum CGEventField : uint
        {
            MouseEventNumber = 0,
            MouseEventClickState = 1,
            MouseEventPressure = 2,
            MouseEventButtonNumber = 3,
            MouseEventDeltaX = 4,
            MouseEventDeltaY = 5,
            MouseEventInstantMouser = 6,
            MouseEventSubtype = 7,
            KeyboardEventAutorepeat = 8,
            KeyboardEventKeycode = 9,
            KeyboardEventKeyboardType = 10,
            ScrollWheelEventDeltaAxis1 = 11,
            ScrollWheelEventDeltaAxis2 = 12,
            ScrollWheelEventDeltaAxis3 = 13,
            ScrollWheelEventFixedPtDeltaAxis1 = 93,
            ScrollWheelEventFixedPtDeltaAxis2 = 94,
            ScrollWheelEventFixedPtDeltaAxis3 = 95,
            ScrollWheelEventPointDeltaAxis1 = 96,
            ScrollWheelEventPointDeltaAxis2 = 97,
            ScrollWheelEventPointDeltaAxis3 = 98,
            ScrollWheelEventScrollPhase = 99,
            ScrollWheelEventScrollCount = 100,
            ScrollWheelEventMomentumPhase = 123,
            EventSourceUserData = 42
        }

        #endregion

        #region CGPoint Structure

        [StructLayout(LayoutKind.Sequential)]
        public struct CGPoint
        {
            public double X;
            public double Y;

            public CGPoint(double x, double y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CGSize
        {
            public double Width;
            public double Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CGRect
        {
            public CGPoint Origin;
            public CGSize Size;
        }

        #endregion

        #region CGEvent Functions

        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGEventSourceCreate(CGEventSourceStateID stateID);

        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGEventCreateMouseEvent(
            IntPtr source,
            CGEventType mouseType,
            CGPoint mouseCursorPosition,
            CGMouseButton mouseButton);

        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGEventCreateKeyboardEvent(
            IntPtr source,
            ushort virtualKey,
            bool keyDown);

        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGEventCreateScrollWheelEvent(
            IntPtr source,
            CGScrollEventUnit units,
            uint wheelCount,
            int wheel1,
            int wheel2);

        [DllImport(CoreGraphicsLib)]
        public static extern void CGEventPost(CGEventTapLocation tap, IntPtr eventRef);

        [DllImport(CoreGraphicsLib)]
        public static extern void CGEventSetFlags(IntPtr eventRef, CGEventFlags flags);

        [DllImport(CoreGraphicsLib)]
        public static extern CGEventFlags CGEventGetFlags(IntPtr eventRef);

        [DllImport(CoreGraphicsLib)]
        public static extern void CGEventSetIntegerValueField(IntPtr eventRef, CGEventField field, long value);

        [DllImport(CoreGraphicsLib)]
        public static extern long CGEventGetIntegerValueField(IntPtr eventRef, CGEventField field);

        [DllImport(CoreGraphicsLib)]
        public static extern void CGEventSetDoubleValueField(IntPtr eventRef, CGEventField field, double value);

        [DllImport(CoreGraphicsLib)]
        public static extern void CGEventSetType(IntPtr eventRef, CGEventType type);

        #endregion

        #region CGDisplay Functions

        [DllImport(CoreGraphicsLib)]
        public static extern uint CGMainDisplayID();

        [DllImport(CoreGraphicsLib)]
        public static extern ulong CGDisplayPixelsWide(uint display);

        [DllImport(CoreGraphicsLib)]
        public static extern ulong CGDisplayPixelsHigh(uint display);

        [DllImport(CoreGraphicsLib)]
        public static extern CGRect CGDisplayBounds(uint display);

        [DllImport(CoreGraphicsLib)]
        public static extern int CGGetOnlineDisplayList(uint maxDisplays, uint[] displays, out uint displayCount);

        [DllImport(CoreGraphicsLib)]
        public static extern int CGGetActiveDisplayList(uint maxDisplays, uint[] displays, out uint displayCount);

        #endregion

        #region Mouse Location

        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGEventCreate(IntPtr source);

        [DllImport(CoreGraphicsLib)]
        public static extern CGPoint CGEventGetLocation(IntPtr eventRef);

        #endregion

        #region CoreFoundation Memory Management

        [DllImport(CoreFoundationLib)]
        public static extern void CFRelease(IntPtr cf);

        [DllImport(CoreFoundationLib)]
        public static extern IntPtr CFRetain(IntPtr cf);

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the current mouse cursor position
        /// </summary>
        public static CGPoint GetMouseLocation()
        {
            IntPtr eventRef = CGEventCreate(IntPtr.Zero);
            if (eventRef == IntPtr.Zero)
                return new CGPoint(0, 0);

            try
            {
                return CGEventGetLocation(eventRef);
            }
            finally
            {
                CFRelease(eventRef);
            }
        }

        /// <summary>
        /// Gets the main display dimensions
        /// </summary>
        public static (int width, int height) GetMainDisplaySize()
        {
            uint mainDisplay = CGMainDisplayID();
            return ((int)CGDisplayPixelsWide(mainDisplay), (int)CGDisplayPixelsHigh(mainDisplay));
        }

        /// <summary>
        /// Gets the number of active displays
        /// </summary>
        public static int GetDisplayCount()
        {
            uint displayCount;
            CGGetActiveDisplayList(0, null, out displayCount);
            return (int)displayCount;
        }

        /// <summary>
        /// Gets display info for a specific screen index
        /// </summary>
        public static (uint displayId, int x, int y, int width, int height) GetDisplayInfo(int screenIndex)
        {
            uint displayCount;
            CGGetActiveDisplayList(0, null, out displayCount);
            
            if (displayCount == 0)
                return (CGMainDisplayID(), 0, 0, 1920, 1080);

            uint[] displays = new uint[displayCount];
            CGGetActiveDisplayList(displayCount, displays, out displayCount);

            if (screenIndex >= displayCount)
                screenIndex = 0;

            uint displayId = displays[screenIndex];
            CGRect bounds = CGDisplayBounds(displayId);

            return (displayId, (int)bounds.Origin.X, (int)bounds.Origin.Y, 
                    (int)bounds.Size.Width, (int)bounds.Size.Height);
        }

        #endregion
    }
}


using System;
using System.Runtime.InteropServices;

namespace NetLock_RMM_User_Process.Linux.Helper
{
    /// <summary>
    /// P/Invoke bindings for libXtst - the X Test Extension library
    /// Used for simulating keyboard and mouse input on X11
    /// </summary>
    internal static class LibXtst
    {
        private const string LibXtstName = "libXtst.so.6";
        private const string LibX11Name = "libX11.so.6";

        // XTest functions for input simulation
        [DllImport(LibXtstName, EntryPoint = "XTestFakeKeyEvent")]
        public static extern int XTestFakeKeyEvent(
            IntPtr display,
            uint keycode,
            bool isPress,
            ulong delay);

        [DllImport(LibXtstName, EntryPoint = "XTestFakeButtonEvent")]
        public static extern int XTestFakeButtonEvent(
            IntPtr display,
            uint button,
            bool isPress,
            ulong delay);

        [DllImport(LibXtstName, EntryPoint = "XTestFakeMotionEvent")]
        public static extern int XTestFakeMotionEvent(
            IntPtr display,
            int screenNumber,
            int x, int y,
            ulong delay);

        [DllImport(LibXtstName, EntryPoint = "XTestFakeRelativeMotionEvent")]
        public static extern int XTestFakeRelativeMotionEvent(
            IntPtr display,
            int dx, int dy,
            ulong delay);

        [DllImport(LibXtstName, EntryPoint = "XTestQueryExtension")]
        public static extern bool XTestQueryExtension(
            IntPtr display,
            out int eventBase,
            out int errorBase,
            out int majorVersion,
            out int minorVersion);

        [DllImport(LibXtstName, EntryPoint = "XTestGrabControl")]
        public static extern int XTestGrabControl(IntPtr display, bool impervious);

        // Keysym to keycode conversion
        [DllImport(LibX11Name, EntryPoint = "XKeysymToKeycode")]
        public static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);

        [DllImport(LibX11Name, EntryPoint = "XStringToKeysym")]
        public static extern ulong XStringToKeysym(string str);

        // Mouse button constants
        public const uint Button1 = 1; // Left
        public const uint Button2 = 2; // Middle
        public const uint Button3 = 3; // Right
        public const uint Button4 = 4; // Scroll up
        public const uint Button5 = 5; // Scroll down
        public const uint Button6 = 6; // Scroll left
        public const uint Button7 = 7; // Scroll right

        // Common keysyms (from X11/keysymdef.h)
        public static class KeySym
        {
            // Modifiers
            public const ulong Shift_L = 0xffe1;
            public const ulong Shift_R = 0xffe2;
            public const ulong Control_L = 0xffe3;
            public const ulong Control_R = 0xffe4;
            public const ulong Alt_L = 0xffe9;
            public const ulong Alt_R = 0xffea;
            public const ulong Super_L = 0xffeb;
            public const ulong Super_R = 0xffec;

            // Function keys
            public const ulong F1 = 0xffbe;
            public const ulong F2 = 0xffbf;
            public const ulong F3 = 0xffc0;
            public const ulong F4 = 0xffc1;
            public const ulong F5 = 0xffc2;
            public const ulong F6 = 0xffc3;
            public const ulong F7 = 0xffc4;
            public const ulong F8 = 0xffc5;
            public const ulong F9 = 0xffc6;
            public const ulong F10 = 0xffc7;
            public const ulong F11 = 0xffc8;
            public const ulong F12 = 0xffc9;

            // Special keys
            public const ulong BackSpace = 0xff08;
            public const ulong Tab = 0xff09;
            public const ulong Return = 0xff0d;
            public const ulong Escape = 0xff1b;
            public const ulong Delete = 0xffff;
            public const ulong Home = 0xff50;
            public const ulong Left = 0xff51;
            public const ulong Up = 0xff52;
            public const ulong Right = 0xff53;
            public const ulong Down = 0xff54;
            public const ulong Page_Up = 0xff55;
            public const ulong Page_Down = 0xff56;
            public const ulong End = 0xff57;
            public const ulong Insert = 0xff63;
            public const ulong Space = 0x0020;
            public const ulong Print = 0xff61;
            public const ulong Pause = 0xff13;
            public const ulong Scroll_Lock = 0xff14;
            public const ulong Num_Lock = 0xff7f;
            public const ulong Caps_Lock = 0xffe5;

            // Letters (lowercase)
            public const ulong a = 0x0061;
            public const ulong b = 0x0062;
            public const ulong c = 0x0063;
            public const ulong d = 0x0064;
            public const ulong e = 0x0065;
            public const ulong f = 0x0066;
            public const ulong g = 0x0067;
            public const ulong h = 0x0068;
            public const ulong i = 0x0069;
            public const ulong j = 0x006a;
            public const ulong k = 0x006b;
            public const ulong l = 0x006c;
            public const ulong m = 0x006d;
            public const ulong n = 0x006e;
            public const ulong o = 0x006f;
            public const ulong p = 0x0070;
            public const ulong q = 0x0071;
            public const ulong r = 0x0072;
            public const ulong s = 0x0073;
            public const ulong t = 0x0074;
            public const ulong u = 0x0075;
            public const ulong v = 0x0076;
            public const ulong w = 0x0077;
            public const ulong x = 0x0078;
            public const ulong y = 0x0079;
            public const ulong z = 0x007a;

            // Numbers
            public const ulong Num0 = 0x0030;
            public const ulong Num1 = 0x0031;
            public const ulong Num2 = 0x0032;
            public const ulong Num3 = 0x0033;
            public const ulong Num4 = 0x0034;
            public const ulong Num5 = 0x0035;
            public const ulong Num6 = 0x0036;
            public const ulong Num7 = 0x0037;
            public const ulong Num8 = 0x0038;
            public const ulong Num9 = 0x0039;
        }

        /// <summary>
        /// Checks if XTest extension is available
        /// </summary>
        public static bool IsXTestAvailable(IntPtr display)
        {
            try
            {
                if (display == IntPtr.Zero)
                    return false;

                return XTestQueryExtension(display, out _, out _, out _, out _);
            }
            catch
            {
                return false;
            }
        }
    }
}


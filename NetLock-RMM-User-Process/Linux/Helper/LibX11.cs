using System;
using System.Runtime.InteropServices;

namespace NetLock_RMM_User_Process.Linux.Helper
{
    /// <summary>
    /// P/Invoke bindings for libX11 - the X Window System library
    /// These are the core X11 functions needed for screen capture and display info
    /// </summary>
    internal static class LibX11
    {
        private const string LibX11Name = "libX11.so.6";
        private const string LibXcbName = "libxcb.so.1";
        private const string LibXcbShmName = "libxcb-shm.so.0";

        // X11 Display functions
        [DllImport(LibX11Name, EntryPoint = "XOpenDisplay")]
        public static extern IntPtr XOpenDisplay(string displayName);

        [DllImport(LibX11Name, EntryPoint = "XCloseDisplay")]
        public static extern int XCloseDisplay(IntPtr display);

        [DllImport(LibX11Name, EntryPoint = "XDefaultScreen")]
        public static extern int XDefaultScreen(IntPtr display);

        [DllImport(LibX11Name, EntryPoint = "XDefaultRootWindow")]
        public static extern IntPtr XDefaultRootWindow(IntPtr display);

        [DllImport(LibX11Name, EntryPoint = "XRootWindow")]
        public static extern IntPtr XRootWindow(IntPtr display, int screenNumber);

        [DllImport(LibX11Name, EntryPoint = "XScreenCount")]
        public static extern int XScreenCount(IntPtr display);

        [DllImport(LibX11Name, EntryPoint = "XDisplayWidth")]
        public static extern int XDisplayWidth(IntPtr display, int screenNumber);

        [DllImport(LibX11Name, EntryPoint = "XDisplayHeight")]
        public static extern int XDisplayHeight(IntPtr display, int screenNumber);

        [DllImport(LibX11Name, EntryPoint = "XDefaultDepth")]
        public static extern int XDefaultDepth(IntPtr display, int screenNumber);

        [DllImport(LibX11Name, EntryPoint = "XDefaultVisual")]
        public static extern IntPtr XDefaultVisual(IntPtr display, int screenNumber);

        [DllImport(LibX11Name, EntryPoint = "XDefaultColormap")]
        public static extern IntPtr XDefaultColormap(IntPtr display, int screenNumber);

        [DllImport(LibX11Name, EntryPoint = "XDefaultGC")]
        public static extern IntPtr XDefaultGC(IntPtr display, int screenNumber);

        [DllImport(LibX11Name, EntryPoint = "XFlush")]
        public static extern int XFlush(IntPtr display);

        [DllImport(LibX11Name, EntryPoint = "XSync")]
        public static extern int XSync(IntPtr display, bool discard);

        // XImage functions for screen capture
        [DllImport(LibX11Name, EntryPoint = "XGetImage")]
        public static extern IntPtr XGetImage(
            IntPtr display,
            IntPtr drawable,
            int x, int y,
            uint width, uint height,
            ulong planeMask,
            int format);

        [DllImport(LibX11Name, EntryPoint = "XDestroyImage")]
        public static extern int XDestroyImage(IntPtr image);

        [DllImport(LibX11Name, EntryPoint = "XFree")]
        public static extern int XFree(IntPtr data);

        // XImage structure - this matches the C struct
        [StructLayout(LayoutKind.Sequential)]
        public struct XImage
        {
            public int width;
            public int height;
            public int xoffset;
            public int format;
            public IntPtr data;
            public int byte_order;
            public int bitmap_unit;
            public int bitmap_bit_order;
            public int bitmap_pad;
            public int depth;
            public int bytes_per_line;
            public int bits_per_pixel;
            public ulong red_mask;
            public ulong green_mask;
            public ulong blue_mask;
            public IntPtr obdata;
            // There are function pointers after this, but we don't need them
        }

        // Image format constants
        public const int ZPixmap = 2;
        public const int XYPixmap = 1;
        public const int XYBitmap = 0;

        // AllPlanes constant for plane_mask
        public const ulong AllPlanes = ~0UL;

        /// <summary>
        /// Checks if X11 display is available
        /// </summary>
        public static bool IsX11Available()
        {
            try
            {
                IntPtr display = XOpenDisplay(null);
                if (display != IntPtr.Zero)
                {
                    XCloseDisplay(display);
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}


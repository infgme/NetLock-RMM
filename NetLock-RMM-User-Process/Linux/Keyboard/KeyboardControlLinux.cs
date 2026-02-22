using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NetLock_RMM_User_Process.Linux.Helper;
using NetLock_RMM_User_Process.Linux.ScreenControl;

namespace NetLock_RMM_User_Process.Linux.Keyboard
{
    /// <summary>
    /// Linux keyboard control implementation using X11 XTest or CLI tools for Wayland
    /// </summary>
    internal class KeyboardControlLinux
    {
        private static IntPtr _display = IntPtr.Zero;
        private static readonly object _displayLock = new object();

        // Input tool for Wayland fallback
        private static string _inputTool;

        // Key timing
        private const int KEY_DELAY_MS = 10;

        // Keysym for modifier keys
        private const ulong XK_Shift_L = 0xffe1;
        private const ulong XK_Control_L = 0xffe3;
        private const ulong XK_Alt_L = 0xffe9;
        private const ulong XK_Super_L = 0xffeb;

        /// <summary>
        /// Initializes the keyboard control
        /// </summary>
        private static bool EnsureInitialized()
        {
            lock (_displayLock)
            {
                if (DisplayServerDetector.IsX11)
                {
                    if (_display == IntPtr.Zero)
                    {
                        _display = LibX11.XOpenDisplay(null);
                        if (_display == IntPtr.Zero)
                        {
                            Console.WriteLine("[KeyboardControl] Failed to open X11 display");
                            return false;
                        }

                        if (!LibXtst.IsXTestAvailable(_display))
                        {
                            Console.WriteLine("[KeyboardControl] XTest extension not available");
                            LibX11.XCloseDisplay(_display);
                            _display = IntPtr.Zero;
                            return false;
                        }

                        Console.WriteLine("[KeyboardControl] X11 XTest initialized");
                    }
                    return true;
                }
                else if (DisplayServerDetector.IsWayland)
                {
                    // Check if PipeWire RemoteDesktop is available first
                    var pipeWire = ScreenCaptureLinux.ActivePipeWireCapture;
                    if (pipeWire != null && pipeWire.SupportsRemoteInput)
                    {
                        Console.WriteLine("[KeyboardControl] Using PipeWire RemoteDesktop for input");
                        return true;
                    }
                    
                    // Fallback to CLI tools
                    if (string.IsNullOrEmpty(_inputTool))
                    {
                        _inputTool = DisplayServerDetector.GetAvailableInputTool();
                        if (string.IsNullOrEmpty(_inputTool))
                        {
                            Console.WriteLine("[KeyboardControl] No input tool found for Wayland");
                            return false;
                        }
                        Console.WriteLine($"[KeyboardControl] Wayland initialized with tool: {_inputTool}");
                    }
                    return true;
                }
                
                return false;
            }
        }

        /// <summary>
        /// Sends a single key press
        /// </summary>
        public static async Task SendKey(byte vk, bool shift)
        {
            if (!EnsureInitialized())
            {
                Console.WriteLine("[KeyboardControl] Not initialized");
                return;
            }

            ulong keysym = VkToKeysym(vk);
            Console.WriteLine($"[KeyboardControl] SendKey: vk=0x{vk:X2}, keysym=0x{keysym:X}, shift={shift}");
            
            if (keysym == 0)
            {
                Console.WriteLine($"[KeyboardControl] Unknown VK code: {vk}");
                return;
            }

            if (DisplayServerDetector.IsX11)
            {
                SendKeyX11(keysym, shift);
            }
            else if (DisplayServerDetector.IsWayland)
            {
                SendKeyWayland(keysym, shift);
            }

            await Task.CompletedTask;
        }

        private static void SendKeyX11(ulong keysym, bool shift)
        {
            lock (_displayLock)
            {
                if (_display == IntPtr.Zero)
                    return;

                byte keycode = LibXtst.XKeysymToKeycode(_display, keysym);
                byte shiftKeycode = LibXtst.XKeysymToKeycode(_display, XK_Shift_L);

                if (keycode == 0)
                {
                    Console.WriteLine($"[KeyboardControl] No keycode for keysym: {keysym}");
                    return;
                }

                try
                {
                    if (shift && shiftKeycode != 0)
                    {
                        LibXtst.XTestFakeKeyEvent(_display, shiftKeycode, true, 0);
                    }

                    LibXtst.XTestFakeKeyEvent(_display, keycode, true, 0);
                    LibX11.XFlush(_display);
                    Thread.Sleep(KEY_DELAY_MS);
                    LibXtst.XTestFakeKeyEvent(_display, keycode, false, 0);

                    if (shift && shiftKeycode != 0)
                    {
                        LibXtst.XTestFakeKeyEvent(_display, shiftKeycode, false, 0);
                    }

                    LibX11.XFlush(_display);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[KeyboardControl] X11 key error: {ex.Message}");
                }
            }
        }

        private static void SendKeyWayland(ulong keysym, bool shift)
        {
            // Try PipeWire RemoteDesktop portal first
            var pipeWire = ScreenCaptureLinux.ActivePipeWireCapture;
            if (pipeWire != null && pipeWire.SupportsRemoteInput)
            {
                // Convert keysym to Linux keycode
                int keycode = KeysymToLinuxKeycode(keysym);
                Console.WriteLine($"[KeyboardControl] Wayland key: keysym=0x{keysym:X}, keycode={keycode}, shift={shift}");
                
                if (keycode > 0)
                {
                    if (shift)
                    {
                        pipeWire.KeyDown(42);  // Left Shift keycode
                        Thread.Sleep(10);
                    }
                    
                    pipeWire.PressKey(keycode);
                    Thread.Sleep(10);
                    
                    if (shift)
                    {
                        pipeWire.KeyUp(42);
                    }
                }
                else
                {
                    Console.WriteLine($"[KeyboardControl] Unknown keysym: 0x{keysym:X}");
                }
                return;
            }
            
            // Fallback to CLI tools
            string keyName = KeysymToName(keysym);
            if (string.IsNullOrEmpty(keyName))
                return;

            switch (_inputTool)
            {
                case "ydotool":
                    if (shift)
                        Bash.ExecuteCommand($"ydotool key shift+{keyName}");
                    else
                        Bash.ExecuteCommand($"ydotool key {keyName}");
                    break;
                case "xdotool":
                    if (shift)
                        Bash.ExecuteCommand($"xdotool key shift+{keyName}");
                    else
                        Bash.ExecuteCommand($"xdotool key {keyName}");
                    break;
                case "wtype":
                    // wtype is for text entry, not key simulation
                    break;
            }
        }
        
        /// <summary>
        /// Convert X11 keysym to Linux evdev keycode for RemoteDesktop portal
        /// </summary>
        private static int KeysymToLinuxKeycode(ulong keysym)
        {
            // Common keysyms to Linux evdev keycodes
            // See: /usr/include/linux/input-event-codes.h
            
            // Convert uppercase letters to lowercase for lookup
            // X11 keysyms: A-Z = 0x41-0x5A, a-z = 0x61-0x7A
            if (keysym >= 0x41 && keysym <= 0x5A)
            {
                keysym = keysym + 0x20; // Convert to lowercase
            }
            
            return keysym switch
            {
                // Letters (lowercase) - a=0x61, z=0x7a
                0x61 => 30,  // a - KEY_A
                0x62 => 48,  // b - KEY_B
                0x63 => 46,  // c - KEY_C
                0x64 => 32,  // d - KEY_D
                0x65 => 18,  // e - KEY_E
                0x66 => 33,  // f - KEY_F
                0x67 => 34,  // g - KEY_G
                0x68 => 35,  // h - KEY_H
                0x69 => 23,  // i - KEY_I
                0x6a => 36,  // j - KEY_J
                0x6b => 37,  // k - KEY_K
                0x6c => 38,  // l - KEY_L
                0x6d => 50,  // m - KEY_M
                0x6e => 49,  // n - KEY_N
                0x6f => 24,  // o - KEY_O
                0x70 => 25,  // p - KEY_P
                0x71 => 16,  // q - KEY_Q
                0x72 => 19,  // r - KEY_R
                0x73 => 31,  // s - KEY_S
                0x74 => 20,  // t - KEY_T
                0x75 => 22,  // u - KEY_U
                0x76 => 47,  // v - KEY_V
                0x77 => 17,  // w - KEY_W
                0x78 => 45,  // x - KEY_X
                0x79 => 21,  // y - KEY_Y
                0x7a => 44,  // z - KEY_Z
                
                // Numbers (0x30-0x39)
                0x30 => 11,  // 0 - KEY_0
                0x31 => 2,   // 1 - KEY_1
                0x32 => 3,   // 2 - KEY_2
                0x33 => 4,   // 3 - KEY_3
                0x34 => 5,   // 4 - KEY_4
                0x35 => 6,   // 5 - KEY_5
                0x36 => 7,   // 6 - KEY_6
                0x37 => 8,   // 7 - KEY_7
                0x38 => 9,   // 8 - KEY_8
                0x39 => 10,  // 9 - KEY_9
                
                // Special keys
                0xff0d => 28,   // Return/Enter - KEY_ENTER
                0xff08 => 14,   // BackSpace - KEY_BACKSPACE
                0xff09 => 15,   // Tab - KEY_TAB
                0xff1b => 1,    // Escape - KEY_ESC
                0x20   => 57,   // Space - KEY_SPACE
                0xffff => 111,  // Delete - KEY_DELETE
                
                // Arrow keys
                0xff51 => 105,  // Left - KEY_LEFT
                0xff52 => 103,  // Up - KEY_UP
                0xff53 => 106,  // Right - KEY_RIGHT
                0xff54 => 108,  // Down - KEY_DOWN
                
                // Navigation keys
                0xff50 => 102,  // Home - KEY_HOME
                0xff57 => 107,  // End - KEY_END
                0xff55 => 104,  // Page_Up - KEY_PAGEUP
                0xff56 => 109,  // Page_Down - KEY_PAGEDOWN
                0xff63 => 110,  // Insert - KEY_INSERT
                
                // Function keys (0xffbe-0xffc9)
                0xffbe => 59,   // F1 - KEY_F1
                0xffbf => 60,   // F2 - KEY_F2
                0xffc0 => 61,   // F3 - KEY_F3
                0xffc1 => 62,   // F4 - KEY_F4
                0xffc2 => 63,   // F5 - KEY_F5
                0xffc3 => 64,   // F6 - KEY_F6
                0xffc4 => 65,   // F7 - KEY_F7
                0xffc5 => 66,   // F8 - KEY_F8
                0xffc6 => 67,   // F9 - KEY_F9
                0xffc7 => 68,   // F10 - KEY_F10
                0xffc8 => 87,   // F11 - KEY_F11
                0xffc9 => 88,   // F12 - KEY_F12
                
                // Modifiers
                0xffe1 => 42,   // Shift_L - KEY_LEFTSHIFT
                0xffe2 => 54,   // Shift_R - KEY_RIGHTSHIFT
                0xffe3 => 29,   // Control_L - KEY_LEFTCTRL
                0xffe4 => 97,   // Control_R - KEY_RIGHTCTRL
                0xffe9 => 56,   // Alt_L - KEY_LEFTALT
                0xffea => 100,  // Alt_R - KEY_RIGHTALT
                0xffeb => 125,  // Super_L - KEY_LEFTMETA
                0xffec => 126,  // Super_R - KEY_RIGHTMETA
                0xffe5 => 58,   // Caps_Lock - KEY_CAPSLOCK
                
                // Punctuation and symbols
                0x2d => 12,     // minus - KEY_MINUS
                0x3d => 13,     // equal - KEY_EQUAL
                0x5b => 26,     // bracketleft - KEY_LEFTBRACE
                0x5d => 27,     // bracketright - KEY_RIGHTBRACE
                0x5c => 43,     // backslash - KEY_BACKSLASH
                0x3b => 39,     // semicolon - KEY_SEMICOLON
                0x27 => 40,     // apostrophe - KEY_APOSTROPHE
                0x60 => 41,     // grave - KEY_GRAVE
                0x2c => 51,     // comma - KEY_COMMA
                0x2e => 52,     // period - KEY_DOT
                0x2f => 53,     // slash - KEY_SLASH
                
                _ => 0
            };
        }

        /// <summary>
        /// Sends Ctrl+C
        /// </summary>
        public static void SendCtrlC()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.c);
        }

        /// <summary>
        /// Sends Ctrl+V (with optional clipboard content)
        /// </summary>
        public static void SendCtrlV(string content)
        {
            if (!string.IsNullOrEmpty(content))
            {
                // Set clipboard content using xclip or xsel
                SetClipboardText(content);
            }
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.v);
        }

        /// <summary>
        /// Sends Ctrl+X
        /// </summary>
        public static void SendCtrlX()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.x);
        }

        /// <summary>
        /// Sends Ctrl+Z
        /// </summary>
        public static void SendCtrlZ()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.z);
        }

        /// <summary>
        /// Sends Ctrl+Y
        /// </summary>
        public static void SendCtrlY()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.y);
        }

        /// <summary>
        /// Sends Ctrl+A
        /// </summary>
        public static void SendCtrlA()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.a);
        }

        /// <summary>
        /// Sends Ctrl+S
        /// </summary>
        public static void SendCtrlS()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.s);
        }

        /// <summary>
        /// Sends Ctrl+N
        /// </summary>
        public static void SendCtrlN()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.n);
        }

        /// <summary>
        /// Sends Ctrl+P
        /// </summary>
        public static void SendCtrlP()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.p);
        }

        /// <summary>
        /// Sends Ctrl+F
        /// </summary>
        public static void SendCtrlF()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.f);
        }

        /// <summary>
        /// Sends Ctrl+R
        /// </summary>
        public static void SendCtrlR()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.r);
        }

        /// <summary>
        /// Sends Ctrl+Shift+T
        /// </summary>
        public static void SendCtrlShiftT()
        {
            if (!EnsureInitialized())
                return;

            if (DisplayServerDetector.IsX11)
            {
                lock (_displayLock)
                {
                    if (_display == IntPtr.Zero)
                        return;

                    byte ctrlKeycode = LibXtst.XKeysymToKeycode(_display, XK_Control_L);
                    byte shiftKeycode = LibXtst.XKeysymToKeycode(_display, XK_Shift_L);
                    byte tKeycode = LibXtst.XKeysymToKeycode(_display, LibXtst.KeySym.t);

                    LibXtst.XTestFakeKeyEvent(_display, ctrlKeycode, true, 0);
                    LibXtst.XTestFakeKeyEvent(_display, shiftKeycode, true, 0);
                    LibXtst.XTestFakeKeyEvent(_display, tKeycode, true, 0);
                    LibX11.XFlush(_display);
                    Thread.Sleep(KEY_DELAY_MS);
                    LibXtst.XTestFakeKeyEvent(_display, tKeycode, false, 0);
                    LibXtst.XTestFakeKeyEvent(_display, shiftKeycode, false, 0);
                    LibXtst.XTestFakeKeyEvent(_display, ctrlKeycode, false, 0);
                    LibX11.XFlush(_display);
                }
            }
            else if (DisplayServerDetector.IsWayland)
            {
                var pipeWire = ScreenCaptureLinux.ActivePipeWireCapture;
                if (pipeWire != null && pipeWire.SupportsRemoteInput)
                {
                    // Ctrl=29, Shift=42, T=20
                    pipeWire.KeyCombo(new[] { 29, 42 }, 20);
                    return;
                }
                ExecuteKeyCommand("ctrl+shift+t");
            }
        }

        /// <summary>
        /// Sends Ctrl+Backspace
        /// </summary>
        public static void SendCtrlBackspace()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.BackSpace);
        }

        /// <summary>
        /// Sends Ctrl+Left Arrow
        /// </summary>
        public static void SendCtrlArrowLeft()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.Left);
        }

        /// <summary>
        /// Sends Ctrl+Right Arrow
        /// </summary>
        public static void SendCtrlArrowRight()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.Right);
        }

        /// <summary>
        /// Sends Ctrl+Up Arrow
        /// </summary>
        public static void SendCtrlArrowUp()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.Up);
        }

        /// <summary>
        /// Sends Ctrl+Down Arrow
        /// </summary>
        public static void SendCtrlArrowDown()
        {
            SendKeyCombination(XK_Control_L, LibXtst.KeySym.Down);
        }

        /// <summary>
        /// Sends Alt+F4
        /// </summary>
        public static void SendAltF4()
        {
            SendKeyCombination(XK_Alt_L, LibXtst.KeySym.F4);
        }

        /// <summary>
        /// Sends Ctrl+Alt+Delete (Linux equivalent - might vary by DE)
        /// Note: On Linux, this typically doesn't have the same effect as Windows
        /// </summary>
        public static void SendCtrlAltDelete()
        {
            if (!EnsureInitialized())
                return;

            Console.WriteLine("[KeyboardControl] Ctrl+Alt+Delete requested (limited on Linux)");

            if (DisplayServerDetector.IsX11)
            {
                lock (_displayLock)
                {
                    if (_display == IntPtr.Zero)
                        return;

                    byte ctrlKeycode = LibXtst.XKeysymToKeycode(_display, XK_Control_L);
                    byte altKeycode = LibXtst.XKeysymToKeycode(_display, XK_Alt_L);
                    byte delKeycode = LibXtst.XKeysymToKeycode(_display, LibXtst.KeySym.Delete);

                    LibXtst.XTestFakeKeyEvent(_display, ctrlKeycode, true, 0);
                    LibXtst.XTestFakeKeyEvent(_display, altKeycode, true, 0);
                    LibXtst.XTestFakeKeyEvent(_display, delKeycode, true, 0);
                    LibX11.XFlush(_display);
                    Thread.Sleep(KEY_DELAY_MS);
                    LibXtst.XTestFakeKeyEvent(_display, delKeycode, false, 0);
                    LibXtst.XTestFakeKeyEvent(_display, altKeycode, false, 0);
                    LibXtst.XTestFakeKeyEvent(_display, ctrlKeycode, false, 0);
                    LibX11.XFlush(_display);
                }
            }
            else if (DisplayServerDetector.IsWayland)
            {
                var pipeWire = ScreenCaptureLinux.ActivePipeWireCapture;
                if (pipeWire != null && pipeWire.SupportsRemoteInput)
                {
                    // Ctrl=29, Alt=56, Delete=111
                    pipeWire.KeyCombo(new[] { 29, 56 }, 111);
                    return;
                }
                ExecuteKeyCommand("ctrl+alt+Delete");
            }
        }

        private static void SendKeyCombination(ulong modifierKeysym, ulong keyKeysym)
        {
            if (!EnsureInitialized())
                return;

            if (DisplayServerDetector.IsX11)
            {
                lock (_displayLock)
                {
                    if (_display == IntPtr.Zero)
                        return;

                    byte modKeycode = LibXtst.XKeysymToKeycode(_display, modifierKeysym);
                    byte keyKeycode = LibXtst.XKeysymToKeycode(_display, keyKeysym);

                    if (modKeycode == 0 || keyKeycode == 0)
                    {
                        Console.WriteLine($"[KeyboardControl] Invalid keycodes");
                        return;
                    }

                    LibXtst.XTestFakeKeyEvent(_display, modKeycode, true, 0);
                    LibXtst.XTestFakeKeyEvent(_display, keyKeycode, true, 0);
                    LibX11.XFlush(_display);
                    Thread.Sleep(KEY_DELAY_MS);
                    LibXtst.XTestFakeKeyEvent(_display, keyKeycode, false, 0);
                    LibXtst.XTestFakeKeyEvent(_display, modKeycode, false, 0);
                    LibX11.XFlush(_display);
                }
            }
            else if (DisplayServerDetector.IsWayland)
            {
                // Try PipeWire RemoteDesktop portal first
                var pipeWire = ScreenCaptureLinux.ActivePipeWireCapture;
                if (pipeWire != null && pipeWire.SupportsRemoteInput)
                {
                    int modKeycode = KeysymToLinuxKeycode(modifierKeysym);
                    int keyKeycode = KeysymToLinuxKeycode(keyKeysym);
                    
                    Console.WriteLine($"[KeyboardControl] Shortcut: mod=0x{modifierKeysym:X}({modKeycode}), key=0x{keyKeysym:X}({keyKeycode})");
                    
                    if (modKeycode > 0 && keyKeycode > 0)
                    {
                        // Use combo command to send all keys atomically
                        pipeWire.KeyCombo(new[] { modKeycode }, keyKeycode);
                        return;
                    }
                }
                
                // Fallback to CLI tools
                string modName = modifierKeysym switch
                {
                    XK_Control_L => "ctrl",
                    XK_Alt_L => "alt",
                    XK_Shift_L => "shift",
                    XK_Super_L => "super",
                    _ => ""
                };

                string keyName = KeysymToName(keyKeysym);
                if (!string.IsNullOrEmpty(modName) && !string.IsNullOrEmpty(keyName))
                {
                    ExecuteKeyCommand($"{modName}+{keyName}");
                }
            }
        }

        private static void ExecuteKeyCommand(string keyCombo)
        {
            switch (_inputTool)
            {
                case "ydotool":
                    Bash.ExecuteCommand($"ydotool key {keyCombo}");
                    break;
                case "xdotool":
                    Bash.ExecuteCommand($"xdotool key {keyCombo}");
                    break;
            }
        }

        /// <summary>
        /// Types a string of text
        /// </summary>
        public static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (!EnsureInitialized())
                return;

            if (DisplayServerDetector.IsX11)
            {
                // Use xdotool for text entry (more reliable than individual keypresses)
                string escapedText = text.Replace("'", "'\\''");
                Bash.ExecuteCommand($"xdotool type '{escapedText}'");
            }
            else if (DisplayServerDetector.IsWayland)
            {
                // Try PipeWire RemoteDesktop portal first
                var pipeWire = ScreenCaptureLinux.ActivePipeWireCapture;
                if (pipeWire != null && pipeWire.SupportsRemoteInput)
                {
                    // Type each character using keycodes
                    foreach (char c in text)
                    {
                        int keycode = CharToLinuxKeycode(c, out bool needsShift);
                        if (keycode > 0)
                        {
                            Console.WriteLine($"[KeyboardControl] TypeText char='{c}' keycode={keycode} shift={needsShift}");
                            
                            if (needsShift)
                            {
                                pipeWire.KeyDown(42);  // Left Shift
                                Thread.Sleep(10);
                            }
                            
                            pipeWire.PressKey(keycode);
                            Thread.Sleep(10);
                            
                            if (needsShift)
                            {
                                pipeWire.KeyUp(42);
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[KeyboardControl] Unknown char: '{c}' (0x{(int)c:X2})");
                        }
                    }
                    return;
                }
                
                // Fallback to CLI tools
                switch (_inputTool)
                {
                    case "ydotool":
                        string escapedText = text.Replace("'", "'\\''");
                        Bash.ExecuteCommand($"ydotool type '{escapedText}'");
                        break;
                    case "wtype":
                        escapedText = text.Replace("'", "'\\''");
                        Bash.ExecuteCommand($"wtype '{escapedText}'");
                        break;
                    case "xdotool":
                        escapedText = text.Replace("'", "'\\''");
                        Bash.ExecuteCommand($"xdotool type '{escapedText}'");
                        break;
                }
            }
        }
        
        /// <summary>
        /// Convert a character to Linux evdev keycode
        /// </summary>
        private static int CharToLinuxKeycode(char c, out bool needsShift)
        {
            needsShift = false;
            
            // Lowercase letters
            if (c >= 'a' && c <= 'z')
            {
                // a=30, b=48, c=46, etc. (not sequential!)
                return c switch
                {
                    'a' => 30, 'b' => 48, 'c' => 46, 'd' => 32, 'e' => 18,
                    'f' => 33, 'g' => 34, 'h' => 35, 'i' => 23, 'j' => 36,
                    'k' => 37, 'l' => 38, 'm' => 50, 'n' => 49, 'o' => 24,
                    'p' => 25, 'q' => 16, 'r' => 19, 's' => 31, 't' => 20,
                    'u' => 22, 'v' => 47, 'w' => 17, 'x' => 45, 'y' => 21,
                    'z' => 44,
                    _ => 0
                };
            }
            
            // Uppercase letters - same keycode but with shift
            if (c >= 'A' && c <= 'Z')
            {
                needsShift = true;
                return CharToLinuxKeycode(char.ToLower(c), out _);
            }
            
            // Numbers
            if (c >= '0' && c <= '9')
            {
                return c switch
                {
                    '0' => 11, '1' => 2, '2' => 3, '3' => 4, '4' => 5,
                    '5' => 6, '6' => 7, '7' => 8, '8' => 9, '9' => 10,
                    _ => 0
                };
            }
            
            // Special characters
            return c switch
            {
                ' ' => 57,   // Space
                '\n' => 28,  // Enter
                '\t' => 15,  // Tab
                '-' => 12,   // Minus
                '=' => 13,   // Equal
                '[' => 26,   // Left bracket
                ']' => 27,   // Right bracket
                '\\' => 43,  // Backslash
                ';' => 39,   // Semicolon
                '\'' => 40,  // Apostrophe
                '`' => 41,   // Grave
                ',' => 51,   // Comma
                '.' => 52,   // Period
                '/' => 53,   // Slash
                
                // Shifted characters
                '!' => SetShift(ref needsShift, 2),    // Shift+1
                '@' => SetShift(ref needsShift, 3),    // Shift+2
                '#' => SetShift(ref needsShift, 4),    // Shift+3
                '$' => SetShift(ref needsShift, 5),    // Shift+4
                '%' => SetShift(ref needsShift, 6),    // Shift+5
                '^' => SetShift(ref needsShift, 7),    // Shift+6
                '&' => SetShift(ref needsShift, 8),    // Shift+7
                '*' => SetShift(ref needsShift, 9),    // Shift+8
                '(' => SetShift(ref needsShift, 10),   // Shift+9
                ')' => SetShift(ref needsShift, 11),   // Shift+0
                '_' => SetShift(ref needsShift, 12),   // Shift+Minus
                '+' => SetShift(ref needsShift, 13),   // Shift+Equal
                '{' => SetShift(ref needsShift, 26),   // Shift+[
                '}' => SetShift(ref needsShift, 27),   // Shift+]
                '|' => SetShift(ref needsShift, 43),   // Shift+Backslash
                ':' => SetShift(ref needsShift, 39),   // Shift+Semicolon
                '"' => SetShift(ref needsShift, 40),   // Shift+Apostrophe
                '~' => SetShift(ref needsShift, 41),   // Shift+Grave
                '<' => SetShift(ref needsShift, 51),   // Shift+Comma
                '>' => SetShift(ref needsShift, 52),   // Shift+Period
                '?' => SetShift(ref needsShift, 53),   // Shift+Slash
                
                _ => 0
            };
        }
        
        private static int SetShift(ref bool needsShift, int keycode)
        {
            needsShift = true;
            return keycode;
        }

        /// <summary>
        /// Sets the clipboard text
        /// </summary>
        public static void SetClipboardText(string text)
        {
            string escapedText = text.Replace("'", "'\\''");
            
            // Try xclip first, then xsel
            string result = Bash.ExecuteCommand($"echo -n '{escapedText}' | xclip -selection clipboard 2>/dev/null");
            if (string.IsNullOrEmpty(result))
            {
                Bash.ExecuteCommand($"echo -n '{escapedText}' | xsel --clipboard --input 2>/dev/null");
            }
        }

        /// <summary>
        /// Gets the clipboard text
        /// </summary>
        public static string GetClipboardText()
        {
            // Try xclip first
            string result = Bash.ExecuteCommand("xclip -selection clipboard -o 2>/dev/null");
            if (!string.IsNullOrEmpty(result))
                return result;

            // Try xsel
            return Bash.ExecuteCommand("xsel --clipboard --output 2>/dev/null");
        }

        /// <summary>
        /// Converts Windows VK code to X11 keysym
        /// </summary>
        private static ulong VkToKeysym(byte vk)
        {
            return vk switch
            {
                // Letters A-Z (VK 0x41-0x5A)
                0x41 => LibXtst.KeySym.a,
                0x42 => LibXtst.KeySym.b,
                0x43 => LibXtst.KeySym.c,
                0x44 => LibXtst.KeySym.d,
                0x45 => LibXtst.KeySym.e,
                0x46 => LibXtst.KeySym.f,
                0x47 => LibXtst.KeySym.g,
                0x48 => LibXtst.KeySym.h,
                0x49 => LibXtst.KeySym.i,
                0x4A => LibXtst.KeySym.j,
                0x4B => LibXtst.KeySym.k,
                0x4C => LibXtst.KeySym.l,
                0x4D => LibXtst.KeySym.m,
                0x4E => LibXtst.KeySym.n,
                0x4F => LibXtst.KeySym.o,
                0x50 => LibXtst.KeySym.p,
                0x51 => LibXtst.KeySym.q,
                0x52 => LibXtst.KeySym.r,
                0x53 => LibXtst.KeySym.s,
                0x54 => LibXtst.KeySym.t,
                0x55 => LibXtst.KeySym.u,
                0x56 => LibXtst.KeySym.v,
                0x57 => LibXtst.KeySym.w,
                0x58 => LibXtst.KeySym.x,
                0x59 => LibXtst.KeySym.y,
                0x5A => LibXtst.KeySym.z,

                // Numbers 0-9 (VK 0x30-0x39)
                0x30 => LibXtst.KeySym.Num0,
                0x31 => LibXtst.KeySym.Num1,
                0x32 => LibXtst.KeySym.Num2,
                0x33 => LibXtst.KeySym.Num3,
                0x34 => LibXtst.KeySym.Num4,
                0x35 => LibXtst.KeySym.Num5,
                0x36 => LibXtst.KeySym.Num6,
                0x37 => LibXtst.KeySym.Num7,
                0x38 => LibXtst.KeySym.Num8,
                0x39 => LibXtst.KeySym.Num9,

                // Function keys F1-F12
                0x70 => LibXtst.KeySym.F1,
                0x71 => LibXtst.KeySym.F2,
                0x72 => LibXtst.KeySym.F3,
                0x73 => LibXtst.KeySym.F4,
                0x74 => LibXtst.KeySym.F5,
                0x75 => LibXtst.KeySym.F6,
                0x76 => LibXtst.KeySym.F7,
                0x77 => LibXtst.KeySym.F8,
                0x78 => LibXtst.KeySym.F9,
                0x79 => LibXtst.KeySym.F10,
                0x7A => LibXtst.KeySym.F11,
                0x7B => LibXtst.KeySym.F12,

                // Special keys
                0x08 => LibXtst.KeySym.BackSpace,
                0x09 => LibXtst.KeySym.Tab,
                0x0D => LibXtst.KeySym.Return,
                0x1B => LibXtst.KeySym.Escape,
                0x20 => LibXtst.KeySym.Space,
                0x21 => LibXtst.KeySym.Page_Up,
                0x22 => LibXtst.KeySym.Page_Down,
                0x23 => LibXtst.KeySym.End,
                0x24 => LibXtst.KeySym.Home,
                0x25 => LibXtst.KeySym.Left,
                0x26 => LibXtst.KeySym.Up,
                0x27 => LibXtst.KeySym.Right,
                0x28 => LibXtst.KeySym.Down,
                0x2D => LibXtst.KeySym.Insert,
                0x2E => LibXtst.KeySym.Delete,

                // Modifiers
                0x10 => LibXtst.KeySym.Shift_L,
                0x11 => LibXtst.KeySym.Control_L,
                0x12 => LibXtst.KeySym.Alt_L,

                _ => 0
            };
        }

        /// <summary>
        /// Converts keysym to a name usable by xdotool/ydotool
        /// </summary>
        private static string KeysymToName(ulong keysym)
        {
            return keysym switch
            {
                // Letters
                LibXtst.KeySym.a => "a",
                LibXtst.KeySym.b => "b",
                LibXtst.KeySym.c => "c",
                LibXtst.KeySym.d => "d",
                LibXtst.KeySym.e => "e",
                LibXtst.KeySym.f => "f",
                LibXtst.KeySym.g => "g",
                LibXtst.KeySym.h => "h",
                LibXtst.KeySym.i => "i",
                LibXtst.KeySym.j => "j",
                LibXtst.KeySym.k => "k",
                LibXtst.KeySym.l => "l",
                LibXtst.KeySym.m => "m",
                LibXtst.KeySym.n => "n",
                LibXtst.KeySym.o => "o",
                LibXtst.KeySym.p => "p",
                LibXtst.KeySym.q => "q",
                LibXtst.KeySym.r => "r",
                LibXtst.KeySym.s => "s",
                LibXtst.KeySym.t => "t",
                LibXtst.KeySym.u => "u",
                LibXtst.KeySym.v => "v",
                LibXtst.KeySym.w => "w",
                LibXtst.KeySym.x => "x",
                LibXtst.KeySym.y => "y",
                LibXtst.KeySym.z => "z",

                // Special keys
                LibXtst.KeySym.BackSpace => "BackSpace",
                LibXtst.KeySym.Tab => "Tab",
                LibXtst.KeySym.Return => "Return",
                LibXtst.KeySym.Escape => "Escape",
                LibXtst.KeySym.Space => "space",
                LibXtst.KeySym.Delete => "Delete",
                LibXtst.KeySym.Home => "Home",
                LibXtst.KeySym.End => "End",
                LibXtst.KeySym.Page_Up => "Page_Up",
                LibXtst.KeySym.Page_Down => "Page_Down",
                LibXtst.KeySym.Left => "Left",
                LibXtst.KeySym.Right => "Right",
                LibXtst.KeySym.Up => "Up",
                LibXtst.KeySym.Down => "Down",
                LibXtst.KeySym.Insert => "Insert",

                // Function keys
                LibXtst.KeySym.F1 => "F1",
                LibXtst.KeySym.F2 => "F2",
                LibXtst.KeySym.F3 => "F3",
                LibXtst.KeySym.F4 => "F4",
                LibXtst.KeySym.F5 => "F5",
                LibXtst.KeySym.F6 => "F6",
                LibXtst.KeySym.F7 => "F7",
                LibXtst.KeySym.F8 => "F8",
                LibXtst.KeySym.F9 => "F9",
                LibXtst.KeySym.F10 => "F10",
                LibXtst.KeySym.F11 => "F11",
                LibXtst.KeySym.F12 => "F12",

                _ => ""
            };
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public static void Cleanup()
        {
            lock (_displayLock)
            {
                if (_display != IntPtr.Zero)
                {
                    LibX11.XCloseDisplay(_display);
                    _display = IntPtr.Zero;
                }
            }
        }
    }
}


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NetLock_RMM_User_Process.MacOS.Helper;
using static NetLock_RMM_User_Process.MacOS.Helper.CoreGraphics;

namespace NetLock_RMM_User_Process.MacOS.Keyboard
{
    /// <summary>
    /// macOS keyboard control implementation using CoreGraphics CGEvent API
    /// </summary>
    internal static class KeyboardControlMacOS
    {
        private static IntPtr _eventSource = IntPtr.Zero;

        #region macOS Virtual Key Codes (from HIToolbox/Events.h)

        // Letters (ANSI layout)
        private const ushort kVK_ANSI_A = 0x00;
        private const ushort kVK_ANSI_S = 0x01;
        private const ushort kVK_ANSI_D = 0x02;
        private const ushort kVK_ANSI_F = 0x03;
        private const ushort kVK_ANSI_H = 0x04;
        private const ushort kVK_ANSI_G = 0x05;
        private const ushort kVK_ANSI_Z = 0x06;
        private const ushort kVK_ANSI_X = 0x07;
        private const ushort kVK_ANSI_C = 0x08;
        private const ushort kVK_ANSI_V = 0x09;
        private const ushort kVK_ANSI_B = 0x0B;
        private const ushort kVK_ANSI_Q = 0x0C;
        private const ushort kVK_ANSI_W = 0x0D;
        private const ushort kVK_ANSI_E = 0x0E;
        private const ushort kVK_ANSI_R = 0x0F;
        private const ushort kVK_ANSI_Y = 0x10;
        private const ushort kVK_ANSI_T = 0x11;
        private const ushort kVK_ANSI_O = 0x1F;
        private const ushort kVK_ANSI_U = 0x20;
        private const ushort kVK_ANSI_I = 0x22;
        private const ushort kVK_ANSI_P = 0x23;
        private const ushort kVK_ANSI_L = 0x25;
        private const ushort kVK_ANSI_J = 0x26;
        private const ushort kVK_ANSI_K = 0x28;
        private const ushort kVK_ANSI_N = 0x2D;
        private const ushort kVK_ANSI_M = 0x2E;

        // Numbers
        private const ushort kVK_ANSI_1 = 0x12;
        private const ushort kVK_ANSI_2 = 0x13;
        private const ushort kVK_ANSI_3 = 0x14;
        private const ushort kVK_ANSI_4 = 0x15;
        private const ushort kVK_ANSI_5 = 0x17;
        private const ushort kVK_ANSI_6 = 0x16;
        private const ushort kVK_ANSI_7 = 0x1A;
        private const ushort kVK_ANSI_8 = 0x1C;
        private const ushort kVK_ANSI_9 = 0x19;
        private const ushort kVK_ANSI_0 = 0x1D;

        // Special keys
        private const ushort kVK_Return = 0x24;
        private const ushort kVK_Tab = 0x30;
        private const ushort kVK_Space = 0x31;
        private const ushort kVK_Delete = 0x33;        // Backspace
        private const ushort kVK_Escape = 0x35;
        private const ushort kVK_Command = 0x37;
        private const ushort kVK_Shift = 0x38;
        private const ushort kVK_CapsLock = 0x39;
        private const ushort kVK_Option = 0x3A;        // Alt
        private const ushort kVK_Control = 0x3B;
        private const ushort kVK_RightShift = 0x3C;
        private const ushort kVK_RightOption = 0x3D;
        private const ushort kVK_RightControl = 0x3E;
        private const ushort kVK_Function = 0x3F;

        // Function keys
        private const ushort kVK_F1 = 0x7A;
        private const ushort kVK_F2 = 0x78;
        private const ushort kVK_F3 = 0x63;
        private const ushort kVK_F4 = 0x76;
        private const ushort kVK_F5 = 0x60;
        private const ushort kVK_F6 = 0x61;
        private const ushort kVK_F7 = 0x62;
        private const ushort kVK_F8 = 0x64;
        private const ushort kVK_F9 = 0x65;
        private const ushort kVK_F10 = 0x6D;
        private const ushort kVK_F11 = 0x67;
        private const ushort kVK_F12 = 0x6F;

        // Navigation keys
        private const ushort kVK_Home = 0x73;
        private const ushort kVK_End = 0x77;
        private const ushort kVK_PageUp = 0x74;
        private const ushort kVK_PageDown = 0x79;
        private const ushort kVK_ForwardDelete = 0x75;
        private const ushort kVK_LeftArrow = 0x7B;
        private const ushort kVK_RightArrow = 0x7C;
        private const ushort kVK_DownArrow = 0x7D;
        private const ushort kVK_UpArrow = 0x7E;

        // Punctuation
        private const ushort kVK_ANSI_Equal = 0x18;
        private const ushort kVK_ANSI_Minus = 0x1B;
        private const ushort kVK_ANSI_LeftBracket = 0x21;
        private const ushort kVK_ANSI_RightBracket = 0x1E;
        private const ushort kVK_ANSI_Quote = 0x27;
        private const ushort kVK_ANSI_Semicolon = 0x29;
        private const ushort kVK_ANSI_Backslash = 0x2A;
        private const ushort kVK_ANSI_Comma = 0x2B;
        private const ushort kVK_ANSI_Slash = 0x2C;
        private const ushort kVK_ANSI_Period = 0x2F;
        private const ushort kVK_ANSI_Grave = 0x32;

        // Numpad
        private const ushort kVK_ANSI_Keypad0 = 0x52;
        private const ushort kVK_ANSI_Keypad1 = 0x53;
        private const ushort kVK_ANSI_Keypad2 = 0x54;
        private const ushort kVK_ANSI_Keypad3 = 0x55;
        private const ushort kVK_ANSI_Keypad4 = 0x56;
        private const ushort kVK_ANSI_Keypad5 = 0x57;
        private const ushort kVK_ANSI_Keypad6 = 0x58;
        private const ushort kVK_ANSI_Keypad7 = 0x59;
        private const ushort kVK_ANSI_Keypad8 = 0x5B;
        private const ushort kVK_ANSI_Keypad9 = 0x5C;
        private const ushort kVK_ANSI_KeypadMultiply = 0x43;
        private const ushort kVK_ANSI_KeypadPlus = 0x45;
        private const ushort kVK_ANSI_KeypadMinus = 0x4E;
        private const ushort kVK_ANSI_KeypadDecimal = 0x41;
        private const ushort kVK_ANSI_KeypadDivide = 0x4B;
        private const ushort kVK_ANSI_KeypadEnter = 0x4C;

        #endregion

        #region Windows VK Code Constants (for mapping)
        
        // Windows VK codes that we receive from the remote control
        private const byte WIN_VK_BACK = 0x08;
        private const byte WIN_VK_TAB = 0x09;
        private const byte WIN_VK_RETURN = 0x0D;
        private const byte WIN_VK_SHIFT = 0x10;
        private const byte WIN_VK_CONTROL = 0x11;
        private const byte WIN_VK_ALT = 0x12;
        private const byte WIN_VK_PAUSE = 0x13;
        private const byte WIN_VK_CAPITAL = 0x14;
        private const byte WIN_VK_ESCAPE = 0x1B;
        private const byte WIN_VK_SPACE = 0x20;
        private const byte WIN_VK_PRIOR = 0x21;   // Page Up
        private const byte WIN_VK_NEXT = 0x22;    // Page Down
        private const byte WIN_VK_END = 0x23;
        private const byte WIN_VK_HOME = 0x24;
        private const byte WIN_VK_LEFT = 0x25;
        private const byte WIN_VK_UP = 0x26;
        private const byte WIN_VK_RIGHT = 0x27;
        private const byte WIN_VK_DOWN = 0x28;
        private const byte WIN_VK_INSERT = 0x2D;
        private const byte WIN_VK_DELETE = 0x2E;
        // 0x30-0x39: Numbers 0-9
        // 0x41-0x5A: Letters A-Z
        private const byte WIN_VK_NUMPAD0 = 0x60;
        private const byte WIN_VK_NUMPAD9 = 0x69;
        private const byte WIN_VK_MULTIPLY = 0x6A;
        private const byte WIN_VK_ADD = 0x6B;
        private const byte WIN_VK_SUBTRACT = 0x6D;
        private const byte WIN_VK_DECIMAL = 0x6E;
        private const byte WIN_VK_DIVIDE = 0x6F;
        private const byte WIN_VK_F1 = 0x70;
        private const byte WIN_VK_F12 = 0x7B;
        private const byte WIN_VK_NUMLOCK = 0x90;
        private const byte WIN_VK_SCROLL = 0x91;
        private const byte WIN_VK_SEMICOLON = 0xBA;
        private const byte WIN_VK_EQUALS = 0xBB;
        private const byte WIN_VK_COMMA = 0xBC;
        private const byte WIN_VK_MINUS = 0xBD;
        private const byte WIN_VK_PERIOD = 0xBE;
        private const byte WIN_VK_SLASH = 0xBF;
        private const byte WIN_VK_GRAVE = 0xC0;
        private const byte WIN_VK_LBRACKET = 0xDB;
        private const byte WIN_VK_BACKSLASH = 0xDC;
        private const byte WIN_VK_RBRACKET = 0xDD;
        private const byte WIN_VK_APOSTROPHE = 0xDE;
        
        #endregion

        private static bool _initialized = false;

        /// <summary>
        /// Initialize keyboard control and check permissions
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            Console.WriteLine("[MacOS Keyboard] Initializing...");
            
            // Test if we can create events (requires Accessibility permissions for CGEvent)
            var eventSource = GetEventSource();
            if (eventSource == IntPtr.Zero)
            {
                Console.WriteLine("[MacOS Keyboard] WARNING: CGEventSource creation failed.");
                Console.WriteLine("[MacOS Keyboard] This usually means Accessibility permissions are not granted.");
            }
            else
            {
                Console.WriteLine("[MacOS Keyboard] CGEventSource created successfully");
            }
            
            // Test AppleScript availability with a simple return
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("return \"ok\"");

                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit(2000);
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    var stderr = process.StandardError.ReadToEnd().Trim();
                    
                    if (output == "ok")
                    {
                        Console.WriteLine("[MacOS Keyboard] AppleScript (osascript) is available");
                    }
                    else
                    {
                        Console.WriteLine($"[MacOS Keyboard] AppleScript test returned: {output}");
                        if (!string.IsNullOrEmpty(stderr))
                        {
                            Console.WriteLine($"[MacOS Keyboard] AppleScript stderr: {stderr}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Keyboard] AppleScript test failed: {ex.Message}");
            }
            
            // Test if System Events access is granted
            Console.WriteLine("[MacOS Keyboard] Testing System Events access...");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("tell application \"System Events\" to return name of first process");

                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit(3000);
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    var stderr = process.StandardError.ReadToEnd().Trim();
                    
                    if (string.IsNullOrEmpty(stderr))
                    {
                        Console.WriteLine($"[MacOS Keyboard] System Events access OK (first process: {output})");
                    }
                    else
                    {
                        Console.WriteLine($"[MacOS Keyboard] System Events access FAILED: {stderr}");
                        Console.WriteLine("[MacOS Keyboard] ========================================");
                        Console.WriteLine("[MacOS Keyboard] KEYBOARD INPUT WILL NOT WORK!");
                        Console.WriteLine("[MacOS Keyboard] To fix this:");
                        Console.WriteLine("[MacOS Keyboard] 1. Open System Preferences (or System Settings on macOS 13+)");
                        Console.WriteLine("[MacOS Keyboard] 2. Go to Security & Privacy > Privacy > Accessibility");
                        Console.WriteLine("[MacOS Keyboard] 3. Click the lock to make changes");
                        Console.WriteLine("[MacOS Keyboard] 4. Add Terminal (or this application) to the list");
                        Console.WriteLine("[MacOS Keyboard] 5. Ensure the checkbox is enabled");
                        Console.WriteLine("[MacOS Keyboard] 6. Restart this application");
                        Console.WriteLine("[MacOS Keyboard] ========================================");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Keyboard] System Events test failed: {ex.Message}");
            }
            
            _initialized = true;
            Console.WriteLine("[MacOS Keyboard] Initialization complete");
        }

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
                    Console.WriteLine("[MacOS Keyboard] Failed to create CGEventSource");
                }
            }
            return _eventSource;
        }

        /// <summary>
        /// Map Windows VK code to macOS virtual key code
        /// This is the main mapping function used when receiving input from remote control
        /// </summary>
        private static ushort? MapWindowsVkToMacKeyCode(int vkCode)
        {
            // Letters A-Z (Windows VK: 0x41-0x5A)
            if (vkCode >= 0x41 && vkCode <= 0x5A)
            {
                char letter = (char)vkCode;
                return MapLetterToKeyCode(letter);
            }
            
            // Numbers 0-9 (Windows VK: 0x30-0x39)
            if (vkCode >= 0x30 && vkCode <= 0x39)
            {
                char number = (char)vkCode;
                return MapNumberToKeyCode(number);
            }
            
            // Numpad 0-9 (Windows VK: 0x60-0x69)
            if (vkCode >= WIN_VK_NUMPAD0 && vkCode <= WIN_VK_NUMPAD9)
            {
                int numpadIndex = vkCode - WIN_VK_NUMPAD0;
                return numpadIndex switch
                {
                    0 => kVK_ANSI_Keypad0,
                    1 => kVK_ANSI_Keypad1,
                    2 => kVK_ANSI_Keypad2,
                    3 => kVK_ANSI_Keypad3,
                    4 => kVK_ANSI_Keypad4,
                    5 => kVK_ANSI_Keypad5,
                    6 => kVK_ANSI_Keypad6,
                    7 => kVK_ANSI_Keypad7,
                    8 => kVK_ANSI_Keypad8,
                    9 => kVK_ANSI_Keypad9,
                    _ => null
                };
            }
            
            // Function keys F1-F12 (Windows VK: 0x70-0x7B)
            if (vkCode >= WIN_VK_F1 && vkCode <= WIN_VK_F12)
            {
                int fKeyIndex = vkCode - WIN_VK_F1;
                return fKeyIndex switch
                {
                    0 => kVK_F1,
                    1 => kVK_F2,
                    2 => kVK_F3,
                    3 => kVK_F4,
                    4 => kVK_F5,
                    5 => kVK_F6,
                    6 => kVK_F7,
                    7 => kVK_F8,
                    8 => kVK_F9,
                    9 => kVK_F10,
                    10 => kVK_F11,
                    11 => kVK_F12,
                    _ => null
                };
            }
            
            // Other keys
            return vkCode switch
            {
                // Control keys
                WIN_VK_BACK => kVK_Delete,           // Backspace
                WIN_VK_TAB => kVK_Tab,
                WIN_VK_RETURN => kVK_Return,         // Enter
                WIN_VK_ESCAPE => kVK_Escape,
                WIN_VK_SPACE => kVK_Space,
                WIN_VK_DELETE => kVK_ForwardDelete,  // Delete (forward)
                WIN_VK_INSERT => kVK_Function,       // No direct equivalent, use Fn
                
                // Navigation
                WIN_VK_HOME => kVK_Home,
                WIN_VK_END => kVK_End,
                WIN_VK_PRIOR => kVK_PageUp,
                WIN_VK_NEXT => kVK_PageDown,
                WIN_VK_LEFT => kVK_LeftArrow,
                WIN_VK_UP => kVK_UpArrow,
                WIN_VK_RIGHT => kVK_RightArrow,
                WIN_VK_DOWN => kVK_DownArrow,
                
                // Modifiers
                WIN_VK_SHIFT => kVK_Shift,
                WIN_VK_CONTROL => kVK_Control,
                WIN_VK_ALT => kVK_Option,
                WIN_VK_CAPITAL => kVK_CapsLock,
                
                // Numpad operators
                WIN_VK_MULTIPLY => kVK_ANSI_KeypadMultiply,
                WIN_VK_ADD => kVK_ANSI_KeypadPlus,
                WIN_VK_SUBTRACT => kVK_ANSI_KeypadMinus,
                WIN_VK_DECIMAL => kVK_ANSI_KeypadDecimal,
                WIN_VK_DIVIDE => kVK_ANSI_KeypadDivide,
                
                // Punctuation
                WIN_VK_SEMICOLON => kVK_ANSI_Semicolon,
                WIN_VK_EQUALS => kVK_ANSI_Equal,
                WIN_VK_COMMA => kVK_ANSI_Comma,
                WIN_VK_MINUS => kVK_ANSI_Minus,
                WIN_VK_PERIOD => kVK_ANSI_Period,
                WIN_VK_SLASH => kVK_ANSI_Slash,
                WIN_VK_GRAVE => kVK_ANSI_Grave,
                WIN_VK_LBRACKET => kVK_ANSI_LeftBracket,
                WIN_VK_BACKSLASH => kVK_ANSI_Backslash,
                WIN_VK_RBRACKET => kVK_ANSI_RightBracket,
                WIN_VK_APOSTROPHE => kVK_ANSI_Quote,
                
                _ => null
            };
        }

        /// <summary>
        /// Map ASCII code to macOS virtual key code (legacy support)
        /// </summary>
        private static ushort? MapAsciiToKeyCode(int asciiCode)
        {
            return asciiCode switch
            {
                // Letters (lowercase)
                >= 97 and <= 122 => MapLetterToKeyCode((char)asciiCode),
                // Letters (uppercase)
                >= 65 and <= 90 => MapLetterToKeyCode((char)(asciiCode + 32)),
                // Numbers
                >= 48 and <= 57 => MapNumberToKeyCode((char)asciiCode),
                // Special characters
                8 => kVK_Delete,           // Backspace
                9 => kVK_Tab,              // Tab
                10 or 13 => kVK_Return,    // Enter
                27 => kVK_Escape,          // Escape
                32 => kVK_Space,           // Space
                127 => kVK_ForwardDelete,  // Delete
                _ => null
            };
        }

        private static ushort? MapLetterToKeyCode(char letter)
        {
            return char.ToLower(letter) switch
            {
                'a' => kVK_ANSI_A,
                'b' => kVK_ANSI_B,
                'c' => kVK_ANSI_C,
                'd' => kVK_ANSI_D,
                'e' => kVK_ANSI_E,
                'f' => kVK_ANSI_F,
                'g' => kVK_ANSI_G,
                'h' => kVK_ANSI_H,
                'i' => kVK_ANSI_I,
                'j' => kVK_ANSI_J,
                'k' => kVK_ANSI_K,
                'l' => kVK_ANSI_L,
                'm' => kVK_ANSI_M,
                'n' => kVK_ANSI_N,
                'o' => kVK_ANSI_O,
                'p' => kVK_ANSI_P,
                'q' => kVK_ANSI_Q,
                'r' => kVK_ANSI_R,
                's' => kVK_ANSI_S,
                't' => kVK_ANSI_T,
                'u' => kVK_ANSI_U,
                'v' => kVK_ANSI_V,
                'w' => kVK_ANSI_W,
                'x' => kVK_ANSI_X,
                'y' => kVK_ANSI_Y,
                'z' => kVK_ANSI_Z,
                _ => null
            };
        }

        private static ushort? MapNumberToKeyCode(char number)
        {
            return number switch
            {
                '0' => kVK_ANSI_0,
                '1' => kVK_ANSI_1,
                '2' => kVK_ANSI_2,
                '3' => kVK_ANSI_3,
                '4' => kVK_ANSI_4,
                '5' => kVK_ANSI_5,
                '6' => kVK_ANSI_6,
                '7' => kVK_ANSI_7,
                '8' => kVK_ANSI_8,
                '9' => kVK_ANSI_9,
                _ => null
            };
        }

        /// <summary>
        /// Map special key string to key code
        /// </summary>
        private static ushort? MapSpecialKeyToKeyCode(string keyName)
        {
            return keyName.ToLower() switch
            {
                "enter" or "return" => kVK_Return,
                "tab" => kVK_Tab,
                "space" => kVK_Space,
                "backspace" => kVK_Delete,
                "delete" => kVK_ForwardDelete,
                "escape" or "esc" => kVK_Escape,
                "arrowleft" or "left" => kVK_LeftArrow,
                "arrowright" or "right" => kVK_RightArrow,
                "arrowup" or "up" => kVK_UpArrow,
                "arrowdown" or "down" => kVK_DownArrow,
                "home" => kVK_Home,
                "end" => kVK_End,
                "pageup" => kVK_PageUp,
                "pagedown" => kVK_PageDown,
                "f1" => kVK_F1,
                "f2" => kVK_F2,
                "f3" => kVK_F3,
                "f4" => kVK_F4,
                "f5" => kVK_F5,
                "f6" => kVK_F6,
                "f7" => kVK_F7,
                "f8" => kVK_F8,
                "f9" => kVK_F9,
                "f10" => kVK_F10,
                "f11" => kVK_F11,
                "f12" => kVK_F12,
                _ => null
            };
        }

        #region Key Press Methods

        /// <summary>
        /// Send a key press (down + up) with optional modifiers
        /// This accepts Windows VK codes (as used by the remote control protocol)
        /// </summary>
        public static Task SendKey(int vkCode, bool shift = false)
        {
            Console.WriteLine($"[MacOS Keyboard] SendKey called with vkCode=0x{vkCode:X2}, shift={shift}");
            
            // Map Windows VK code to macOS key code
            var keyCode = MapWindowsVkToMacKeyCode(vkCode);
            if (keyCode.HasValue)
            {
                Console.WriteLine($"[MacOS Keyboard] Mapped to macOS keyCode=0x{keyCode.Value:X2}");
                return SendKeyPress(keyCode.Value, shift, false, false);
            }
            
            Console.WriteLine($"[MacOS Keyboard] Unknown VK code: 0x{vkCode:X2}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Send a key press with the specified modifiers
        /// Uses AppleScript as primary method (more reliable), falls back to CGEvent
        /// </summary>
        private static Task SendKeyPress(ushort keyCode, bool shift = false, bool command = false, bool option = false, bool control = false)
        {
            Console.WriteLine($"[MacOS Keyboard] SendKeyPress: keyCode=0x{keyCode:X2}, shift={shift}, cmd={command}, opt={option}, ctrl={control}");
            
            // Try AppleScript first (more reliable, doesn't require Accessibility permissions for most cases)
            if (TrySendKeyViaAppleScript(keyCode, shift, command, option, control))
            {
                return Task.CompletedTask;
            }
            
            // Fall back to CGEvent
            Console.WriteLine("[MacOS Keyboard] AppleScript failed, trying CGEvent...");
            return SendKeyPressCGEvent(keyCode, shift, command, option, control);
        }

        /// <summary>
        /// Try to send a key press via AppleScript
        /// </summary>
        private static bool TrySendKeyViaAppleScript(ushort keyCode, bool shift, bool command, bool option, bool control)
        {
            try
            {
                // Build the AppleScript key code command
                // AppleScript uses the same key codes as Carbon/HIToolbox
                var modList = new System.Collections.Generic.List<string>();
                
                if (shift) modList.Add("shift down");
                if (command) modList.Add("command down");
                if (option) modList.Add("option down");
                if (control) modList.Add("control down");
                
                string modifiers = modList.Count > 0 
                    ? $" using {{{string.Join(", ", modList)}}}" 
                    : "";
                
                // Build the complete AppleScript
                string script = $"tell application \"System Events\" to key code {keyCode}{modifiers}";
                
                Console.WriteLine($"[MacOS Keyboard] AppleScript: {script}");
                
                // Use ProcessStartInfo with ArgumentList for proper escaping
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Console.WriteLine("[MacOS Keyboard] Failed to start osascript");
                    return false;
                }

                process.WaitForExit(3000);
                
                var stderr = process.StandardError.ReadToEnd();
                var stdout = process.StandardOutput.ReadToEnd();
                
                if (!string.IsNullOrEmpty(stderr))
                {
                    Console.WriteLine($"[MacOS Keyboard] AppleScript stderr: {stderr}");
                    if (stderr.Contains("not allowed") || stderr.Contains("accessibility") || stderr.Contains("1002"))
                    {
                        Console.WriteLine("[MacOS Keyboard] Permission denied - need Accessibility access");
                        Console.WriteLine("[MacOS Keyboard] Go to: System Preferences > Security & Privacy > Privacy > Accessibility");
                    }
                    return false;
                }
                
                if (!string.IsNullOrEmpty(stdout))
                {
                    Console.WriteLine($"[MacOS Keyboard] AppleScript stdout: {stdout}");
                }
                
                Console.WriteLine("[MacOS Keyboard] AppleScript key code sent successfully");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Keyboard] AppleScript error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send a key press using CGEvent (requires Accessibility permissions)
        /// </summary>
        private static Task SendKeyPressCGEvent(ushort keyCode, bool shift = false, bool command = false, bool option = false, bool control = false)
        {
            try
            {
                IntPtr source = GetEventSource();
                
                if (source == IntPtr.Zero)
                {
                    Console.WriteLine("[MacOS Keyboard] CGEventSource is null - Accessibility permissions may be required");
                    Console.WriteLine("[MacOS Keyboard] Grant access in System Preferences > Security & Privacy > Privacy > Accessibility");
                    return Task.CompletedTask;
                }
                
                // Build flags
                CGEventFlags flags = CGEventFlags.None;
                if (shift) flags |= CGEventFlags.Shift;
                if (command) flags |= CGEventFlags.Command;
                if (option) flags |= CGEventFlags.Alternate;
                if (control) flags |= CGEventFlags.Control;

                Console.WriteLine($"[MacOS Keyboard] CGEvent: Sending keyCode=0x{keyCode:X2} with flags={flags}");

                // Create key down event
                IntPtr keyDown = CGEventCreateKeyboardEvent(source, keyCode, true);
                if (keyDown != IntPtr.Zero)
                {
                    if (flags != CGEventFlags.None)
                        CGEventSetFlags(keyDown, flags);
                    CGEventPost(CGEventTapLocation.HID, keyDown);
                    CFRelease(keyDown);
                }
                else
                {
                    Console.WriteLine("[MacOS Keyboard] Failed to create key down event - check Accessibility permissions");
                    return Task.CompletedTask;
                }

                // Small delay between down and up
                System.Threading.Thread.Sleep(10);

                // Create key up event
                IntPtr keyUp = CGEventCreateKeyboardEvent(source, keyCode, false);
                if (keyUp != IntPtr.Zero)
                {
                    if (flags != CGEventFlags.None)
                        CGEventSetFlags(keyUp, flags);
                    CGEventPost(CGEventTapLocation.HID, keyUp);
                    CFRelease(keyUp);
                }
                else
                {
                    Console.WriteLine("[MacOS Keyboard] Failed to create key up event");
                }
                
                Console.WriteLine("[MacOS Keyboard] CGEvent key sent");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Keyboard] CGEvent error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Type a string of text using keyboard simulation
        /// Uses AppleScript keystroke command which is most reliable
        /// </summary>
        public static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            Console.WriteLine($"[MacOS Keyboard] TypeText: '{text}'");

            // Use AppleScript for all text input (most reliable)
            try
            {
                // Escape backslashes and quotes for AppleScript string
                string escapedText = text
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"");

                // Build the AppleScript command
                string script = $"tell application \"System Events\" to keystroke \"{escapedText}\"";
                
                Console.WriteLine($"[MacOS Keyboard] AppleScript: {script}");
                
                // Use ProcessStartInfo with ArgumentList for proper escaping
                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit(5000);
                    var stderr = process.StandardError.ReadToEnd();
                    var stdout = process.StandardOutput.ReadToEnd();
                    
                    if (!string.IsNullOrEmpty(stderr))
                    {
                        Console.WriteLine($"[MacOS Keyboard] TypeText stderr: {stderr}");
                        // If AppleScript failed, try CGEvent as fallback for single chars
                        if (text.Length == 1)
                        {
                            TrySendCharViaCGEvent(text[0]);
                        }
                    }
                    else
                    {
                        Console.WriteLine("[MacOS Keyboard] TypeText successful via AppleScript");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Keyboard] TypeText error: {ex.Message}");
                // Try CGEvent as fallback for single characters
                if (text.Length == 1)
                {
                    TrySendCharViaCGEvent(text[0]);
                }
            }
        }

        /// <summary>
        /// Try to send a single character via CGEvent (fallback)
        /// </summary>
        private static void TrySendCharViaCGEvent(char c)
        {
            bool needsShift = char.IsUpper(c) || IsShiftedChar(c);
            ushort? keyCode = GetKeyCodeForChar(c);
            
            if (keyCode.HasValue)
            {
                Console.WriteLine($"[MacOS Keyboard] Fallback: Sending char '{c}' via CGEvent");
                SendKeyPressCGEvent(keyCode.Value, shift: needsShift);
            }
        }

        /// <summary>
        /// Check if a character requires Shift to type
        /// </summary>
        private static bool IsShiftedChar(char c)
        {
            return c switch
            {
                '!' or '@' or '#' or '$' or '%' or '^' or '&' or '*' or '(' or ')' => true,
                '_' or '+' or '{' or '}' or '|' or ':' or '"' or '<' or '>' or '?' or '~' => true,
                _ => false
            };
        }

        /// <summary>
        /// Get the macOS key code for a character
        /// </summary>
        private static ushort? GetKeyCodeForChar(char c)
        {
            // Handle lowercase letters
            if (c >= 'a' && c <= 'z')
                return MapLetterToKeyCode(c);
            
            // Handle uppercase letters (same key code, just needs shift)
            if (c >= 'A' && c <= 'Z')
                return MapLetterToKeyCode(char.ToLower(c));
            
            // Handle numbers
            if (c >= '0' && c <= '9')
                return MapNumberToKeyCode(c);
            
            // Handle special characters
            return c switch
            {
                ' ' => kVK_Space,
                '\t' => kVK_Tab,
                '\n' or '\r' => kVK_Return,
                // Unshifted punctuation
                '-' or '_' => kVK_ANSI_Minus,
                '=' or '+' => kVK_ANSI_Equal,
                '[' or '{' => kVK_ANSI_LeftBracket,
                ']' or '}' => kVK_ANSI_RightBracket,
                '\\' or '|' => kVK_ANSI_Backslash,
                ';' or ':' => kVK_ANSI_Semicolon,
                '\'' or '"' => kVK_ANSI_Quote,
                ',' or '<' => kVK_ANSI_Comma,
                '.' or '>' => kVK_ANSI_Period,
                '/' or '?' => kVK_ANSI_Slash,
                '`' or '~' => kVK_ANSI_Grave,
                // Shifted numbers -> symbols
                '!' => kVK_ANSI_1,
                '@' => kVK_ANSI_2,
                '#' => kVK_ANSI_3,
                '$' => kVK_ANSI_4,
                '%' => kVK_ANSI_5,
                '^' => kVK_ANSI_6,
                '&' => kVK_ANSI_7,
                '*' => kVK_ANSI_8,
                '(' => kVK_ANSI_9,
                ')' => kVK_ANSI_0,
                _ => null
            };
        }

        #endregion

        #region Keyboard Shortcuts (Cmd+Key instead of Ctrl+Key on macOS)

        public static void SendCmdA() => SendKeyPress(kVK_ANSI_A, command: true);
        public static void SendCmdC() => SendKeyPress(kVK_ANSI_C, command: true);
        public static void SendCmdV() => SendKeyPress(kVK_ANSI_V, command: true);
        
        public static void SendCmdV(string? clipboardContent)
        {
            if (!string.IsNullOrEmpty(clipboardContent))
            {
                SetClipboardText(clipboardContent);
            }
            SendKeyPress(kVK_ANSI_V, command: true);
        }
        
        public static void SendCmdX() => SendKeyPress(kVK_ANSI_X, command: true);
        public static void SendCmdZ() => SendKeyPress(kVK_ANSI_Z, command: true);
        public static void SendCmdShiftZ() => SendKeyPress(kVK_ANSI_Z, shift: true, command: true); // Redo
        public static void SendCmdY() => SendKeyPress(kVK_ANSI_Y, command: true);
        public static void SendCmdS() => SendKeyPress(kVK_ANSI_S, command: true);
        public static void SendCmdN() => SendKeyPress(kVK_ANSI_N, command: true);
        public static void SendCmdP() => SendKeyPress(kVK_ANSI_P, command: true);
        public static void SendCmdF() => SendKeyPress(kVK_ANSI_F, command: true);
        public static void SendCmdR() => SendKeyPress(kVK_ANSI_R, command: true);
        public static void SendCmdT() => SendKeyPress(kVK_ANSI_T, command: true);
        public static void SendCmdShiftT() => SendKeyPress(kVK_ANSI_T, shift: true, command: true);
        
        // Arrow key shortcuts
        public static void SendCmdArrowLeft() => SendKeyPress(kVK_LeftArrow, command: true);
        public static void SendCmdArrowRight() => SendKeyPress(kVK_RightArrow, command: true);
        public static void SendCmdArrowUp() => SendKeyPress(kVK_UpArrow, command: true);
        public static void SendCmdArrowDown() => SendKeyPress(kVK_DownArrow, command: true);
        
        // Option (Alt) + Backspace = delete word
        public static void SendOptionBackspace() => SendKeyPress(kVK_Delete, option: true);
        
        // Ctrl+Alt+Delete equivalent on Mac - show Force Quit dialog
        public static void SendForceQuitDialog()
        {
            // Cmd+Option+Escape opens Force Quit Applications
            SendKeyPress(kVK_Escape, command: true, option: true);
        }

        // Compatibility aliases (Ctrl -> Cmd for macOS)
        public static void SendCtrlA() => SendCmdA();
        public static void SendCtrlC() => SendCmdC();
        public static void SendCtrlV(string? content = null) => SendCmdV(content);
        public static void SendCtrlX() => SendCmdX();
        public static void SendCtrlZ() => SendCmdZ();
        public static void SendCtrlY() => SendCmdShiftZ(); // macOS uses Cmd+Shift+Z for redo
        public static void SendCtrlS() => SendCmdS();
        public static void SendCtrlN() => SendCmdN();
        public static void SendCtrlP() => SendCmdP();
        public static void SendCtrlF() => SendCmdF();
        public static void SendCtrlR() => SendCmdR();
        public static void SendCtrlShiftT() => SendCmdShiftT();
        public static void SendCtrlBackspace() => SendOptionBackspace();
        public static void SendCtrlArrowLeft() => SendKeyPress(kVK_LeftArrow, option: true); // Option moves by word on Mac
        public static void SendCtrlArrowRight() => SendKeyPress(kVK_RightArrow, option: true);
        public static void SendCtrlArrowUp() => SendCmdArrowUp();
        public static void SendCtrlArrowDown() => SendCmdArrowDown();
        public static void SendCtrlAltDelete() => SendForceQuitDialog();

        #endregion

        #region Clipboard

        /// <summary>
        /// Get clipboard text using pbpaste
        /// </summary>
        public static string GetClipboardText()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pbpaste",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    return output;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Keyboard] GetClipboardText error: {ex.Message}");
            }
            return string.Empty;
        }

        /// <summary>
        /// Set clipboard text using pbcopy
        /// </summary>
        public static void SetClipboardText(string text)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pbcopy",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.StandardInput.Write(text);
                    process.StandardInput.Close();
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacOS Keyboard] SetClipboardText error: {ex.Message}");
            }
        }

        #endregion

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


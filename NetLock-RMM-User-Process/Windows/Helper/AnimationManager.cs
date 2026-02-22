using System;
using System.Runtime.InteropServices;

namespace NetLock_RMM_User_Process.Windows.Helper
{
    /// <summary>
    /// Manages Windows UI animations for optimized remote desktop performance.
    /// Disables animations during remote sessions and restores them afterward.
    /// </summary>
    public static class AnimationManager
    {
        #region Native Methods and Constants

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref ANIMATIONINFO pvParam, uint fWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmIsCompositionEnabled(out bool enabled);

        // SystemParametersInfo actions
        private const uint SPI_GETANIMATION = 0x0048;
        private const uint SPI_SETANIMATION = 0x0049;
        private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
        private const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
        private const uint SPI_GETCOMBOBOXANIMATION = 0x1004;
        private const uint SPI_SETCOMBOBOXANIMATION = 0x1005;
        private const uint SPI_GETLISTBOXSMOOTHSCROLLING = 0x1006;
        private const uint SPI_SETLISTBOXSMOOTHSCROLLING = 0x1007;
        private const uint SPI_GETMENUANIMATION = 0x1002;
        private const uint SPI_SETMENUANIMATION = 0x1003;
        private const uint SPI_GETSELECTIONFADE = 0x1014;
        private const uint SPI_SETSELECTIONFADE = 0x1015;
        private const uint SPI_GETTOOLTIPANIMATION = 0x1016;
        private const uint SPI_SETTOOLTIPANIMATION = 0x1017;
        private const uint SPI_GETTOOLTIPFADE = 0x1018;
        private const uint SPI_SETTOOLTIPFADE = 0x1019;
        private const uint SPI_GETCURSORSHADOW = 0x101A;
        private const uint SPI_SETCURSORSHADOW = 0x101B;
        private const uint SPI_GETUIEFFECTS = 0x103E;
        private const uint SPI_SETUIEFFECTS = 0x103F;
        private const uint SPI_GETDRAGFULLWINDOWS = 0x0026;
        private const uint SPI_SETDRAGFULLWINDOWS = 0x0025;
        private const uint SPI_GETFONTSMOOTHING = 0x004A;
        private const uint SPI_SETFONTSMOOTHING = 0x004B;

        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;
        private const uint SPIF_SENDWININICHANGE = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        private struct ANIMATIONINFO
        {
            public uint cbSize;
            public int iMinAnimate;

            public static ANIMATIONINFO Create()
            {
                return new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf(typeof(ANIMATIONINFO)) };
            }
        }

        #endregion

        #region State Storage

        // Store original settings to restore later
        private static bool _originalAnimationEnabled;
        private static bool _originalClientAreaAnimation;
        private static bool _originalComboBoxAnimation;
        private static bool _originalListBoxSmoothScrolling;
        private static bool _originalMenuAnimation;
        private static bool _originalSelectionFade;
        private static bool _originalTooltipAnimation;
        private static bool _originalTooltipFade;
        private static bool _originalCursorShadow;
        private static bool _originalUIEffects;
        private static bool _originalDragFullWindows;
        private static bool _originalFontSmoothing;

        private static bool _settingsSaved = false;
        private static bool _animationsDisabled = false;
        private static readonly object _lock = new object();

        #endregion

        #region Public Methods

        /// <summary>
        /// Disables Windows animations for optimal remote desktop performance.
        /// Saves the current settings to restore them later.
        /// </summary>
        /// <returns>True if animations were successfully disabled</returns>
        public static bool DisableAnimations()
        {
            lock (_lock)
            {
                if (_animationsDisabled)
                {
                    Console.WriteLine("Animations are already disabled.");
                    return true;
                }

                try
                {
                    // Save current settings first
                    SaveCurrentSettings();

                    Console.WriteLine("Disabling Windows animations for remote session...");

                    // Disable window minimize/maximize animation
                    var animInfo = ANIMATIONINFO.Create();
                    animInfo.iMinAnimate = 0; // Disable
                    SystemParametersInfo(SPI_SETANIMATION, animInfo.cbSize, ref animInfo, SPIF_SENDCHANGE);

                    // Disable client area animations
                    SetBoolSetting(SPI_SETCLIENTAREAANIMATION, false);

                    // Disable combo box animation
                    SetBoolSetting(SPI_SETCOMBOBOXANIMATION, false);

                    // Disable listbox smooth scrolling
                    SetBoolSetting(SPI_SETLISTBOXSMOOTHSCROLLING, false);

                    // Disable menu animation
                    SetBoolSetting(SPI_SETMENUANIMATION, false);

                    // Disable selection fade
                    SetBoolSetting(SPI_SETSELECTIONFADE, false);

                    // Disable tooltip animation
                    SetBoolSetting(SPI_SETTOOLTIPANIMATION, false);

                    // Disable tooltip fade
                    SetBoolSetting(SPI_SETTOOLTIPFADE, false);

                    // Disable cursor shadow (minor performance gain)
                    SetBoolSetting(SPI_SETCURSORSHADOW, false);

                    // Disable UI effects (visual styles animations)
                    SetBoolSetting(SPI_SETUIEFFECTS, false);

                    // Disable drag full windows (show window contents while dragging)
                    // This can significantly reduce CPU/GPU load during window dragging
                    SetBoolSetting(SPI_SETDRAGFULLWINDOWS, false);

                    _animationsDisabled = true;
                    Console.WriteLine("Windows animations disabled successfully.");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error disabling animations: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Restores Windows animations to their original settings.
        /// </summary>
        /// <returns>True if animations were successfully restored</returns>
        public static bool RestoreAnimations()
        {
            lock (_lock)
            {
                if (!_animationsDisabled)
                {
                    Console.WriteLine("Animations are not disabled, nothing to restore.");
                    return true;
                }

                if (!_settingsSaved)
                {
                    Console.WriteLine("Original settings were not saved, cannot restore.");
                    return false;
                }

                try
                {
                    Console.WriteLine("Restoring Windows animations...");

                    // Restore window minimize/maximize animation
                    var animInfo = ANIMATIONINFO.Create();
                    animInfo.iMinAnimate = _originalAnimationEnabled ? 1 : 0;
                    SystemParametersInfo(SPI_SETANIMATION, animInfo.cbSize, ref animInfo, SPIF_SENDCHANGE);

                    // Restore all other settings
                    SetBoolSetting(SPI_SETCLIENTAREAANIMATION, _originalClientAreaAnimation);
                    SetBoolSetting(SPI_SETCOMBOBOXANIMATION, _originalComboBoxAnimation);
                    SetBoolSetting(SPI_SETLISTBOXSMOOTHSCROLLING, _originalListBoxSmoothScrolling);
                    SetBoolSetting(SPI_SETMENUANIMATION, _originalMenuAnimation);
                    SetBoolSetting(SPI_SETSELECTIONFADE, _originalSelectionFade);
                    SetBoolSetting(SPI_SETTOOLTIPANIMATION, _originalTooltipAnimation);
                    SetBoolSetting(SPI_SETTOOLTIPFADE, _originalTooltipFade);
                    SetBoolSetting(SPI_SETCURSORSHADOW, _originalCursorShadow);
                    SetBoolSetting(SPI_SETUIEFFECTS, _originalUIEffects);
                    SetBoolSetting(SPI_SETDRAGFULLWINDOWS, _originalDragFullWindows);

                    _animationsDisabled = false;
                    Console.WriteLine("Windows animations restored successfully.");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error restoring animations: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Checks if animations are currently disabled by this manager.
        /// </summary>
        public static bool AreAnimationsDisabled => _animationsDisabled;

        #endregion

        #region Private Methods

        private static void SaveCurrentSettings()
        {
            if (_settingsSaved)
                return;

            try
            {
                // Get window minimize/maximize animation
                var animInfo = ANIMATIONINFO.Create();
                SystemParametersInfo(SPI_GETANIMATION, animInfo.cbSize, ref animInfo, 0);
                _originalAnimationEnabled = animInfo.iMinAnimate != 0;

                // Get all other settings
                _originalClientAreaAnimation = GetBoolSetting(SPI_GETCLIENTAREAANIMATION);
                _originalComboBoxAnimation = GetBoolSetting(SPI_GETCOMBOBOXANIMATION);
                _originalListBoxSmoothScrolling = GetBoolSetting(SPI_GETLISTBOXSMOOTHSCROLLING);
                _originalMenuAnimation = GetBoolSetting(SPI_GETMENUANIMATION);
                _originalSelectionFade = GetBoolSetting(SPI_GETSELECTIONFADE);
                _originalTooltipAnimation = GetBoolSetting(SPI_GETTOOLTIPANIMATION);
                _originalTooltipFade = GetBoolSetting(SPI_GETTOOLTIPFADE);
                _originalCursorShadow = GetBoolSetting(SPI_GETCURSORSHADOW);
                _originalUIEffects = GetBoolSetting(SPI_GETUIEFFECTS);
                _originalDragFullWindows = GetBoolSetting(SPI_GETDRAGFULLWINDOWS);

                _settingsSaved = true;
                Console.WriteLine("Original animation settings saved.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving original settings: {ex.Message}");
            }
        }

        private static bool GetBoolSetting(uint action)
        {
            bool value = false;
            SystemParametersInfo(action, 0, ref value, 0);
            return value;
        }

        private static void SetBoolSetting(uint action, bool value)
        {
            SystemParametersInfo(action, 0, new IntPtr(value ? 1 : 0), SPIF_SENDCHANGE);
        }

        #endregion
    }
}


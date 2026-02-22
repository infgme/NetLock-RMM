using System;
using Avalonia;
using Avalonia.Styling;

namespace NetLock_RMM_Relay_App;

/// <summary>
/// Manages application theme (Auto, Light, Dark)
/// </summary>
public class ThemeManager
{
    private static ThemeManager? _instance;
    public static ThemeManager Instance => _instance ??= new ThemeManager();
    
    private const string ThemeSettingKey = "theme_mode";
    
    public enum ThemeMode
    {
        Auto,
        Light,
        Dark
    }
    
    private ThemeMode _currentMode = ThemeMode.Auto;
    
    public ThemeMode CurrentMode
    {
        get => _currentMode;
        set
        {
            if (_currentMode != value)
            {
                _currentMode = value;
                Application_Settings.Handler.Set_Value(ThemeSettingKey, value.ToString());
                ApplyTheme();
            }
        }
    }
    
    private ThemeManager()
    {
        LoadThemePreference();
    }
    
    private void LoadThemePreference()
    {
        var savedTheme = Application_Settings.Handler.Get_Value(ThemeSettingKey);
        if (!string.IsNullOrEmpty(savedTheme) && Enum.TryParse<ThemeMode>(savedTheme, out var mode))
        {
            _currentMode = mode;
        }
        else
        {
            _currentMode = ThemeMode.Auto;
        }
    }
    
    public void Initialize()
    {
        ApplyTheme();
    }
    
    public void ApplyTheme()
    {
        var app = Application.Current;
        if (app == null) return;
        
        var targetVariant = GetEffectiveThemeVariant();
        app.RequestedThemeVariant = targetVariant;
    }
    
    private ThemeVariant GetEffectiveThemeVariant()
    {
        return _currentMode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Auto => DetectSystemTheme(),
            _ => ThemeVariant.Dark
        };
    }
    
    private ThemeVariant DetectSystemTheme()
    {
        try
        {
            // Try to detect system theme on Linux
            if (OperatingSystem.IsLinux())
            {
                // Check GNOME/GTK theme
                var gtkTheme = Environment.GetEnvironmentVariable("GTK_THEME");
                if (!string.IsNullOrEmpty(gtkTheme) && gtkTheme.Contains("dark", StringComparison.OrdinalIgnoreCase))
                {
                    return ThemeVariant.Dark;
                }
                
                // Check KDE theme
                var kdeColorScheme = Environment.GetEnvironmentVariable("KDE_SESSION_VERSION");
                if (!string.IsNullOrEmpty(kdeColorScheme))
                {
                    // KDE uses dark theme by default in many configurations
                    return ThemeVariant.Dark;
                }
            }
            // On Windows, Avalonia can detect system theme automatically
            else if (OperatingSystem.IsWindows())
            {
                // Let Avalonia handle Windows theme detection
                return ThemeVariant.Default;
            }
            // On macOS, Avalonia can detect system theme automatically
            else if (OperatingSystem.IsMacOS())
            {
                return ThemeVariant.Default;
            }
        }
        catch (Exception ex)
        {
            
        }
        
        // Default to Dark theme
        return ThemeVariant.Dark;
    }
    
    public string GetThemeDisplayName()
    {
        return _currentMode switch
        {
            ThemeMode.Auto => "🌓 Auto",
            ThemeMode.Light => "☀️ Light",
            ThemeMode.Dark => "🌙 Dark",
            _ => "Unknown"
        };
    }
}


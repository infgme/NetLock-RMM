using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using Global.Helper;
using NetLock_RMM_Relay_App.Windows;

namespace NetLock_RMM_Relay_App
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            
            // Initialize theme manager
            ThemeManager.Instance.Initialize();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            try
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    // Show login window on startup
                    desktop.MainWindow = new LoginWindow();
                    
                    Logging.Debug("App", "OnFrameworkInitializationCompleted", 
                        "NetLock RMM Relay Proxy Client started");
                }
                
                base.OnFrameworkInitializationCompleted();
            }
            catch (Exception ex)
            {
                Logging.Error("App", "OnFrameworkInitializationCompleted", ex.ToString());
            }
        }
    }
}


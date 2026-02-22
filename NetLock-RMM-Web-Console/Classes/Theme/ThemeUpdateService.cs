namespace NetLock_RMM_Web_Console.Classes.Theme
{
    public class ThemeUpdateService
    {
        public event Action? OnThemeUpdated;

        public void NotifyThemeUpdated()
        {
            OnThemeUpdated?.Invoke();
        }
    }
}


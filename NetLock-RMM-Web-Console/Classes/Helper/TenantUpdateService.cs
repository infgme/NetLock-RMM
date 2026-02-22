namespace NetLock_RMM_Web_Console.Classes.Helper
{
    public class TenantUpdateService
    {
        public event Action? OnTenantUpdated;

        public void NotifyTenantUpdated()
        {
            OnTenantUpdated?.Invoke();
        }
    }
}


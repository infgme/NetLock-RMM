namespace NetLock_RMM_Server.Configuration
{
    public class Server
    {
        public static string agent_version = String.Empty;
        public static bool isDocker = false;
        public static bool loggingEnabled = true;
        public static DateTime serverStartTime = DateTime.Now;
        public static string public_override_url = String.Empty;

        public static int relay_port = 0;
        
        // Relay Server RSA Public Key
        public static string relay_public_key_pem = String.Empty;
        public static string relay_public_key_fingerprint = String.Empty;
        
        // Relay Server TLS Configuration (optional - for direct exposure without reverse proxy)
        public static bool relay_use_tls = false;
        public static string relay_cert_path = String.Empty;
        public static string relay_cert_password = String.Empty;
        
        // Agent Updates - Concurrent Downloads
        public static int MaxConcurrentAgentUpdates = 5;
        public static SemaphoreSlim MaxConcurrentNetLockPackageDownloadsSemaphore = new SemaphoreSlim(5, 5);
    }
}

namespace NetLock_RMM_Web_Console.Classes.Security;

/// <summary>
/// General, configurable Rate Limiter.
/// The global instance is shared across all pages (Login etc.).
/// </summary>
public class RateLimiter
{
    /// <summary>
    /// Global instance: 10 attempts per 60 minutes, shared counter across all pages.
    /// </summary>
    public static RateLimiter Instance { get; } = new(maxAttempts: 10, windowMinutes: 60);

    private readonly Dictionary<string, (int FailedAttempts, DateTime WindowStart)> _attempts = new();
    private readonly object _lock = new();
    
    private readonly int _maxAttempts;
    private readonly int _windowMinutes;
    private readonly int _cleanupIntervalMinutes;
    
    private DateTime _lastCleanup = DateTime.UtcNow;

    /// <summary>
    /// Creates a new RateLimiter with configurable parameters.
    /// </summary>
    /// <param name="maxAttempts">Maximum number of attempts per time window</param>
    /// <param name="windowMinutes">Time window in minutes</param>
    /// <param name="cleanupIntervalMinutes">Interval in minutes for cleaning up old entries (default: 10)</param>
    public RateLimiter(int maxAttempts, int windowMinutes, int cleanupIntervalMinutes = 10)
    {
        _maxAttempts = maxAttempts;
        _windowMinutes = windowMinutes;
        _cleanupIntervalMinutes = cleanupIntervalMinutes;
    }

    /// <summary>
    /// Checks if the IP address still has attempts remaining.
    /// </summary>
    /// <param name="ipAddress">The user's IP address</param>
    /// <returns>True if allowed, False if blocked</returns>
    public bool IsAllowed(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            return true;

        lock (_lock)
        {
            CleanupExpiredEntries();

            if (_attempts.TryGetValue(ipAddress, out var tracking))
            {
                var now = DateTime.UtcNow;
                
                // Time window expired? Start a new window
                if ((now - tracking.WindowStart).TotalMinutes >= _windowMinutes)
                {
                    _attempts[ipAddress] = (0, now);
                    return true;
                }

                // Too many attempts in the current time window?
                if (tracking.FailedAttempts >= _maxAttempts)
                {
                    return false;
                }

                return true;
            }

            // First request from this IP
            return true;
        }
    }

    /// <summary>
    /// Records a failed attempt.
    /// </summary>
    /// <param name="ipAddress">The user's IP address</param>
    public void RecordFailedAttempt(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            return;

        lock (_lock)
        {
            var now = DateTime.UtcNow;

            if (_attempts.TryGetValue(ipAddress, out var tracking))
            {
                // Time window expired? Start a new window
                if ((now - tracking.WindowStart).TotalMinutes >= _windowMinutes)
                {
                    _attempts[ipAddress] = (1, now);
                }
                else
                {
                    _attempts[ipAddress] = (tracking.FailedAttempts + 1, tracking.WindowStart);
                }
            }
            else
            {
                _attempts[ipAddress] = (1, now);
            }
        }
    }

    /// <summary>
    /// Resets the failed attempts for an IP address.
    /// </summary>
    /// <param name="ipAddress">The user's IP address</param>
    public void ResetAttempts(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            return;

        lock (_lock)
        {
            _attempts.Remove(ipAddress);
        }
    }

    /// <summary>
    /// Returns the number of remaining attempts.
    /// </summary>
    /// <param name="ipAddress">The user's IP address</param>
    /// <returns>Number of remaining attempts</returns>
    public int GetRemainingAttempts(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            return _maxAttempts;

        lock (_lock)
        {
            if (_attempts.TryGetValue(ipAddress, out var tracking))
            {
                var now = DateTime.UtcNow;
                
                if ((now - tracking.WindowStart).TotalMinutes >= _windowMinutes)
                {
                    return _maxAttempts;
                }

                return Math.Max(0, _maxAttempts - tracking.FailedAttempts);
            }

            return _maxAttempts;
        }
    }

    /// <summary>
    /// Returns the time in minutes until the block is lifted.
    /// </summary>
    /// <param name="ipAddress">The user's IP address</param>
    /// <returns>Minutes until unblock, or 0 if not blocked</returns>
    public int GetMinutesUntilUnblock(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            return 0;

        lock (_lock)
        {
            if (_attempts.TryGetValue(ipAddress, out var tracking))
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - tracking.WindowStart).TotalMinutes;
                
                if (elapsed >= _windowMinutes)
                    return 0;

                if (tracking.FailedAttempts >= _maxAttempts)
                {
                    return (int)Math.Ceiling(_windowMinutes - elapsed);
                }
            }

            return 0;
        }
    }

    private void CleanupExpiredEntries()
    {
        if ((DateTime.UtcNow - _lastCleanup).TotalMinutes < _cleanupIntervalMinutes)
            return;

        var now = DateTime.UtcNow;
        
        var expiredIps = _attempts
            .Where(kvp => (now - kvp.Value.WindowStart).TotalMinutes >= _windowMinutes)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var ip in expiredIps)
        {
            _attempts.Remove(ip);
        }

        _lastCleanup = now;
    }
}


namespace NetLock_RMM_Server.Endpoints;

public static class Relay_Server
{
    public static void MapRelayEndpoints(this WebApplication app)
    {
        #region Web Console & Relay App Share
        
        // Create Relay Session
        app.MapPost("/admin/relay/create", async (HttpContext context) =>
        {
            await NetLock_RMM_Server.Relay.Handler.CreateRelaySession(context);
        });

        // Close Relay Session
        app.MapPost("/admin/relay/close", async (HttpContext context) =>
        {
            await NetLock_RMM_Server.Relay.Handler.CloseRelaySession(context);
        });

        // List Active Relay Sessions
        app.MapGet("/admin/relay/list", async (HttpContext context) =>
        {
            await NetLock_RMM_Server.Relay.Handler.ListRelaySessions(context);
        });

        // Get Relay Session Info
        app.MapGet("/admin/relay/info", async (HttpContext context) =>
        {
            await NetLock_RMM_Server.Relay.Handler.GetRelaySessionInfo(context);
        });
        
        #endregion
        
        #region Relay App
    
        // Register Relay Admin Client
        app.MapPost("/admin/relay/register", async (HttpContext context) =>
        {
            await NetLock_RMM_Server.Relay.Handler.RegisterRelayAdminClient(context);
        });
    
        // Announce Admin Connection (HTTP verification before TCP connection)
        app.MapPost("/admin/relay/announce", async (HttpContext context) =>
        {
            await NetLock_RMM_Server.Relay.Handler.AnnounceAdminConnection(context);
        });
    
        // Disconnect Admin from Session (clean cleanup when relay app closes connection)
        app.MapPost("/admin/relay/disconnect", async (HttpContext context) =>
        {
            await NetLock_RMM_Server.Relay.Handler.DisconnectAdminFromSession(context);
        });
    
        // Get Relay Server Public Key
        app.MapGet("/relay/public-key", async (HttpContext context) =>
        {
            await NetLock_RMM_Server.Relay.Handler.GetRelayPublicKey(context);
        });
    
        // Rotate Relay RSA Keys (admin only)
        app.MapPost("/admin/relay/rotate-keys", async (HttpContext context) =>
        {
            await NetLock_RMM_Server.Relay.Handler.RotateRelayKeys(context);
        });
    
        // Get Session Kick Status (status polling for relay app)
        app.MapPost("/admin/relay/session/status", async (HttpContext context) =>
        {
            await NetLock_RMM_Server.Relay.Handler.GetSessionKickStatus(context);
        });
    
        #endregion
    }
}
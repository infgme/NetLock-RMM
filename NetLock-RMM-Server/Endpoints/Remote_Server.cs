using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Primitives;
using NetLock_RMM_Server.Agent.Windows;
using NetLock_RMM_Server.SignalR;

namespace NetLock_RMM_Server.Endpoints;

public static class Remote_Server
{
    public static void MapRemoteServerEndpoints(this WebApplication app)
    {
        // Temporary endpoint to bridge the remote control, due to issues related with signalr causing instability on client side
        app.MapPost("/Agent/Windows/Remote/Command", async (HttpContext context, IHubContext<CommandHub> hubContext) =>
        {
            try
            {
                Logging.Handler.Debug("POST Request Mapping", "/Agent/Windows/Remote/Command", "Request received.");

                // Add headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'");

                // Get the remote IP address from the X-Forwarded-For header
                string ip_address_external = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue) 
                    ? headerValue.ToString() 
                    : context.Connection.RemoteIpAddress.ToString();

                // Verify package guid
                bool hasPackageGuid = context.Request.Headers.TryGetValue("Package_Guid", out StringValues package_guid) || context.Request.Headers.TryGetValue("Package-Guid", out package_guid);

                if (!hasPackageGuid)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }

                bool package_guid_status = await Authentification.Verify_NetLock_Package_Configurations_Guid(package_guid);
                if (!package_guid_status)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }

                string json;
                string responseId = string.Empty;
                byte[] screenshotBytes = null;

                // Check if request is multipart/form-data (for binary screenshots)
                if (context.Request.HasFormContentType)
                {
                    var form = await context.Request.ReadFormAsync();

                    // Extract device_identity JSON from form
                    json = form["device_identity"].ToString();

                    // Extract response_id
                    responseId = form["response_id"].ToString();

                    // Extract binary screenshot data
                    var screenshotFile = form.Files["screenshot"];
                    if (screenshotFile != null && screenshotFile.Length > 0)
                    {
                        using (var memoryStream = new MemoryStream())
                        {
                            await screenshotFile.CopyToAsync(memoryStream);
                            screenshotBytes = memoryStream.ToArray();
                        }
                    }
                }
                else // Handle JSON requests (old behavior)
                {
                    using (StreamReader reader = new StreamReader(context.Request.Body))
                    {
                        json = await reader.ReadToEndAsync() ?? string.Empty;
                    }

                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        JsonElement root = document.RootElement;
                        JsonElement remoteControlElement = root.GetProperty("remote_control");

                        responseId = remoteControlElement.GetProperty("response_id").GetString();
                    }
                }

                // Verify the device
                string device_status = await Authentification.Verify_Device(json, ip_address_external, false);

                if (device_status == "authorized" || device_status == "synced" || device_status == "not_synced")
                {
                    string admin_identity_info_json = CommandHubSingleton.Instance.GetAdminIdentity(responseId);
                    string admin_client_id = string.Empty;

                    using (JsonDocument document = JsonDocument.Parse(admin_identity_info_json))
                    {
                        JsonElement admin_client_id_element = document.RootElement.GetProperty("admin_client_id");
                        admin_client_id = admin_client_id_element.ToString();
                    }

                    // Send screenshot as byte array directly
                    await CommandHubSingleton.Instance.HubContext.Clients.Client(admin_client_id)
                        .SendAsync("ReceiveClientResponseRemoteControlScreenCapture", screenshotBytes);

                    CommandHubSingleton.Instance.RemoveAdminCommand(responseId);

                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync("ok");
                }
                else
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync(device_status);
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("POST Request Mapping", "/Agent/Windows/Remote/Command", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("Invalid request.");
            }
        });
    }
}
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using NetLock_RMM_Server.Agent.Windows;

namespace NetLock_RMM_Server.Endpoints;

public static class Communication_Server
{
    public static void MapCommServerEndpoints(this WebApplication app)
    {
        // Check Agent Version
        app.MapPost("/Agent/Windows/Check_Version", async context =>
        {
            try
            {
                Logging.Handler.Debug("POST Request Mapping", "/Agent/Windows/Check_Version", "Request received.");

                // Add headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'"); // protect against XSS 

                // Get the remote IP address
                string ip_address_external = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue) ? headerValue.ToString() : context.Features.Get<IHttpConnectionFeature>()?.RemoteIpAddress?.ToString();

                // Verify package guid
                bool hasPackageGuid = context.Request.Headers.TryGetValue("Package_Guid", out StringValues package_guid) || context.Request.Headers.TryGetValue("Package-Guid", out package_guid);

                if (hasPackageGuid == false)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }
                else
                {
                    bool package_guid_status = await Authentification.Verify_NetLock_Package_Configurations_Guid(package_guid);

                    if (package_guid_status == false)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized.");
                        return;
                    }
                }

                // Read the JSON data
                string json;
                using (StreamReader reader = new StreamReader(context.Request.Body))
                {
                    json = await reader.ReadToEndAsync() ?? string.Empty;
                }

                // Check the version of the device
                string version_status = await Version_Handler.Check_Version(json);

                // Return the device status
                context.Response.StatusCode = 200;
                await context.Response.WriteAsync(version_status);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                Logging.Handler.Error("POST Request Mapping", "/Agent/Windows/Check_Version", ex.ToString());
                await context.Response.WriteAsync("Invalid request.");
            }
        });
        
        //Verify Device
        app.MapPost("/Agent/Windows/Verify_Device", async context =>
        {
            try
            {
                Logging.Handler.Debug("POST Request Mapping", "/Agent/Windows/Verify_Device", "Request received.");

                // Add headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'"); // protect against XSS 

                // Get the remote IP address from the X-Forwarded-For header
                string ip_address_external = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue) ? headerValue.ToString() : context.Connection.RemoteIpAddress.ToString();

                // Verify package guid
                bool hasPackageGuid = context.Request.Headers.TryGetValue("Package_Guid", out StringValues package_guid) || context.Request.Headers.TryGetValue("Package-Guid", out package_guid);

                if (hasPackageGuid == false)
                {
                    Logging.Handler.Debug("POST Request Mapping", "/Agent/Windows/Verify_Device", "No guid provided. Unauthorized.");
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }
                else
                {
                    bool package_guid_status = await Authentification.Verify_NetLock_Package_Configurations_Guid(package_guid);

                    if (package_guid_status == false)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized.");
                        return;
                    }
                }

                // Read the JSON data
                string json;
                using (StreamReader reader = new StreamReader(context.Request.Body))
                {
                    json = await reader.ReadToEndAsync() ?? string.Empty;
                }

                // Verify the device
                string device_status = await Authentification.Verify_Device(json, ip_address_external, true);

                await context.Response.WriteAsync(device_status);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("POST Request Mapping", "/Agent/Windows/Verify_Device", ex.ToString());

                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("Invalid request.");
            }
        });
        
        //Update device information
        app.MapPost("/Agent/Windows/Update_Device_Information", async context =>
        {
            try
            {
                Logging.Handler.Debug("POST Request Mapping", "/Agent/Windows/Update_Device_Information", "Request received.");

                // Add headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'"); // protect against XSS 

                // Get the remote IP address from the X-Forwarded-For header
                string ip_address_external = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue) ? headerValue.ToString() : context.Connection.RemoteIpAddress.ToString();

                // Verify package guid
                bool hasPackageGuid = context.Request.Headers.TryGetValue("Package_Guid", out StringValues package_guid) || context.Request.Headers.TryGetValue("Package-Guid", out package_guid);

                if (hasPackageGuid == false)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }
                else
                {
                    bool package_guid_status = await Authentification.Verify_NetLock_Package_Configurations_Guid(package_guid);

                    if (package_guid_status == false)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized.");
                        return;
                    }
                }

                // Read the JSON data
                string json;
                using (StreamReader reader = new StreamReader(context.Request.Body))
                {
                    json = await reader.ReadToEndAsync() ?? string.Empty;
                }

                // Verify the device
                string device_status = await Authentification.Verify_Device(json, ip_address_external, true);

                // Check if the device is authorized, synced or not synced. If so, update the device information
                if (device_status == "authorized" || device_status == "synced" || device_status == "not_synced")
                {
                    await Device_Handler.Update_Device_Information(json);
                    context.Response.StatusCode = 200;
                }
                else
                {
                    context.Response.StatusCode = 403;
                }

                await context.Response.WriteAsync(device_status);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("POST Request Mapping", "/Agent/Windows/Update_Device_Information", ex.Message);

                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("Invalid request.");
            }
        });
        
        //Insert events
        app.MapPost("/Agent/Windows/Events", async context =>
        {
            try
            {
                Logging.Handler.Debug("POST Request Mapping", "/Agent/Windows/Events", "Request received.");

                // Add headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'"); // protect against XSS 

                // Get the remote IP address from the X-Forwarded-For header
                string ip_address_external = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue) ? headerValue.ToString() : context.Connection.RemoteIpAddress.ToString();

                // Verify package guid
                bool hasPackageGuid = context.Request.Headers.TryGetValue("Package_Guid", out StringValues package_guid) || context.Request.Headers.TryGetValue("Package-Guid", out package_guid);

                if (hasPackageGuid == false)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }
                else
                {
                    bool package_guid_status = await Authentification.Verify_NetLock_Package_Configurations_Guid(package_guid);

                    if (package_guid_status == false)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized.");
                        return;
                    }
                }

                // Read the JSON data
                string json;
                using (StreamReader reader = new StreamReader(context.Request.Body))
                {
                    json = await reader.ReadToEndAsync() ?? string.Empty;
                }

                // Verify the device
                string device_status = await Authentification.Verify_Device(json, ip_address_external, false);

                // Check if the device is authorized. If so, consume the events
                if (device_status == "authorized" || device_status == "synced" || device_status == "not_synced")
                {
                    device_status = await Event_Handler.Consume(json);
                    context.Response.StatusCode = 200;
                }
                else
                {
                    context.Response.StatusCode = 403;
                }

                await context.Response.WriteAsync(device_status);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("POST Request Mapping", "/Agent/Windows/Events", ex.Message);

                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("Invalid request.");
            }
        });
        
        //Get policy
        app.MapPost("/Agent/Windows/Policy", async context =>
        {
            try
            {
                Logging.Handler.Debug("POST Request Mapping", "/Agent/Windows/Policy", "Request received.");

                // Add headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'"); // protect against XSS 

                // Get the remote IP address from the X-Forwarded-For header
                string ip_address_external = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue) ? headerValue.ToString() : context.Connection.RemoteIpAddress.ToString();

                // Verify package guid
                bool hasPackageGuid = context.Request.Headers.TryGetValue("Package_Guid", out StringValues package_guid) || context.Request.Headers.TryGetValue("Package-Guid", out package_guid);

                if (hasPackageGuid == false)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }
                else
                {
                    bool package_guid_status = await Authentification.Verify_NetLock_Package_Configurations_Guid(package_guid);

                    if (package_guid_status == false)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized.");
                        return;
                    }
                }

                // Read the JSON data
                string json;
                using (StreamReader reader = new StreamReader(context.Request.Body))
                {
                    json = await reader.ReadToEndAsync() ?? string.Empty;
                }

                // Verify the device
                string device_status = await Authentification.Verify_Device(json, ip_address_external, true);

                string device_policy_json = string.Empty;

                // Check if the device is authorized, synced, or not synced. If so, get the policy
                if (device_status == "authorized" || device_status == "synced" || device_status == "not_synced")
                {
                    device_policy_json = await Policy_Handler.Get_Policy(json, ip_address_external);
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync(device_policy_json);
                }
                else // If the device is not authorized, return the device status as unauthorized
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync(device_status);
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("POST Request Mapping", "/Agent/Windows/Policy", ex.ToString());

                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("Invalid request.");
            }
        });
    }
}
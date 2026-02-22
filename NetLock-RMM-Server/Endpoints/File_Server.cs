using System.Text.Json;
using Microsoft.Extensions.Primitives;
using NetLock_RMM_Server.Agent.Windows;

namespace NetLock_RMM_Server.Endpoints;

public static class File_Server
{
    public static void MapFileServerEndpoints(this WebApplication app)
    {
        app.MapGet("/admin/devices/connected", async (HttpContext context) =>
        {
            try
            {
                Logging.Handler.Debug("/admin/devices/connected", "Request received.", "");

                // Add security header
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'");

                // Determine external IP address (if available)
                string ipAddressExternal = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue)
                    ? headerValue.ToString()
                    : context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

                // Verify API key
                if (!context.Request.Headers.TryGetValue("x-api-key", out StringValues apiKey) || !await NetLock_RMM_Server.Files.Handler.Verify_Api_Key(apiKey))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }
                
                // Get connected devices access keys from CommandHubSingleton
                var connectedAccessKeys = new List<string>();
                foreach (var identityJson in NetLock_RMM_Server.SignalR.CommandHubSingleton.Instance._clientConnections.Values)
                {
                    try
                    {
                        var identityDoc = JsonDocument.Parse(identityJson);
                        if (identityDoc.RootElement.TryGetProperty("device_identity", out var deviceIdentity) &&
                            deviceIdentity.TryGetProperty("access_key", out var accessKeyElement))
                        {
                            string accessKey = accessKeyElement.GetString();
                            if (!string.IsNullOrEmpty(accessKey) && !connectedAccessKeys.Contains(accessKey))
                            {
                                connectedAccessKeys.Add(accessKey);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logging.Handler.Error("/admin/devices/connected", "Failed to parse identity JSON", ex.ToString());
                    }
                }

                // Create JSON response
                var jsonObject = new { access_keys = connectedAccessKeys };
                string json = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
                Logging.Handler.Debug("/admin/devices/connected", "Connected devices", json);

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(json);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("/admin/devices/connected/index", "General error", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("An error occurred while processing the request.");
            }
        });
        
        app.MapPost("/admin/create_installer", async (HttpContext context) =>
        {
            try
            {
                Logging.Handler.Debug("/admin/create_installer", "Request received.", "");

                // Add security headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'");

                // Get api key | is not required
                bool hasApiKey = context.Request.Headers.TryGetValue("x-api-key", out StringValues files_api_key);

                // Verify API key
                if (!hasApiKey || !await NetLock_RMM_Server.Files.Handler.Verify_Api_Key(files_api_key))
                {
                    Logging.Handler.Debug("/admin/create_installer", "Missing or invalid API key.", "");
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }

                // Read the JSON data
                string body;
                using (StreamReader reader = new StreamReader(context.Request.Body))
                {
                    body = await reader.ReadToEndAsync() ?? string.Empty;
                }

                // Extract the name and the JSON data
                // Deserialisierung des gesamten JSON-Strings
                string name = String.Empty;
                string server_config = String.Empty;

                using (JsonDocument document = JsonDocument.Parse(body))
                {
                    JsonElement name_element = document.RootElement.GetProperty("name");
                    name = name_element.ToString();

                    JsonElement json_element = document.RootElement.GetProperty("server_config");
                    server_config = json_element.ToString();
                }

                // Create installer file
                string result = await NetLock_RMM_Server.Files.Handler.Create_Custom_Installer(name, server_config);

                // Return the result as a JSON string
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(result);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("/admin/create_installer", "General error", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("1"); // something went wrong
            }
        });
        
        // NetLock admin files device upload
        app.MapPost("/admin/files/upload/device", async (HttpContext context) =>
        {
            try
            {
                Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Request received.");

                // Add headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'"); // protect against XSS 

                // Get the remote IP address from the X-Forwarded-For header
                string ip_address_external = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue) ? headerValue.ToString() : context.Connection.RemoteIpAddress.ToString();

                // Verify package guid
                bool hasPackageGuid = context.Request.Headers.TryGetValue("Package_Guid", out StringValues package_guid) || context.Request.Headers.TryGetValue("Package-Guid", out package_guid);

                Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "hasGuid: " + hasPackageGuid.ToString());
                Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Package guid: " + package_guid.ToString());

                if (hasPackageGuid == false)
                {
                    Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "No guid provided. Unauthorized.");

                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }
                else
                {
                    bool package_guid_status = await Authentification.Verify_NetLock_Package_Configurations_Guid(package_guid);

                    Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Package guid status: " + package_guid_status.ToString());

                    if (package_guid_status == false)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized.");
                        return;
                    }
                }

                // Query parameters
                string tenant_guid = context.Request.Query["tenant_guid"].ToString();
                string location_guid = context.Request.Query["location_guid"].ToString();
                string device_name = context.Request.Query["device_name"].ToString();
                string access_key = context.Request.Query["access_key"].ToString();
                string hwid = context.Request.Query["hwid"].ToString();

                if (String.IsNullOrEmpty(tenant_guid) || String.IsNullOrEmpty(location_guid) || String.IsNullOrEmpty(device_name) || String.IsNullOrEmpty(access_key) || String.IsNullOrEmpty(hwid))
                {
                    Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Invalid request.");
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Invalid request.");
                    return;
                }

                // Build a device identity JSON object with nested "device_identity" object
                string device_identity_json = "{ \"device_identity\": { " +
                                              "\"tenant_guid\": \"" + tenant_guid + "\"," +
                                              "\"location_guid\": \"" + location_guid + "\"," +
                                              "\"device_name\": \"" + device_name + "\"," +
                                              "\"access_key\": \"" + access_key + "\"," +
                                              "\"hwid\": \"" + hwid + "\"" +
                                              "} }";

                // Verify the device
                string device_status = await Authentification.Verify_Device(device_identity_json, ip_address_external, false);

                Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Device status: " + device_status);

                // Check if the device is authorized, synced, or not synced. If so, get the file from the database
                if (device_status == "authorized" || device_status == "synced" || device_status == "not_synced")
                {
                    // Check if the request contains a file
                    if (!context.Request.HasFormContentType)
                    {
                        Logging.Handler.Debug("/admin/files/upload/device", "Invalid request: No form content type.", "");
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsync("Invalid request. No file uploaded #1.");
                        return;
                    }

                    var form = await context.Request.ReadFormAsync();
                    var file = form.Files.FirstOrDefault();
                    if (file == null || file.Length == 0)
                    {
                        Logging.Handler.Debug("/admin/files/upload/device", "Invalid request: No file found in the form.", "");
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsync("Invalid request. No file uploaded #2.");
                        return;
                    }

                    // Ensure the upload directory exists
                    string directoryPath = Path.Combine(Application_Paths._private_files, "devices", tenant_guid, location_guid, device_name, "downloaded");
                    if (!Directory.Exists(directoryPath))
                    {
                        Logging.Handler.Debug("/admin/files/upload/device", "Creating directory: " + directoryPath, "");
                        Directory.CreateDirectory(directoryPath);
                    }

                    Logging.Handler.Debug("/admin/files/upload/device", "Uploading file: " + file.FileName, "");

                    // Set the file path
                    var filePath = Path.Combine(directoryPath, file.FileName);
                    Logging.Handler.Debug("/admin/files/upload/device", "File Path", filePath);

                    // Save the file
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }

                    Logging.Handler.Debug("/admin/files/upload/device", "File uploaded successfully: " + file.FileName, "");

                    // Register the file with the correct directory path (excluding file name)
                    string register_json = await NetLock_RMM_Server.Files.Handler.Register_File(filePath, tenant_guid, location_guid, device_name);

                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync(register_json);
                }
                else // If the device is not authorized, return the device status as unauthorized
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync(device_status);
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("/admin/files/download/device", "General error", ex.ToString());

                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("An error occurred while downloading the file.");
            }
        });
        
        // NetLock admin files device download
        app.MapGet("/admin/files/download/device", async (HttpContext context) =>
        {
            try
            {
                Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Request received.");

                // Add headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'"); // protect against XSS 

                // Get the remote IP address from the X-Forwarded-For header
                string ip_address_external = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue) ? headerValue.ToString() : context.Connection.RemoteIpAddress.ToString();

                // Verify package guid
                bool hasPackageGuid = context.Request.Headers.TryGetValue("Package_Guid", out StringValues package_guid) || context.Request.Headers.TryGetValue("Package-Guid", out package_guid);

                Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "hasGuid: " + hasPackageGuid.ToString());
                Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Package guid: " + package_guid.ToString());

                if (hasPackageGuid == false)
                {
                    Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "No guid provided. Unauthorized.");

                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }
                else
                {
                    bool package_guid_status = await Authentification.Verify_NetLock_Package_Configurations_Guid(package_guid);

                    Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Package guid status: " + package_guid_status.ToString());

                    if (package_guid_status == false)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized.");
                        return;
                    }
                }

                // Query parameters
                string guid = context.Request.Query["guid"].ToString();
                string tenant_guid = context.Request.Query["tenant_guid"].ToString();
                string location_guid = context.Request.Query["location_guid"].ToString();
                string device_name = context.Request.Query["device_name"].ToString();
                string access_key = context.Request.Query["access_key"].ToString();
                string hwid = context.Request.Query["hwid"].ToString();

                if (String.IsNullOrEmpty(guid) || String.IsNullOrEmpty(tenant_guid) || String.IsNullOrEmpty(location_guid) || String.IsNullOrEmpty(device_name) || String.IsNullOrEmpty(access_key) || String.IsNullOrEmpty(hwid))
                {
                    Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Invalid request.");
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Invalid request.");
                    return;
                }

                // Build a device identity JSON object with nested "device_identity" object
                string device_identity_json = "{ \"device_identity\": { " +
                                              "\"tenant_guid\": \"" + tenant_guid + "\"," +
                                              "\"location_guid\": \"" + location_guid + "\"," +
                                              "\"device_name\": \"" + device_name + "\"," +
                                              "\"access_key\": \"" + access_key + "\"," +
                                              "\"hwid\": \"" + hwid + "\"" +
                                              "} }";

                // Verify the device
                string device_status = await Authentification.Verify_Device(device_identity_json, ip_address_external, false);

                Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Device status: " + device_status);

                // Check if the device is authorized, synced, or not synced. If so, get the file from the database
                if (device_status == "authorized" || device_status == "synced" || device_status == "not_synced")
                {
                    // Get the file path by GUID
                    bool file_access = await NetLock_RMM_Server.Files.Handler.Verify_Device_File_Access(tenant_guid, location_guid, device_name, guid);

                    Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "File access: " + file_access.ToString());

                    if (file_access == false)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("Unauthorized.");
                        return;
                    }
                    else
                    {
                        string file_path = await NetLock_RMM_Server.Files.Handler.Get_File_Path_By_GUID(guid);
                        string server_path = Path.Combine(Application_Paths._private_files_admin_db_friendly, file_path);

                        Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "Server path: " + server_path);

                        if (!File.Exists(server_path))
                        {
                            Logging.Handler.Debug("/admin/files/download/device", "File not found", server_path);
                            context.Response.StatusCode = 404;
                            await context.Response.WriteAsync("File not found.");
                            return;
                        }

                        string file_name = Path.GetFileName(server_path);

                        Logging.Handler.Debug("Get Request Mapping", "/admin/files/download/device", "File name: " + file_name);

                        using (var fileStream = new FileStream(server_path, FileMode.Open, FileAccess.Read))
                        {
                            context.Response.StatusCode = 200;
                            context.Response.ContentType = "application/octet-stream";
                            context.Response.Headers.Add("Content-Disposition", $"attachment; filename={file_name}");

                            // Stream directly to the Response.body
                            await fileStream.CopyToAsync(context.Response.Body);
                        }
                    }
                }
                else // If the device is not authorized, return the device status as unauthorized
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync(device_status);
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("/admin/files/download/device", "General error", ex.ToString());

                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("An error occurred while downloading the file.");
            }
        });
        
        app.MapGet("/admin/files/download", async (HttpContext context) =>
        {
            try
            {
                Logging.Handler.Debug("/admin/files/download", "Request received.", "");

                // Add security headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'");

                // Get api key | is not required
                bool hasApiKey = context.Request.Headers.TryGetValue("x-api-key", out StringValues files_api_key);

                // Query parameters
                string guid = context.Request.Query["guid"].ToString();
                string password = context.Request.Query["password"].ToString();

                // Get guid
                guid = Uri.UnescapeDataString(guid);

                // Handle the case when password is null or empty
                password = password != null ? Uri.UnescapeDataString(password) : string.Empty;

                bool hasAccess = await NetLock_RMM_Server.Files.Handler.Verify_File_Access(guid, password, files_api_key); // api key is not required

                if (!hasAccess)
                {
                    Logging.Handler.Debug("/admin/files/download", "Unauthorized.", "");
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }

                string file_path = await NetLock_RMM_Server.Files.Handler.Get_File_Path_By_GUID(guid);
                string server_path = Path.Combine(Application_Paths._private_files_admin_db_friendly, file_path);

                string file_name = Path.GetFileName(server_path);

                using (var fileStream = new FileStream(server_path, FileMode.Open, FileAccess.Read))
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.Headers.Add("Content-Disposition", $"attachment; filename={file_name}");

                    // Stream directly to the Response.body
                    await fileStream.CopyToAsync(context.Response.Body);
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("/admin/files/download", "General error", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("1"); // something went wrong
            }
        });
        
        app.MapPost("/admin/files/upload/{path}", async (HttpContext context, string path) =>
        {
            try
            {
                Logging.Handler.Debug("/admin/files/upload", "Request received.", path);

                // Add security headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'");

                // Verify API key
                bool hasApiKey = context.Request.Headers.TryGetValue("x-api-key", out StringValues files_api_key);

                bool ApiKeyValid = await NetLock_RMM_Server.Files.Handler.Verify_Api_Key(files_api_key);

                if (!hasApiKey || !ApiKeyValid)
                {
                    Logging.Handler.Debug("/admin/files/upload", "Missing or invalid API key.", "");
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }

                // Query-String-Parameter extrahieren
                var tenant_guid = context.Request.Query["tenant_guid"].ToString();
                var location_guid = context.Request.Query["location_guid"].ToString();
                var device_name = context.Request.Query["device_name"].ToString();

                // Check if the request contains a file
                if (!context.Request.HasFormContentType)
                {
                    Logging.Handler.Debug("/admin/files/upload", "Invalid request: No form content type.", "");
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Invalid request. No file uploaded #1.");
                    return;
                }

                var form = await context.Request.ReadFormAsync();
                var file = form.Files.FirstOrDefault();
                if (file == null || file.Length == 0)
                {
                    Logging.Handler.Debug("/admin/files/upload", "Invalid request: No file found in the form.", "");
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Invalid request. No file uploaded #2.");
                    return;
                }

                // Decode the URL-encoded path and sanitize
                if (string.IsNullOrEmpty(path) || path.Equals("base1337", StringComparison.OrdinalIgnoreCase))
                {
                    path = string.Empty;
                }
                else
                {
                    path = Uri.UnescapeDataString(path);
                }

                // Sanitize the path to prevent directory traversal attacks
                string safePath = Path.GetFullPath(Path.Combine(Application_Paths._private_files, path))
                    .Replace('\\', '/').TrimEnd('/');

                // Normalize the allowed base path
                string allowedPath = Path.GetFullPath(Application_Paths._private_files)
                    .Replace('\\', '/').TrimEnd('/');

                // Log for debugging
                Logging.Handler.Debug("/admin/files/upload", "Allowed Path", allowedPath);
                Logging.Handler.Debug("/admin/files/upload", "Sanitized Path", safePath);

                // Check if the sanitized path starts with the allowed base path
                if (!safePath.StartsWith(allowedPath, StringComparison.OrdinalIgnoreCase))
                {
                    Logging.Handler.Debug("/admin/files/upload", "Invalid path: Outside allowed directory.", "");
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Invalid path.");
                    return;
                }

                // Ensure the upload directory exists
                string directoryPath = Path.GetDirectoryName(safePath);
                if (!Directory.Exists(directoryPath))
                {
                    Logging.Handler.Debug("/admin/files/upload", "Creating directory: " + directoryPath, "");
                    Directory.CreateDirectory(directoryPath);
                }

                Logging.Handler.Debug("/admin/files/upload", "Uploading file: " + file.FileName, "");

                // Set the file path
                var filePath = Path.Combine(directoryPath, file.FileName);
                Logging.Handler.Debug("/admin/files/upload", "File Path", filePath);

                // Save the file
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                Logging.Handler.Debug("/admin/files/upload", "File uploaded successfully: " + file.FileName, "");

                // Register the file with the correct directory path (excluding file name)
                string register_json = await NetLock_RMM_Server.Files.Handler.Register_File(filePath, tenant_guid, location_guid, device_name);

                context.Response.StatusCode = 200;

                // Send back info json if api key is valid
                if (hasApiKey && ApiKeyValid)
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(register_json);
                }
                else // If the api key is invalid, just send a simple response
                {
                    await context.Response.WriteAsync("uploaded");
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("/admin/files/upload", "General error", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("1"); // something went wrong
            }
        });
        
        app.MapPost("/admin/files/command", async context =>
        {
            try
            {
                Logging.Handler.Debug("/admin/files/command", "Request received.", "");

                // Add security headers
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'");

                // Verify API key
                bool hasApiKey = context.Request.Headers.TryGetValue("x-api-key", out StringValues files_api_key);
                if (!hasApiKey || !await NetLock_RMM_Server.Files.Handler.Verify_Api_Key(files_api_key))
                {
                    Logging.Handler.Debug("/admin/files/command", "Missing or invalid API key.", "");
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }

                // Deserializing the JSON data (command, path)
                string json;

                using (StreamReader reader = new StreamReader(context.Request.Body))
                {
                    json = await reader.ReadToEndAsync() ?? string.Empty;
                }

                await NetLock_RMM_Server.Files.Handler.Command(json);

                context.Response.StatusCode = 200;
                await context.Response.WriteAsync("executed");
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("/admin/files/command", "General error", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("1"); // something went wrong
            }
        });
        
        app.MapPost("/admin/files/index/{path}", async (HttpContext context, string path) =>
        {
            try
            {
                Logging.Handler.Debug("/admin/files", "Request received.", path);

                // Add security header
                context.Response.Headers.Add("Content-Security-Policy", "default-src 'self'");

                // Determine external IP address (if available)
                string ipAddressExternal = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue)
                    ? headerValue.ToString()
                    : context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

                // Verify API key
                if (!context.Request.Headers.TryGetValue("x-api-key", out StringValues apiKey) || !await NetLock_RMM_Server.Files.Handler.Verify_Api_Key(apiKey))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("Unauthorized.");
                    return;
                }

                // Check whether the path is null or empty
                if (String.IsNullOrWhiteSpace(path))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Invalid request.");
                    return;
                }

                // Handle the special base path
                if (path.Equals("base1337", StringComparison.OrdinalIgnoreCase))
                {
                    path = String.Empty;
                }
                else
                {
                    // URL decoding and removal of possible unauthorised characters
                    path = Uri.UnescapeDataString(path);

                    // Prevent path traversal attacks by normalising the path
                    path = Path.GetFullPath(Path.Combine(Application_Paths._private_files, path));

                    if (!path.StartsWith(Application_Paths._private_files))
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsync("Invalid path.");
                        return;
                    }
                }
                
                // Check directory
                var fullPath = Path.Combine(Application_Paths._private_files, path);

                if (!Directory.Exists(fullPath))
                {
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("Directory not found.");
                    return;
                }

                // Retrieve directory contents
                var directoryTree = await Helper.IO.Get_Directory_Index(fullPath);

                //  Create json (directoryTree) & Application_Paths._private_files
                var jsonObject = new
                {
                    index = directoryTree,
                    server_path = Application_Paths._private_files
                };

                // Convert the object into a JSON string
                string json = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
                Logging.Handler.Debug("Online_Mode.Handler.Update_Device_Information", "json", json);

                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(json);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("/admin/files/index", "General error", ex.ToString());
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("An error occurred while processing the request.");
            }
        });
        
        app.MapGet("/public/downloads/{fileName}", async context =>
        {
            try
            {
                Logging.Handler.Debug("/public/downloads", "Request received.", "");

                var fileName = (string)context.Request.RouteValues["fileName"];
                var downloadPath = Application_Paths._public_downloads_user + "\\" + fileName;

                if (!File.Exists(downloadPath))
                {
                    Logging.Handler.Error("GET Request Mapping", "/public_download", "File not found: " + downloadPath);
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("File not found.");
                    return;
                }

                var memory = new MemoryStream();
                using (var stream = new FileStream(downloadPath, FileMode.Open))
                {
                    await stream.CopyToAsync(memory);
                }
                memory.Position = 0;

                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers.Add("Content-Disposition", $"attachment; filename={fileName}");
                await memory.CopyToAsync(context.Response.Body);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("GET Request Mapping", "/public_download", ex.ToString());

                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("An error occurred while downloading the file.");
            }
        });
    }
}
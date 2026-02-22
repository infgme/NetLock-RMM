using NetLock_RMM_Agent_Remote;
using Global.Helper;
using System.Collections.Generic;
using Windows.Helper.ScreenControl;
using _x101.HWID_System; // Hinzugefügt für List<T>

var builder = Host.CreateApplicationBuilder(args);

Console.WriteLine("Starting NetLock RMM Remote Agent");

// Check if debug mode
if (Logging.Check_Debug_Mode()) // debug_mode
{
    Console.WriteLine("Debug mode enabled");
    Global.Configuration.Agent.debug_mode = true;
}
else
    Console.WriteLine("Debug mode disabled");

// Read server_config.json
if (Convert.ToBoolean(Global.Initialization.Server_Config.Ssl())) // ssl
{
    Global.Configuration.Agent.ssl = true;
    Global.Configuration.Agent.http_https = "https://";
}
else
{
    Global.Configuration.Agent.ssl = false;
    Global.Configuration.Agent.http_https = "http://";
}

Global.Configuration.Agent.package_guid = Global.Initialization.Server_Config.Package_Guid();
Global.Configuration.Agent.communication_servers = Global.Initialization.Server_Config.Communication_Servers();
Global.Configuration.Agent.remote_servers = Global.Initialization.Server_Config.Remote_Servers();
Global.Configuration.Agent.update_servers = Global.Initialization.Server_Config.Update_Servers();
Global.Configuration.Agent.trust_servers = Global.Initialization.Server_Config.Trust_Servers();
Global.Configuration.Agent.file_servers = Global.Initialization.Server_Config.File_Servers();
Global.Configuration.Agent.relay_servers = Global.Initialization.Server_Config.Relay_Servers();
Global.Configuration.Agent.tenant_guid = Global.Initialization.Server_Config.Tenant_Guid();
Global.Configuration.Agent.location_guid = Global.Initialization.Server_Config.Location_Guid();
Global.Configuration.Agent.language = Global.Initialization.Server_Config.Language();
Global.Configuration.Agent.device_name = Environment.MachineName;

// Output platform config
Console.WriteLine($"Device Name: {Global.Configuration.Agent.device_name}");
Console.WriteLine($"Communication Servers: {Global.Configuration.Agent.communication_servers}");
Console.WriteLine($"Remote Servers: {Global.Configuration.Agent.remote_servers}");
Console.WriteLine($"Update Servers: {Global.Configuration.Agent.update_servers}");
Console.WriteLine($"Trust Servers: {Global.Configuration.Agent.trust_servers}");
Console.WriteLine($"File Servers: {Global.Configuration.Agent.file_servers}");
Console.WriteLine($"Relay Servers: {Global.Configuration.Agent.relay_servers}");

Console.WriteLine(Environment.NewLine);
Console.WriteLine("Detecting platform...");

if (OperatingSystem.IsWindows())
{
    Console.WriteLine("Windows platform detected");
    Logging.Debug("Program.cs", "Startup", "Windows platform detected");

    Global.Configuration.Agent.platform = "Windows";
    builder.Services.AddWindowsService();
}
else if (OperatingSystem.IsLinux())
{
    Console.WriteLine("Linux platform detected");
    Logging.Debug("Program.cs", "Startup", "Linux platform detected");

    Global.Configuration.Agent.platform = "Linux";
    builder.Services.AddSystemd();
}
else if (OperatingSystem.IsMacOS())
{
    Console.WriteLine("MacOS platform detected");
    Logging.Debug("Program.cs", "Startup", "MacOS platform detected");

    Global.Configuration.Agent.platform = "MacOS";
}

Global.Initialization.Health.Check_Directories();

builder.Services.AddHostedService<Remote_Worker>();

// Get access key
Remote_Worker.access_key = Global.Initialization.Server_Config.Access_Key();
Global.Configuration.Agent.hwid = ENGINE.HW_UID; // init after access key, because the linux & macos hwid generation is based on the access key
Remote_Worker.authorized = Global.Initialization.Server_Config.Authorized();

var host = builder.Build();
host.Run();

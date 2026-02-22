using MySqlConnector;
using System.Data.Common;
using static System.Runtime.InteropServices.JavaScript.JSType;
using Microsoft.Extensions.Logging;

namespace NetLock_RMM_Server.Background_Services
{
    public class UpdateStateMonitoringService : BackgroundService
    {
        private readonly ILogger<UpdateStateMonitoringService> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckPendingUpdates();
                    await CheckAndUpdateMaxConcurrentAgentUpdates();
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (TaskCanceledException ex)
                {
                    Logging.Handler.Debug("Server_Information_Service.ExecuteAsync", "Task canceled: ", ex.ToString());

                    // Task was terminated, terminate cleanly
                    break;
                }
                catch (Exception ex)
                {
                    Logging.Handler.Error("ServerInformationService", "ExecuteAsync", ex.ToString());
                }
            }
        }

        private async Task CheckPendingUpdates()
        {
            try
            {
                // Check for each device if there are pending updates based on update_pending = 1 & update_started (DATETIME)
                MySqlConnection conn = new MySqlConnection(Configuration.MySQL.Connection_String);

                try
                {
                    await conn.OpenAsync();

                    string query = "SELECT * FROM devices WHERE update_pending = 1;";

                    MySqlCommand cmd = new MySqlCommand(query, conn);
                    
                    Logging.Handler.Debug("Example", "MySQL_Prepared_Query", query);

                    using (DbDataReader reader = await cmd.ExecuteReaderAsync())
                    {
                        if (reader.HasRows)
                        {
                            while (await reader.ReadAsync())
                            {
                                // compare update_started with datetimenow and if it is older than 15 minutes, set update_pending = 0
                                DateTime updateStarted = reader.GetDateTime(reader.GetOrdinal("update_started"));

                                if (DateTime.Now - updateStarted > TimeSpan.FromMinutes(15))
                                {
                                    string device_id = reader["id"].ToString();

                                    // Update the device to set update_pending = 0
                                    await MySQL.Handler.Execute_Command($"UPDATE devices SET update_pending = 0 WHERE id = {device_id};");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logging.Handler.Error("Example", "MySQL_Query", ex.ToString());
                }
                finally
                {
                    await conn.CloseAsync();
                }

                Logging.Handler.Debug("Server_Information_Service.UpdateServerInformation", "Server Information Update Task started at:", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                await NetLock_RMM_Server.MySQL.Handler.Update_Server_Information();
                Logging.Handler.Debug("Server_Information_Service.UpdateServerInformation", "Server Information Update Task finished at:", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("ServerInformationService", "UpdateServerInformation", ex.ToString());
            }
        }
        
        private async Task CheckAndUpdateMaxConcurrentAgentUpdates()
        {
            try
            {
                // Lese aktuellen Wert aus der Datenbank
                string dbValueStr = await MySQL.Handler.Quick_Reader("SELECT * FROM settings;", "agent_updates_max_concurrent_updates");
                
                if (string.IsNullOrEmpty(dbValueStr))
                {
                    _logger.LogWarning("Could not read agent_updates_max_concurrent_updates from database");
                    return;
                }

                int newValue = Convert.ToInt32(dbValueStr);
                
                // Vergleiche mit aktuellem Wert
                int currentValue = NetLock_RMM_Server.Configuration.Server.MaxConcurrentAgentUpdates;
                
                if (newValue != currentValue && newValue > 0)
                {
                    _logger.LogInformation($"Max Concurrent Agent Updates changed from {currentValue} to {newValue}. Updating semaphore...");
                    
                    // Aktualisiere den Wert in der Konfiguration
                    NetLock_RMM_Server.Configuration.Server.MaxConcurrentAgentUpdates = newValue;
                    
                    // Erstelle neuen Semaphore mit dem neuen Wert
                    var oldSemaphore = NetLock_RMM_Server.Configuration.Server.MaxConcurrentNetLockPackageDownloadsSemaphore;
                    NetLock_RMM_Server.Configuration.Server.MaxConcurrentNetLockPackageDownloadsSemaphore = new SemaphoreSlim(newValue, newValue);
                    
                    // Dispose alten Semaphore nach kurzer Verzögerung (damit laufende Downloads nicht abgebrochen werden)
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60));
                        oldSemaphore?.Dispose();
                    });
                    
                    _logger.LogInformation($"Semaphore updated successfully to {newValue} concurrent downloads");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking/updating max concurrent agent updates setting");
            }
        }
    }
}
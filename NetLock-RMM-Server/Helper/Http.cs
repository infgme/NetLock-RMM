using System.Runtime.Intrinsics.X86;
using System;
using System.Net;

namespace Helper
{
    public class Http
    {
        public static async Task<bool> Download_File(string url, string path)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "NetLock RMM Server Backend");
                    client.Timeout = TimeSpan.FromMinutes(60); // Long timeout for large files

                    // With ResponseHeadersRead we start the streaming as soon as the headers have been received.
                    using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        long? contentLength = response.Content.Headers.ContentLength;
                        if (contentLength.HasValue)
                        {
                            Logging.Handler.Debug("Helper.Http.Download_File", "Content-Length", contentLength.Value.ToString());
                        }
                        else
                        {
                            Logging.Handler.Error("Helper.Http.Download_File", "Content-Length", "No value specified.");
                        }

                        using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                        using (var responseStream = await response.Content.ReadAsStreamAsync())
                        {
                            byte[] buffer = new byte[81920]; // 80 KB Buffer
                            long totalBytesRead = 0;
                            int bytesRead;
                            int progressBarWidth = 50; // Width of the loading beam

                            // Download loop
                            while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalBytesRead += bytesRead;

                                if (contentLength.HasValue)
                                {
                                    double progress = (double)totalBytesRead / contentLength.Value;
                                    int progressBlocks = (int)(progress * progressBarWidth);
                                    string progressBar = "[" + new string('#', progressBlocks) + new string('-', progressBarWidth - progressBlocks) + "]";

                                    // Conversion from bytes to MB
                                    double totalMB = contentLength.Value / (1024.0 * 1024.0);
                                    double downloadedMB = totalBytesRead / (1024.0 * 1024.0);

                                    Console.Write($"\r{progressBar} {progress * 100:0.00}% - {downloadedMB:0.00} MB / {totalMB:0.00} MB");
                                }
                                else
                                {
                                    double downloadedMB = totalBytesRead / (1024.0 * 1024.0);
                                    Console.Write($"\rDownloaded {downloadedMB:0.00} MB");
                                }
                            }

                            Console.WriteLine(); // Line break after download
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("Helper.Http.Download_File", "General error", ex.ToString());
                Console.WriteLine(ex.ToString());
                return false;
            }
        }
        
        public static async Task<string> Get_Request_With_Api_Key(string url, bool membersPortal)
        {
            try
            {
                string api_key = await NetLock_RMM_Server.MySQL.Handler.Get_Api_Key(membersPortal);

                using (var httpClient = new HttpClient())
                {
                    // Set Header
                    httpClient.DefaultRequestHeaders.Add("X-Api-Key", api_key);

                    // GET Request absenden
                    var response = await httpClient.GetAsync(url);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        // Read response
                        var result = await response.Content.ReadAsStringAsync();
                        Logging.Handler.Debug("Online_Mode.Handler.Get_Request_With_Api_Key", "Result", result);

                        return result;
                    }
                    else
                    {
                        // Error handling
                        Logging.Handler.Debug("Online_Mode.Handler.Get_Request_With_Api_Key", "Request failed", response.ReasonPhrase);
                        return String.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Logging.Handler.Error("Online_Mode.Handler.Get_Request_With_Api_Key", "General error", ex.ToString());
                return String.Empty;
            }
        }
    }
}

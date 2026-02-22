using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace NetLock_RMM_Server.Files.Compiler
{
    public class Assembly_Manipulation
    {
        public static async Task<bool> Embedd_Server_Config(string path, string content)
        {
            try
            {
                // Encode content to base64
                string base64Content = await Base64.Handler.Encode(content);
                byte[] markerBytes = Encoding.UTF8.GetBytes("SERVERCONFIGMARKER");
                byte[] contentBytes = Encoding.UTF8.GetBytes(base64Content);
                
                // Log the operation
                Logging.Handler.Debug("Assembly_Manipulation.Embedd_Server_Config", "embedding", $"File: {path}, Content size: {contentBytes.Length} bytes");
                
                // Open file with explicit sharing to avoid conflicts
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // Seek to end of file
                    fs.Seek(0, SeekOrigin.End);
                    long originalSize = fs.Position;
                    
                    // Add 512-byte alignment padding for better compatibility with different PE formats
                    long paddingNeeded = (512 - (originalSize % 512)) % 512;
                    if (paddingNeeded > 0)
                    {
                        byte[] padding = new byte[paddingNeeded];
                        await fs.WriteAsync(padding, 0, (int)paddingNeeded);
                    }
                    
                    // Write marker
                    await fs.WriteAsync(markerBytes, 0, markerBytes.Length);
                    
                    // Write content
                    await fs.WriteAsync(contentBytes, 0, contentBytes.Length);
                    
                    // Flush to ensure data is written
                    await fs.FlushAsync();
                    
                    long finalSize = fs.Position;
                    Logging.Handler.Debug("Assembly_Manipulation.Embedd_Server_Config", "success", $"Original size: {originalSize}, Final size: {finalSize}, Added: {finalSize - originalSize} bytes");
                }

                return true;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("NetLock_RMM_Server.Helper.Compiler.Ressource_Manipulation.Write_Ressource", "General error", ex.ToString());
                Console.WriteLine(ex.ToString());
                return false;
            }
        }
    }
}

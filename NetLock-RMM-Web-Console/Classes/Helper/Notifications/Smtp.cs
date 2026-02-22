using System.Net.Mail;
using System.Net;
using MySqlConnector;
using System.Data.Common;
using System.Text.Json;

namespace NetLock_RMM_Web_Console.Classes.Helper.Notifications
{
    public class Smtp
    {
        public class Smtp_Settings
        {
            public string username { get; set; }
            public string password { get; set; }
            public string server { get; set; }
            public string port { get; set; }
            public bool ssl { get; set; }
            public string from { get; set; }
            public string fromAddress { get; set; }
            public string toAddress { get; set; }
        }

        public static async Task<string> Send_Mail(string recipient, string subject, string body)
        {
            string smtp_json = String.Empty;
            Smtp_Settings smtpSettings = null;

            MySqlConnection conn = new MySqlConnection(Configuration.MySQL.Connection_String);

            try
            {
                await conn.OpenAsync();

                MySqlCommand command = new MySqlCommand("SELECT * FROM settings;", conn);
                using (DbDataReader reader = await command.ExecuteReaderAsync())
                {
                    if (reader.HasRows)
                    {
                        while (await reader.ReadAsync())
                        {
                            smtp_json = reader["smtp"].ToString() ?? "";
                        }
                    }
                }

                smtpSettings = JsonSerializer.Deserialize<Smtp_Settings>(smtp_json);
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("class", "Send_Mail", ex.Message);
            }
            finally
            {
                await conn.CloseAsync();
            }

            // Validate smtpSettings
            if (smtpSettings == null)
            {
                string error = "SMTP settings could not be loaded from database";
                Logging.Handler.Error("Classes.Helper.Smtp", "Send_Mail", error);
                return error;
            }
            
            try
            {
                // Prioritize 'fromAddress' field, then 'from' field, finally fallback to username
                string fromAddress = !string.IsNullOrEmpty(smtpSettings.fromAddress) 
                    ? smtpSettings.fromAddress 
                    : (!string.IsNullOrEmpty(smtpSettings.from) 
                        ? smtpSettings.from 
                        : smtpSettings.username);

                using (SmtpClient smtpClient = new SmtpClient(smtpSettings.server, Convert.ToInt32(smtpSettings.port)))
                {
                    smtpClient.Credentials = new NetworkCredential(smtpSettings.username, smtpSettings.password);
                    smtpClient.EnableSsl = smtpSettings.ssl;

                    using (MailMessage mailMessage = new MailMessage())
                    {
                        mailMessage.From = new MailAddress(fromAddress, "Alerts | NetLock");
                        mailMessage.To.Add(recipient);
                        mailMessage.Subject = subject;
                        mailMessage.Body = body;

                        try
                        {
                            // Versende die E-Mail
                            await smtpClient.SendMailAsync(mailMessage);
                            return "success";
                        }
                        catch (Exception ex)
                        {
                            Logging.Handler.Error("Classes.Helper.Smtp", "Send_Mail", ex.Message);
                            return ex.Message;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("Classes.Helper.Smtp", "Send_Mail", ex.Message);
                return ex.Message;
            }
        }

        public static async Task<string> Test_Smtp(string username, string password, string server, int port, bool ssl, string from, string toAddress)
        {
            try
            {
                // Use 'from' field if available, otherwise fallback to username
                string fromAddress = string.IsNullOrEmpty(from) ? username : from;
                
                // Use toAddress if provided, otherwise fallback to username
                string recipient = string.IsNullOrEmpty(toAddress) ? username : toAddress;

                using (SmtpClient smtpClient = new SmtpClient(server, port))
                {
                    // Set the login information for the SMTP server
                    smtpClient.Credentials = new NetworkCredential(username, password);

                    // Enable SSL
                    smtpClient.EnableSsl = ssl;

                    // Create an instance of the MailMessage class
                    using (MailMessage mailMessage = new MailMessage())
                    {
                        // Set sender, recipient, subject and message text
                        mailMessage.From = new MailAddress(fromAddress, "Alerts | NetLock");
                        mailMessage.To.Add(recipient); // Add the recipient e-mail address here
                        mailMessage.Subject = "NetLock - Test Alert";
                        mailMessage.Body = "Test successful.";

                        try
                        {
                            // Send the e-mail
                            await smtpClient.SendMailAsync(mailMessage);
                            return "success";
                        }
                        catch (Exception ex)
                        {
                            Logging.Handler.Error("Classes.Helper.Smtp", "Send_Mail", ex.Message);
                            return ex.Message;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("Classes.Helper.Smtp", "Send_Mail", ex.Message);
                return ex.Message;
            }
        }

    }
}

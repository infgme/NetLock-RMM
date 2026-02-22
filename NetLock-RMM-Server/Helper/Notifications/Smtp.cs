using System.Net.Mail;
using System.Net;
using MySqlConnector;
using System.Data.Common;
using System.Text.Json;
using NetLock_RMM_Server.Configuration;


namespace Helper.Notifications
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
            public string fromAddress { get; set; }
            public string from { get; set; }
        }

        public static async Task<bool> Send_Mail(string recipient, string subject, string body)
        {
            string smtp_json = String.Empty;
            Smtp_Settings smtpSettings = null;

            MySqlConnection conn = new MySqlConnection(MySQL.Connection_String);

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
                return false;
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
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Logging.Handler.Error("Classes.Helper.Smtp", "Send_Mail", ex.Message);
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("Classes.Helper.Smtp", "Send_Mail", ex.Message);
                return false;
            }
        }

        public static async Task<string> Test_Smtp(string username, string password, string server, int port, bool ssl)
        {
            try
            {
                using (SmtpClient smtpClient = new SmtpClient(server, port))
                {
                    // Setze die Anmeldeinformationen für den SMTP-Server
                    smtpClient.Credentials = new NetworkCredential(username, password);

                    // Aktiviere SSL
                    smtpClient.EnableSsl = ssl;

                    // Erstelle eine Instanz der MailMessage-Klasse
                    using (MailMessage mailMessage = new MailMessage())
                    {
                        // Setze Absender, Empfänger, Betreff und Nachrichtentext
                        mailMessage.From = new MailAddress("Alerts | NetLock <" + username + ">");
                        mailMessage.To.Add(username); // Füge hier die Empfänger-E-Mail-Adresse hinzu
                        mailMessage.Subject = "NetLock - Test Alert";
                        mailMessage.Body = "Test erfolgreich.";

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

    }
}

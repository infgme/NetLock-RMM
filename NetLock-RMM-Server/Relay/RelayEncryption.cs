using System.Security.Cryptography;
using System.Text;
using MySqlConnector;

namespace NetLock_RMM_Server.Relay
{
    /// <summary>
    /// Manages RSA encryption for relay sessions.
    /// The server holds an RSA keypair; clients encrypt session keys with the server public key.
    /// </summary>
    public static class RelayEncryption
    {
        private static RSA? _serverRsa = null;
        private static string? _publicKeyPem = null;
        private static readonly object _lock = new object();

        /// <summary>
        /// Initialize or load the RSA keypair at server startup.
        /// </summary>
        public static async Task<bool> InitializeServerKeys()
        {
            try
            {
                Logging.Handler.Debug("RelayEncryption", "InitializeServerKeys", "Initializing RSA keypair...");

                // Check if keys already exist in DB
                using var conn = new MySqlConnection(Configuration.MySQL.Connection_String);
                await conn.OpenAsync();

                using var checkCmd = new MySqlCommand(
                    "SELECT relay_private_key, relay_public_key FROM settings LIMIT 1;", conn);

                string? privateKeyPem = null;
                string? publicKeyPem = null;

                using (var reader = await checkCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        privateKeyPem = reader.IsDBNull(0) ? null : reader.GetString(0);
                        publicKeyPem = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }
                }

                // If keys exist => load them
                if (!string.IsNullOrEmpty(privateKeyPem) && !string.IsNullOrEmpty(publicKeyPem))
                {
                    Logging.Handler.Debug("RelayEncryption", "InitializeServerKeys", "Loading existing RSA keys from database...");

                    lock (_lock)
                    {
                        _serverRsa = RSA.Create();
                        _serverRsa.ImportFromPem(privateKeyPem);
                        _publicKeyPem = publicKeyPem;
                    }

                    Logging.Handler.Info("RelayEncryption", "InitializeServerKeys", "Keys loaded successfully");
                    return true;
                }

                // Otherwise: generate new keys
                Logging.Handler.Info("RelayEncryption", "InitializeServerKeys", "No keys found - generating new RSA-4096 keypair...");

                RSA newRsa = RSA.Create(4096);
                string newPrivateKeyPem = newRsa.ExportRSAPrivateKeyPem();
                string newPublicKeyPem = newRsa.ExportRSAPublicKeyPem();

                // Store in DB
                using var updateCmd = new MySqlCommand(@"
                    UPDATE settings 
                    SET relay_private_key = @privateKey, 
                        relay_public_key = @publicKey 
                    LIMIT 1;", conn);

                updateCmd.Parameters.AddWithValue("@privateKey", newPrivateKeyPem);
                updateCmd.Parameters.AddWithValue("@publicKey", newPublicKeyPem);
                await updateCmd.ExecuteNonQueryAsync();

                lock (_lock)
                {
                    _serverRsa = newRsa;
                    _publicKeyPem = newPublicKeyPem;
                }

                Logging.Handler.Info("RelayEncryption", "InitializeServerKeys", "New keypair generated and stored");

                return true;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayEncryption", "InitializeServerKeys", ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Return the public key in PEM format.
        /// </summary>
        public static string GetPublicKeyPem()
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_publicKeyPem))
                {
                    throw new InvalidOperationException("RSA keys not initialized. Call InitializeServerKeys first.");
                }
                return _publicKeyPem;
            }
        }

        /// <summary>
        /// Decrypt a session key encrypted with the server public key.
        /// </summary>
        /// <param name="encryptedKeyBase64">Base64-encoded encrypted key</param>
        /// <returns>Decrypted session key as byte array</returns>
        public static byte[] DecryptSessionKey(string encryptedKeyBase64)
        {
            try
            {
                lock (_lock)
                {
                    if (_serverRsa == null)
                    {
                        throw new InvalidOperationException("RSA keys not initialized");
                    }

                    byte[] encryptedData = Convert.FromBase64String(encryptedKeyBase64);
                    byte[] decryptedKey = _serverRsa.Decrypt(encryptedData, RSAEncryptionPadding.OaepSHA256);

                    return decryptedKey;
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayEncryption", "DecryptSessionKey", ex.ToString());
                throw new CryptographicException("Failed to decrypt session key", ex);
            }
        }

        /// <summary>
        /// Rotate the server RSA keypair (manual rotation).
        /// WARNING: All active sessions will become invalid.
        /// </summary>
        public static async Task<bool> RotateServerKeys()
        {
            try
            {
                Logging.Handler.Warning("RelayEncryption", "RotateServerKeys", "Rotating RSA keypair - active sessions will be invalidated");

                // Generate new keys
                RSA newRsa = RSA.Create(4096);
                string newPrivateKeyPem = newRsa.ExportRSAPrivateKeyPem();
                string newPublicKeyPem = newRsa.ExportRSAPublicKeyPem();

                // Store in DB
                using var conn = new MySqlConnection(Configuration.MySQL.Connection_String);
                await conn.OpenAsync();

                using var updateCmd = new MySqlCommand(@"
                    UPDATE settings 
                    SET relay_private_key = @privateKey, 
                        relay_public_key = @publicKey 
                    LIMIT 1;", conn);

                updateCmd.Parameters.AddWithValue("@privateKey", newPrivateKeyPem);
                updateCmd.Parameters.AddWithValue("@publicKey", newPublicKeyPem);
                await updateCmd.ExecuteNonQueryAsync();

                // Replace running keys
                lock (_lock)
                {
                    _serverRsa?.Dispose();
                    _serverRsa = newRsa;
                    _publicKeyPem = newPublicKeyPem;
                }

                Logging.Handler.Info("RelayEncryption", "RotateServerKeys", "RSA keypair rotated successfully");

                return true;
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayEncryption", "RotateServerKeys", ex.ToString());
                return false;
            }
        }

        /// <summary>
        /// Return the fingerprint of the public key (for out-of-band verification).
        /// </summary>
        public static string GetPublicKeyFingerprint()
        {
            try
            {
                lock (_lock)
                {
                    if (_serverRsa == null || string.IsNullOrEmpty(_publicKeyPem))
                    {
                        throw new InvalidOperationException("RSA keys not initialized");
                    }

                    // SHA-256 hash of the public key
                    using var sha256 = SHA256.Create();
                    byte[] publicKeyBytes = Encoding.UTF8.GetBytes(_publicKeyPem);
                    byte[] hashBytes = sha256.ComputeHash(publicKeyBytes);

                    // Format as hex string with colons (SSH style)
                    var fingerprint = BitConverter.ToString(hashBytes).Replace("-", ":");
                    return $"SHA256:{fingerprint}";
                }
            }
            catch (Exception ex)
            {
                Logging.Handler.Error("RelayEncryption", "GetPublicKeyFingerprint", ex.ToString());
                return "ERROR";
            }
        }

        /// <summary>
        /// Cleanup on server shutdown.
        /// </summary>
        public static void Dispose()
        {
            lock (_lock)
            {
                _serverRsa?.Dispose();
                _serverRsa = null;
                _publicKeyPem = null;
            }
        }
    }
}

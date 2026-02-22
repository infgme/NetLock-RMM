using System;
using System.Security.Cryptography;

namespace NetLock_RMM_Relay_App.RelayClient
{
    /// <summary>
    /// OPTIMIZED end-to-end encryption for Relay App
    /// 
    /// Uses session-key based encryption for MAXIMUM performance:
    /// 1. Once at startup: Generate AES-256 session key
    /// 2. All data: Encrypt with AES-256-GCM (1000x faster than RSA!)
    /// 
    /// Performance improvement: ~1000x faster than hybrid RSA per packet!
    /// Format: [Nonce:12][Tag:16][Ciphertext]
    /// </summary>
    public class RelayEncryption : IDisposable
    {
        private readonly RSA _adminPrivateKey;   // Admin private key (for decryption) (currently unused)
        private readonly RSA _agentPublicKey;    // Agent public key (for encryption)
        
        // AES session key (generated once, NOT per packet!)
        private byte[] _sessionKey;
        private bool _sessionKeyEstablished = false;

        /// <summary>
        /// Constructor for E2EE Relay Encryption (Admin Side)
        /// </summary>
        /// <param name="adminPrivateKey">Admin RSA private key</param>
        /// <param name="agentPublicKey">Agent public key</param>
        public RelayEncryption(RSA adminPrivateKey, RSA agentPublicKey)
        {
            _adminPrivateKey = adminPrivateKey ?? throw new ArgumentNullException(nameof(adminPrivateKey));
            _agentPublicKey = agentPublicKey ?? throw new ArgumentNullException(nameof(agentPublicKey));
            
            // Generate AES-256 session key ONCE
            _sessionKey = new byte[32]; // 256 bits
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(_sessionKey);
            }
            _sessionKeyEstablished = true;
            
            Console.WriteLine("[RELAY APP E2EE] Session Key established (AES-256-GCM)");
        }

        /// <summary>
        /// Exports session key encrypted with RSA for agent
        /// Admin sends this with the first packet!
        /// </summary>
        public byte[] ExportEncryptedSessionKey()
        {
            if (!_sessionKeyEstablished)
                throw new InvalidOperationException("Session key not established");
            
            // Encrypt session key with agent public key
            return _agentPublicKey.Encrypt(_sessionKey, RSAEncryptionPadding.OaepSHA256);
        }

        /// <summary>
        /// Encrypts data with AES-256-GCM
        /// Format: [Nonce:12][Tag:16][Ciphertext]
        /// </summary>
        public byte[] Encrypt(byte[] plaintext)
        {
            if (plaintext == null || plaintext.Length == 0)
                throw new ArgumentException("Plaintext cannot be null or empty", nameof(plaintext));

            if (!_sessionKeyEstablished)
                throw new InvalidOperationException("Session key not established");

            using (var aesGcm = new AesGcm(_sessionKey, AesGcm.TagByteSizes.MaxSize))
            {
                // Generate nonce (12 bytes for GCM)
                byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
                RandomNumberGenerator.Fill(nonce);
                
                // Encrypt
                byte[] ciphertext = new byte[plaintext.Length];
                byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes
                
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
                
                // Combine: [Nonce:12][Tag:16][Ciphertext]
                byte[] result = new byte[nonce.Length + tag.Length + ciphertext.Length];
                Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
                Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
                Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);
                
                return result;
            }
        }

        /// <summary>
        /// Decrypts data with AES-256-GCM
        /// </summary>
        public byte[] Decrypt(byte[] encrypted)
        {
            if (encrypted == null || encrypted.Length < 28) // Min: 12 (nonce) + 16 (tag) + data
                throw new ArgumentException("Invalid encrypted data", nameof(encrypted));

            if (!_sessionKeyEstablished)
                throw new InvalidOperationException("Session key not established");

            using (var aesGcm = new AesGcm(_sessionKey, AesGcm.TagByteSizes.MaxSize))
            {
                // Parse: [Nonce:12][Tag:16][Ciphertext]
                int offset = 0;
                
                byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
                Buffer.BlockCopy(encrypted, offset, nonce, 0, nonce.Length);
                offset += nonce.Length;
                
                byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes
                Buffer.BlockCopy(encrypted, offset, tag, 0, tag.Length);
                offset += tag.Length;
                
                byte[] ciphertext = new byte[encrypted.Length - offset];
                Buffer.BlockCopy(encrypted, offset, ciphertext, 0, ciphertext.Length);
                
                // Decrypt
                byte[] plaintext = new byte[ciphertext.Length];
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
                
                return plaintext;
            }
        }

        public void Dispose()
        {
            // Clear session key from memory (security)
            if (_sessionKey != null && _sessionKey.Length > 0)
            {
                Array.Clear(_sessionKey, 0, _sessionKey.Length);
                _sessionKey = Array.Empty<byte>();
            }
        }
    }
    
    /// <summary>
    /// Helper class for RSA keypair generation and server verification
    /// </summary>
    public static class RelayKeyGenerator
    {
        /// <summary>
        /// Generates a new RSA-4096 keypair for admin
        /// </summary>
        public static (RSA rsa, string publicKeyPem) GenerateKeyPair()
        {
            RSA rsa = RSA.Create(4096);
            string publicKeyPem = rsa.ExportRSAPublicKeyPem();
            return (rsa, publicKeyPem);
        }

        /// <summary>
        /// Imports a public key from PEM string
        /// </summary>
        public static RSA ImportPublicKey(string publicKeyPem)
        {
            RSA rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa;
        }

        /// <summary>
        /// Calculates SHA256 fingerprint of a public key
        /// </summary>
        public static string CalculateFingerprint(string publicKeyPem)
        {
            using var sha256 = SHA256.Create();
            byte[] publicKeyBytes = System.Text.Encoding.UTF8.GetBytes(publicKeyPem);
            byte[] hashBytes = sha256.ComputeHash(publicKeyBytes);
            
            return $"SHA256:{BitConverter.ToString(hashBytes).Replace("-", ":")}";
        }

        /// <summary>
        /// Verifies server public key fingerprint (TOFU - Trust On First Use)
        /// </summary>
        public static bool VerifyServerFingerprint(string publicKeyPem, string expectedFingerprint)
        {
            string calculatedFingerprint = CalculateFingerprint(publicKeyPem);
            return calculatedFingerprint.Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase);
        }
    }
}

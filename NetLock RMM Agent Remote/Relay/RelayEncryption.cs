using System;
using System.Security.Cryptography;

namespace NetLock_RMM_Agent_Remote.Relay
{
    /// <summary>
    /// OPTIMIZED end-to-end encryption for Relay
    /// 
    /// Uses hybrid encryption for MAXIMUM performance:
    /// 1. RSA-4096: Only once for exchanging the AES session key
    /// 2. AES-256-GCM: For all data (1000x faster than RSA!)
    /// </summary>
    public class RelayEncryption : IDisposable
    {
        private readonly RSA _ownPrivateKey;   // Own private key (for decryption)
        private readonly RSA _peerPublicKey;   // Peer's public key (for encryption)
        
        // AES Session Key (received from admin!)
        private byte[]? _sessionKey = null;
        private bool _sessionKeyEstablished = false;
        private readonly object _sessionKeyLock = new object();

        /// <summary>
        /// Constructor for E2EE Relay Encryption
        /// </summary>
        /// <param name="ownPrivateKey">Own RSA private key (used for decryption)</param>
        /// <param name="peerPublicKey">Peer's public key (used for encryption)</param>
        public RelayEncryption(RSA ownPrivateKey, RSA peerPublicKey)
        {
            _ownPrivateKey = ownPrivateKey ?? throw new ArgumentNullException(nameof(ownPrivateKey));
            _peerPublicKey = peerPublicKey ?? throw new ArgumentNullException(nameof(peerPublicKey));
            
            // Session key is NOT generated here, but received from admin!
            Console.WriteLine($"[RELAY E2EE] Waiting for Session-Key from Admin...");
        }

        /// <summary>
        /// Imports RSA-encrypted session key from admin
        /// Called when first packet is received
        /// </summary>
        public void ImportEncryptedSessionKey(byte[] encryptedSessionKey)
        {
            lock (_sessionKeyLock)
            {
                if (_sessionKeyEstablished)
                {
                    Console.WriteLine($"[RELAY E2EE] Session-Key already established, ignoring");
                    return;
                }

                try
                {
                    // Decrypt session key with own private key
                    _sessionKey = _ownPrivateKey.Decrypt(encryptedSessionKey, RSAEncryptionPadding.OaepSHA256);
                    _sessionKeyEstablished = true;
                    
                    Console.WriteLine($"[RELAY E2EE] Session-Key established (AES-256-GCM) - received from Admin");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RELAY E2EE] Failed to import Session-Key: {ex.Message}");
                    throw;
                }
            }
        }
        
        /// <summary>
        /// Checks if the session key has been established
        /// </summary>
        public bool IsSessionKeyEstablished()
        {
            lock (_sessionKeyLock)
            {
                return _sessionKeyEstablished;
            }
        }

        /// <summary>
        /// Encrypts data with AES-256-GCM (FAST!)
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
        /// Decrypts data with AES-256-GCM (faster)
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
            // Clear session key from memory
            if (_sessionKey != null)
            {
                Array.Clear(_sessionKey, 0, _sessionKey.Length);
                _sessionKey = null;
            }
        }
    }
}


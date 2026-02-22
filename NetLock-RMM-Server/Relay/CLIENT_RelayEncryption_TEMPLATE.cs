/*using System;
using System.Security.Cryptography;

namespace NetLock_RMM_Client.Relay
{
    /// <summary>
    /// Client-side end-to-end encryption for the relay.
    /// Used by both Agent and Admin client.
    /// Hybrid encryption: RSA-4096 for small payloads, RSA + AES-256-GCM for large payloads.
    /// </summary>
    public class RelayEncryption : IDisposable
    {
        private readonly RSA _ownPrivateKey;   // own private key (Agent or Admin)
        private readonly RSA _peerPublicKey;   // peer public key
        
        private const int RSA_MAX_PLAINTEXT_SIZE = 446; // RSA-4096 with OAEP-SHA256

        /// <summary>
        /// Constructor for E2EE relay encryption.
        /// </summary>
        /// <param name="ownPrivateKey">own RSA private key (used for decryption)</param>
        /// <param name="peerPublicKey">peer public key (used for encryption)</param>
        public RelayEncryption(RSA ownPrivateKey, RSA peerPublicKey)
        {
            _ownPrivateKey = ownPrivateKey ?? throw new ArgumentNullException(nameof(ownPrivateKey));
            _peerPublicKey = peerPublicKey ?? throw new ArgumentNullException(nameof(peerPublicKey));
        }

        /// <summary>
        /// Encrypts data using the peer's public key.
        /// Uses hybrid encryption for large payloads.
        /// </summary>
        public byte[] Encrypt(byte[] plaintext)
        {
            if (plaintext == null || plaintext.Length == 0)
                throw new ArgumentException("Plaintext cannot be null or empty", nameof(plaintext));

            // Small payloads: direct RSA
            if (plaintext.Length <= RSA_MAX_PLAINTEXT_SIZE)
            {
                return _peerPublicKey.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA256);
            }

            // Large payloads: hybrid (AES-GCM + RSA)
            return HybridEncrypt(plaintext);
        }

        /// <summary>
        /// Decrypts data using the own private key.
        /// </summary>
        public byte[] Decrypt(byte[] ciphertext)
        {
            if (ciphertext == null || ciphertext.Length == 0)
                throw new ArgumentException("Ciphertext cannot be null or empty", nameof(ciphertext));

            // Determine hybrid vs direct RSA
            if (ciphertext.Length <= 512) // RSA-4096 ciphertext = 512 bytes
            {
                return _ownPrivateKey.Decrypt(ciphertext, RSAEncryptionPadding.OaepSHA256);
            }

            // Hybrid decryption
            return HybridDecrypt(ciphertext);
        }

        /// <summary>
        /// Hybrid encryption: AES-256-GCM + RSA-4096
        /// Format: [KeyLength(4)][EncryptedAESKey(512)][IV(12)][Tag(16)][Ciphertext]
        /// </summary>
        private byte[] HybridEncrypt(byte[] plaintext)
        {
            // 1. generate AES-256 key + nonce
            byte[] aesKey = new byte[32]; // 256 bits
            byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
            
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(aesKey);
                rng.GetBytes(nonce);
            }

            // 2. encrypt data with AES-GCM
            byte[] ciphertext = new byte[plaintext.Length];
            byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes

            using (var aesGcm = new AesGcm(aesKey))
            {
                aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            // 3. encrypt AES key with peer public key
            byte[] encryptedKey = _peerPublicKey.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

            // 4. combine: [KeyLength(4)][EncryptedKey][Nonce(12)][Tag(16)][Ciphertext]
            int keyLength = encryptedKey.Length;
            byte[] result = new byte[4 + keyLength + nonce.Length + tag.Length + ciphertext.Length];
            
            int offset = 0;
            Buffer.BlockCopy(BitConverter.GetBytes(keyLength), 0, result, offset, 4);
            offset += 4;
            Buffer.BlockCopy(encryptedKey, 0, result, offset, keyLength);
            offset += keyLength;
            Buffer.BlockCopy(nonce, 0, result, offset, nonce.Length);
            offset += nonce.Length;
            Buffer.BlockCopy(tag, 0, result, offset, tag.Length);
            offset += tag.Length;
            Buffer.BlockCopy(ciphertext, 0, result, offset, ciphertext.Length);

            return result;
        }

        /// <summary>
        /// Hybrid decryption: AES-256-GCM + RSA-4096
        /// </summary>
        private byte[] HybridDecrypt(byte[] encrypted)
        {
            // 1. parse format: [KeyLength(4)][EncryptedKey][Nonce(12)][Tag(16)][Ciphertext]
            int offset = 0;
            int keyLength = BitConverter.ToInt32(encrypted, offset);
            offset += 4;

            byte[] encryptedKey = new byte[keyLength];
            Buffer.BlockCopy(encrypted, offset, encryptedKey, 0, keyLength);
            offset += keyLength;

            byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
            Buffer.BlockCopy(encrypted, offset, nonce, 0, nonce.Length);
            offset += nonce.Length;

            byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];
            Buffer.BlockCopy(encrypted, offset, tag, 0, tag.Length);
            offset += tag.Length;

            byte[] ciphertext = new byte[encrypted.Length - offset];
            Buffer.BlockCopy(encrypted, offset, ciphertext, 0, ciphertext.Length);

            // 2. decrypt AES key with own private key
            byte[] aesKey = _ownPrivateKey.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);

            // 3. decrypt data with AES-GCM
            byte[] plaintext = new byte[ciphertext.Length];

            using (var aesGcm = new AesGcm(aesKey))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            return plaintext;
        }

        public void Dispose()
        {
            // Private keys should be managed by the caller
            // Perform cleanup here only if necessary
        }
    }

    /// <summary>
    /// Helper class for RSA keypair generation
    /// </summary>
    public static class RelayKeyGenerator
    {
        /// <summary>
        /// Generates a new RSA-4096 keypair for Agent/Admin
        /// </summary>
        public static (RSA rsa, string publicKeyPem) GenerateKeyPair()
        {
            RSA rsa = RSA.Create(4096);
            string publicKeyPem = rsa.ExportRSAPublicKeyPem();
            return (rsa, publicKeyPem);
        }

        /// <summary>
        /// Imports a public key from a PEM string
        /// </summary>
        public static RSA ImportPublicKey(string publicKeyPem)
        {
            RSA rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa;
        }

        /// <summary>
        /// Verifies server public key fingerprint (TOFU)
        /// </summary>
        public static bool VerifyServerFingerprint(string publicKeyPem, string expectedFingerprint)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] publicKeyBytes = System.Text.Encoding.UTF8.GetBytes(publicKeyPem);
            byte[] hashBytes = sha256.ComputeHash(publicKeyBytes);
            
            string fingerprint = $"SHA256:{BitConverter.ToString(hashBytes).Replace("-", ":")}";
            return fingerprint.Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase);
        }
    }
}
*/
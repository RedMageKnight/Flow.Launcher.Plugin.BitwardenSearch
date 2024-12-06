using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Net;
using Flow.Launcher.Plugin.BitwardenSearch;

public class SecureMasterPasswordVerifier
{
    private const string HASH_CREDENTIAL_NAME = "BitwardenFlowPlugin_MasterHash";
    private const int PBKDF2_ITERATIONS = 600000; // High iteration count for security

    public static void StoreMasterPasswordHash(string masterPassword, string clientId)
    {
        try
        {
            // Generate a unique salt based on the clientId
            byte[] saltBytes = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(clientId)
            );

            // Generate hash
            byte[] hashBytes = GeneratePasswordHash(masterPassword, saltBytes);

            // Convert to Base64 for storage
            string combinedHash = Convert.ToBase64String(
                saltBytes.Concat(hashBytes).ToArray()
            );

            // Store in Windows Credential Manager
            SecureCredentialManager.SaveCredential(
            HASH_CREDENTIAL_NAME,
            new NetworkCredential(string.Empty, combinedHash).SecurePassword,
            HASH_CREDENTIAL_NAME
        );

            Logger.Log("Master password hash stored securely", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to store master password hash", ex);
            throw;
        }
    }

    public static bool VerifyMasterPassword(string masterPassword, string clientId)
    {
        try
        {
            // Retrieve stored hash
            var storedHashSecure = SecureCredentialManager.RetrieveCredential(HASH_CREDENTIAL_NAME, HASH_CREDENTIAL_NAME);
            if (storedHashSecure == null)
            {
                Logger.Log("No stored master password hash found", LogLevel.Warning);
                return false;
            }

            string storedCombinedHash = new NetworkCredential(string.Empty, storedHashSecure).Password;
            byte[] storedCombined = Convert.FromBase64String(storedCombinedHash);

            // Split salt and hash
            byte[] saltBytes = storedCombined.Take(32).ToArray(); // SHA256 produces 32 bytes
            byte[] storedHashBytes = storedCombined.Skip(32).ToArray();

            // Generate hash of provided password
            byte[] newHashBytes = GeneratePasswordHash(masterPassword, saltBytes);

            // Constant-time comparison to prevent timing attacks
            bool matches = ConstantTimeEquals(storedHashBytes, newHashBytes);
            
            Logger.Log("Master password verification completed", LogLevel.Debug);
            return matches;
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to verify master password", ex);
            return false;
        }
    }

    private static byte[] GeneratePasswordHash(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password,
            salt,
            PBKDF2_ITERATIONS,
            HashAlgorithmName.SHA256
        );
        return pbkdf2.GetBytes(32); // 256 bits
    }

    // Constant-time comparison to prevent timing attacks
    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;

        int result = 0;
        for (int i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }
        return result == 0;
    }

    public static void ClearStoredHash()
    {
        try
        {
            SecureCredentialManager.DeleteCredential(HASH_CREDENTIAL_NAME);
            Logger.Log("Master password hash cleared", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to clear master password hash", ex);
        }
    }
}
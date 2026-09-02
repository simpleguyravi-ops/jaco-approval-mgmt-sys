using System.Security.Cryptography;
using System.Text;

namespace JACO.Unified.Infrastructure;

// API keys are high-entropy random secrets (256 bits), not human-chosen passwords, so a
// plain salted SHA-256 hash is the right tool here -- unlike PasswordHasher's deliberately
// slow PBKDF2, there's no low-entropy dictionary attack to defend against, only "don't
// store the plaintext." Same principle GitHub/Stripe personal-access tokens use.
public static class ApiKeyService
{
    const string Prefix = "jaco_";

    public static (string plaintextKey, string hash, string keyPrefix) GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var key = Prefix + Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        // Non-secret, shown in the UI so an admin can tell keys apart once the plaintext is
        // gone -- also doubles as the DB lookup key so verifying a presented key doesn't
        // require hashing it against every active client.
        var keyPrefix = key[..Math.Min(16, key.Length)];
        return (key, Hash(key), keyPrefix);
    }

    public static string Hash(string key) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static bool Verify(string key, string hash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Hash(key)), Encoding.UTF8.GetBytes(hash));
}

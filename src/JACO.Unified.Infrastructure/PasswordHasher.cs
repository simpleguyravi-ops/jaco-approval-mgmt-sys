using System.Security.Cryptography;

namespace JACO.Unified.Infrastructure;

// PBKDF2-SHA256, salt stored alongside the hash (both base64) -- same algorithm/parameters
// as JACO Portal's own PasswordHasher, kept identical on purpose so password strength is
// consistent platform-wide even though each app's credential store is independent.
public static class PasswordHasher
{
    const int Iterations = 100_000;
    const int SaltSize = 16;
    const int KeySize = 32;

    public static (string hash, string salt) Hash(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, HashAlgorithmName.SHA256, KeySize);
        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    public static bool Verify(string password, string hash, string salt)
    {
        var saltBytes = Convert.FromBase64String(salt);
        var expected = Convert.FromBase64String(hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, HashAlgorithmName.SHA256, KeySize);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

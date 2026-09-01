using Microsoft.AspNetCore.DataProtection;

namespace JACO.Unified.Infrastructure;

// Encrypts the SMTP password at rest using the same Data Protection key ring already
// configured for the shared SSO cookie (see Program.cs) -- a dedicated purpose string
// keeps it cryptographically isolated from the cookie's own protector even though they
// share a key ring. Unprotect falls back to returning the raw value on failure rather
// than throwing, so a value written before this existed (plaintext) or inserted directly
// via SQL still works -- it's simply re-encrypted the next time it's saved through the UI.
public sealed class EmailPasswordProtector(IDataProtectionProvider provider)
{
    readonly IDataProtector protector = provider.CreateProtector("JACO.Unified.EmailSettings.Password.v1");

    public string Protect(string plaintext) => protector.Protect(plaintext);

    public string Unprotect(string stored)
    {
        try { return protector.Unprotect(stored); }
        catch (System.Security.Cryptography.CryptographicException) { return stored; }
    }
}

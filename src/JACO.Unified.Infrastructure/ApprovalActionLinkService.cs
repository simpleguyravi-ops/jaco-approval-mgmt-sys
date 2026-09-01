using Microsoft.AspNetCore.DataProtection;

namespace JACO.Unified.Infrastructure;

// Signs+encrypts a (requestId, userId) pair into an opaque, time-limited token for one-click
// email Approve/Reject links -- same IDataProtectionProvider (and shared key ring) already
// used for the SSO cookie and the SMTP password, so no new key material to manage. A tampered
// or expired token fails Unprotect entirely (authenticated encryption); nothing about the
// request/user is ever exposed in the URL itself, so there's no ID to enumerate or edit.
public sealed class ApprovalActionLinkService(IDataProtectionProvider provider)
{
    static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
    readonly ITimeLimitedDataProtector protector = provider.CreateProtector("JACO.Unified.ApprovalActionLink.v1").ToTimeLimitedDataProtector();

    public string GenerateToken(long requestId, int userId) => protector.Protect($"{requestId}:{userId}", Lifetime);

    public bool TryValidate(string? token, out long requestId, out int userId)
    {
        requestId = 0;
        userId = 0;
        if (string.IsNullOrEmpty(token)) return false;
        try
        {
            var parts = protector.Unprotect(token).Split(':');
            return parts.Length == 2 && long.TryParse(parts[0], out requestId) && int.TryParse(parts[1], out userId);
        }
        catch (Exception)
        {
            // Covers both a tampered token (CryptographicException) and an expired one
            // (also surfaces as CryptographicException from the time-limited protector) --
            // either way, the link simply doesn't work anymore.
            return false;
        }
    }
}

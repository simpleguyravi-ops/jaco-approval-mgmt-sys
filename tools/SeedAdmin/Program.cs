// One-time bootstrap for a fresh JAMS deployment: a brand-new database has zero AppUsers
// rows, so nobody can sign in to create the first admin account through the UI. Run this
// once per environment (Test, then Production) to create (or promote/reset) one admin.
//
// Usage:
//   dotnet run -- "<connection string>" <username> "<display name>" <password>
//
// Example:
//   dotnet run -- "Server=localhost\MSSQLSERVER01;Database=JACO_Unified;Trusted_Connection=True;TrustServerCertificate=True;" admin "Administrator" "ChangeMe!2026"
//
// Safe to re-run: if the username already exists, this resets its password and makes sure
// IsAdmin/IsActive are both set, rather than failing on a duplicate.

using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using Microsoft.EntityFrameworkCore;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: dotnet run -- \"<connection string>\" <username> \"<display name>\" <password>");
    return 1;
}

var (connectionString, userName, displayName, password) = (args[0], args[1], args[2], args[3]);

if (password.Length < 8)
{
    Console.Error.WriteLine("Password must be at least 8 characters (matches the app's own minimum).");
    return 1;
}

var options = new DbContextOptionsBuilder<UnifiedDbContext>()
    .UseSqlServer(connectionString)
    .Options;

await using var db = new UnifiedDbContext(options);

var (hash, salt) = PasswordHasher.Hash(password);
var existing = await db.AppUsers.SingleOrDefaultAsync(u => u.UserName == userName);

if (existing is null)
{
    db.AppUsers.Add(new AppUser
    {
        UserName = userName,
        DisplayName = displayName,
        PasswordHash = hash,
        PasswordSalt = salt,
        IsAdmin = true,
        IsActive = true,
        MustChangePassword = true
    });
    Console.WriteLine($"Created admin account '{userName}'.");
}
else
{
    existing.DisplayName = displayName;
    existing.PasswordHash = hash;
    existing.PasswordSalt = salt;
    existing.IsAdmin = true;
    existing.IsActive = true;
    existing.MustChangePassword = true;
    existing.FailedLoginCount = 0;
    existing.LockedUntil = null;
    Console.WriteLine($"Updated existing account '{userName}' to admin with a new password.");
}

await db.SaveChangesAsync();
Console.WriteLine("Done. Sign in and change the password immediately (the app will force this on first login).");
return 0;

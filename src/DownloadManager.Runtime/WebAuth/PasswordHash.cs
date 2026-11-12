using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Runtime.WebAuth;

/// <summary>A saved password: PBKDF2 with SHA-256, a random salt and the iteration count it was made with.</summary>
public sealed class PasswordRecord
{
    public string Algorithm { get; set; } = PasswordHash.Algorithm;
    public int Iterations { get; set; }
    public string Salt { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public DateTimeOffset SetAt { get; set; }

    /// <summary><c>setup</c> (the setup page), <c>changed</c> (signed in), or <c>environment</c> (<c>DOWNLOADMANAGER_PASSWORD</c>).</summary>
    public string Source { get; set; } = "setup";
}

/// <summary>
/// The one user's password, stored only as a PBKDF2-SHA256 hash with 600,000 iterations (the OWASP figure for
/// PBKDF2-HMAC-SHA256), a 16-byte random salt and a 32-byte result. Checking a password compares in fixed time.
/// Every check costs the same work, so at most two run at once: a burst of tries can't take the server's CPU.
/// </summary>
public static class PasswordHash
{
    public const string Algorithm = "pbkdf2-sha256";
    public const int Iterations = 600_000;
    public const int MaximumIterations = 10 * Iterations;
    public const int MinimumLength = 8;
    public const int MaximumLength = 1024;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly SemaphoreSlim Work = new(2, 2);

    /// <summary>Why this password can't be used, or null.</summary>
    public static string? Problem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
            return $"The password needs at least {MinimumLength} characters.";
        if (password.Length > MaximumLength) return $"The password can have at most {MaximumLength} characters.";
        return null;
    }

    public static async Task<PasswordRecord> CreateAsync(string password, DateTimeOffset now, string source, CancellationToken cancellationToken = default)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = await DeriveAsync(password, salt, Iterations, cancellationToken).ConfigureAwait(false);
        return new PasswordRecord
        {
            Algorithm = Algorithm,
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            SetAt = now,
            Source = source,
        };
    }

    public static async Task<bool> VerifyAsync(string? password, PasswordRecord? record, CancellationToken cancellationToken = default)
    {
        // L20: only the work this version asks for, up to ten times more for a later one: fewer would make a stolen file
        // cheap to guess against, and a huge count would tie up the two checks that may run at once.
        if (password is null || record is null || record.Algorithm != Algorithm ||
            record.Iterations is < Iterations or > MaximumIterations || password.Length > MaximumLength)
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(record.Salt);
            expected = Convert.FromBase64String(record.Hash);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length != HashBytes) return false;
        var actual = await DeriveAsync(password, salt, record.Iterations, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static async Task<byte[]> DeriveAsync(string password, byte[] salt, int iterations, CancellationToken cancellationToken)
    {
        await Work.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
                HashAlgorithmName.SHA256, HashBytes), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Work.Release();
        }
    }
}

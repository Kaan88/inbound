using DownloadManager.Contracts;

namespace DownloadManager.Mobile;

/// <summary>The checks and words of the SFTP key row (mockups y1 to y3): what a chosen key file is, why it cannot be used, what the Host's answers mean.</summary>
public static class SftpKeyFile
{
    /// <summary>The most a key file may hold, as on the desktop (SitesRules.MostKeyBytes).</summary>
    public const int MostKeyBytes = 64 * 1024;

    /// <summary>y2b: the words for a public key (the .pub file).</summary>
    public const string PublicKeyMessage = "That is a public key (the .pub file). Choose the private key instead: the file with the same name and no .pub ending.";

    public const string TooLargeMessage = "That file is too large to be a key. Choose the private key file.";
    public const string UnreadableMessage = "That file could not be read.";

    /// <summary>The Host's words (PuttyKeyFile) when a PuTTY key's passphrase is wrong or missing; they belong under the passphrase field (y2).</summary>
    public const string WrongPassphrase = "The passphrase doesn't open this key. Check it and try again.";
    public const string NeedsPassphrase = "This PuTTY key has a passphrase. Enter it as the key passphrase.";

    public static bool IsPassphraseProblem(string? message) => message is WrongPassphrase or NeedsPassphrase;

    public static bool IsPublicKey(string text)
    {
        var problem = SiteKeyRules.Problem(text);
        return problem is not null && problem.StartsWith("That is a public key", StringComparison.Ordinal);
    }

    /// <summary>Why <paramref name="text"/> cannot be a private key, in the words of the mockups; null when it can.</summary>
    public static string? Problem(string text)
    {
        if (SiteKeyRules.Problem(text) is not { } problem) return null;
        return problem.StartsWith("That is a public key", StringComparison.Ordinal) ? PublicKeyMessage : problem;
    }

    /// <summary>The line under the file name: "PuTTY key · Ed25519", "OpenSSH key", "Public key".</summary>
    public static string Kind(string text)
    {
        var trimmed = text.TrimStart();
        if (IsPublicKey(text)) return "Public key";
        if (trimmed.StartsWith("PuTTY-User-Key-File-", StringComparison.Ordinal))
        {
            int colon = trimmed.IndexOf(':');
            int end = trimmed.IndexOfAny(['\r', '\n'], colon < 0 ? 0 : colon);
            string algorithm = colon > 0 && end > colon ? trimmed[(colon + 1)..end].Trim() : "";
            string name = Algorithm(algorithm);
            return name.Length == 0 ? "PuTTY key" : "PuTTY key · " + name;
        }

        if (trimmed.StartsWith("-----BEGIN RSA PRIVATE KEY", StringComparison.Ordinal)) return "OpenSSH key · RSA";
        if (trimmed.StartsWith("-----BEGIN EC PRIVATE KEY", StringComparison.Ordinal)) return "OpenSSH key · ECDSA";
        if (trimmed.StartsWith("-----BEGIN DSA PRIVATE KEY", StringComparison.Ordinal)) return "OpenSSH key · DSA";
        return SiteKeyRules.Problem(text) is null ? "OpenSSH key" : "Not a private key";
    }

    private static string Algorithm(string name) => name switch
    {
        "ssh-ed25519" => "Ed25519",
        "ssh-ed448" => "Ed448",
        "ssh-rsa" => "RSA",
        "ssh-dss" => "DSA",
        _ when name.StartsWith("ecdsa-sha2-", StringComparison.Ordinal) => "ECDSA",
        _ => "",
    };

    /// <summary>The text as sent: trimmed, one kind of line ending, one final newline (as the desktop sends it).</summary>
    public static string Normalize(string text) => text.Trim().Replace("\r\n", "\n").Replace('\r', '\n') + "\n";
}

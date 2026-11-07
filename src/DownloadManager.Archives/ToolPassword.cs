using System.Security.Cryptography;
using System.Text;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives;

/// <summary>
/// How one password reaches 7-Zip: always on stdin, never on the command line, where other processes could read it.
/// Holds the secret, so it never appears in a message or log.
/// </summary>
internal sealed class ToolPassword
{
    private readonly byte[] _stdin;

    private ToolPassword(byte[] stdin)
    {
        _stdin = stdin;
    }

    /// <summary>
    /// For an archive that needs no password. 7-Zip has no switch that says so, and a closed stdin makes it stop
    /// with "Break signaled", so it gets an empty line: an encrypted archive then reports what it needs.
    /// </summary>
    public static ToolPassword None { get; } = new("\r\n"u8.ToArray());

    /// <summary>A copy for one run, which the runner clears after writing.</summary>
    public byte[] CopyStdinBytes() => (byte[])_stdin.Clone();

    /// <remarks>
    /// Every run passes <c>-sccUTF-8</c>, which on 7-Zip 26.04 also makes its prompt read UTF-8, so any password
    /// arrives intact. Without it the prompt reads the OEM code page.
    /// </remarks>
    public static ToolPassword From(string? password)
    {
        if (string.IsNullOrEmpty(password)) return None;

        // 7-Zip reads one line at its prompt.
        if (password.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            throw new ExtractionException(ExtractionFailureKind.WrongPassword, "An archive password cannot contain a line break.");
        }

        var encoded = Encoding.UTF8.GetBytes(password);
        var stdin = new byte[encoded.Length + 2];
        encoded.CopyTo(stdin, 0);
        stdin[^2] = (byte)'\r';
        stdin[^1] = (byte)'\n';
        CryptographicOperations.ZeroMemory(encoded);
        return new ToolPassword(stdin);
    }
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives;

/// <summary>How one password reaches UnRAR. Holds the secret, so it never appears in a message or log.</summary>
internal sealed class UnrarPassword
{
    private const int MaxStdinCharacters = 127;

    private readonly byte[]? _stdin;

    static UnrarPassword()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private UnrarPassword(string switchArgument, byte[]? stdin)
    {
        SwitchArgument = switchArgument;
        _stdin = stdin;
    }

    /// <summary>For an archive that needs no password. <c>-p-</c> never prompts.</summary>
    public static UnrarPassword None { get; } = new("-p-", null);

    public string SwitchArgument { get; }

    /// <summary>A copy for one run, which the runner clears after writing. Null means close stdin at once.</summary>
    public byte[]? CopyStdinBytes() => _stdin is null ? null : (byte[])_stdin.Clone();

    public static UnrarPassword From(string? password) => From(password, CurrentAnsiEncoding());

    internal static UnrarPassword From(string? password, Encoding ansi)
    {
        if (string.IsNullOrEmpty(password)) return None;

        // UnRAR reads one line at a prompt, and a command line cannot hold a NUL.
        if (password.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            throw new ExtractionException(ExtractionFailureKind.WrongPassword, "A RAR password cannot contain a line break.");
        }

        if (password.Length <= MaxStdinCharacters)
        {
            try
            {
                var encoded = ansi.GetBytes(password);
                var stdin = new byte[encoded.Length + 2];
                encoded.CopyTo(stdin, 0);
                stdin[^2] = (byte)'\r';
                stdin[^1] = (byte)'\n';
                CryptographicOperations.ZeroMemory(encoded);
                return new UnrarPassword("-p", stdin);
            }
            catch (EncoderFallbackException)
            {
                // Falls through to the command line below.
            }
        }

        // Documented tool limitation, and the only time a password leaves stdin. UnRAR reads
        // stdin in the Windows ANSI code page and cuts it at 127 characters, so a password with
        // characters outside that code page, or a longer one, cannot arrive intact that way.
        // RARINISWITCHES does not help either. While UnRAR runs, -p<value> is visible on its
        // command line to other processes of this user, or to administrators.
        return new UnrarPassword("-p" + password, null);
    }

    private static Encoding CurrentAnsiEncoding()
    {
        // Throws rather than best-fits: a look-alike byte would look like a wrong password.
        return Encoding.GetEncoding((int)GetAnsiCodePage(), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static uint GetAnsiCodePage() => OperatingSystem.IsWindows() ? GetACP() : 65001;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}

using System.Diagnostics;
using System.Security.Cryptography;
using DownloadManager.Persistence;

namespace DownloadManager.Host;

/// <summary>
/// MAC.5: the secret key that seals saved passwords and keys, in the user's login Keychain, in place of DPAPI on
/// Windows and the Secret Service on Linux: a copy of the data folder alone opens nothing. One generic password per
/// data folder (service <see cref="Service"/>, account the folder's path), base64. The commands go to
/// <c>security -i</c> on its standard input, so the key never shows in a process list.
/// </summary>
internal sealed class MacKeychainKeyStore : ISecretKeyStore
{
    public const string Service = "Inbound Download Manager secret key";
    private const string Security = "/usr/bin/security";
    private readonly string _account;

    private MacKeychainKeyStore(string dataFolder) => _account = Path.GetFullPath(dataFolder);

    public string Describe => "the login Keychain";

    /// <summary>Null when there is no Keychain to use (no <c>security</c>, or no login keychain); the reason is logged.</summary>
    public static MacKeychainKeyStore? TryOpen(string dataFolder, Action<string> log)
    {
        try
        {
            var (code, output, _) = Run(["default-keychain"], null);
            if (code == 0 && output.Trim().Length > 0) return new MacKeychainKeyStore(dataFolder);
            log("No login Keychain: security default-keychain gave none.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log($"No login Keychain: {ex.GetType().Name}");
        }

        return null;
    }

    public byte[]? TryRead()
    {
        var (code, output, error) = Run(["find-generic-password", "-s", Service, "-a", _account, "-w"], null);
        // 44: errSecItemNotFound, "The specified item could not be found in the keychain."
        if (code == 44 || error.Contains("could not be found", StringComparison.OrdinalIgnoreCase)) return null;
        if (code != 0) throw new IOException($"The login Keychain could not be read (security exit {code}).");
        try
        {
            return Convert.FromBase64String(output.Trim());
        }
        catch (FormatException)
        {
            throw new IOException("The login Keychain's Inbound key is not in the expected form.");
        }
    }

    public void Create(byte[] key)
    {
        var encoded = Convert.ToBase64String(key);
        // security -i's exit code doesn't say whether the command in it worked: the read-back below does.
        var (code, _, _) = Run(["-i"], AddCommand(_account, encoded) + "\n");
        if (code != 0) throw new IOException($"The key could not be kept in the login Keychain (security exit {code}).");
        if (TryRead() is not { } back || !CryptographicOperations.FixedTimeEquals(back, key))
            throw new IOException("The key kept in the login Keychain did not read back the same.");
    }

    /// <summary>One <c>security -i</c> line: arguments in double quotes, as its own parser takes them.</summary>
    public static string AddCommand(string account, string encodedKey) =>
        $"add-generic-password -U -s {Quote(Service)} -a {Quote(account)} -w {Quote(encodedKey)}";

    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static (int Code, string Output, string Error) Run(string[] arguments, string? input)
    {
        var start = new ProcessStartInfo(Security)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("security did not start.");
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            // A locked Keychain asks the user to unlock it; two minutes, as the Secret Service prompt.
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new IOException("The login Keychain did not answer (locked, and not unlocked in time?).");
        }

        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}

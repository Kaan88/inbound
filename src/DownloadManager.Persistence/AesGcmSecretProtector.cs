using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Persistence;

/// <summary>
/// AES-256-GCM with a 32-byte key file, for a server where there is no DPAPI. The key file lives in the data folder,
/// created with mode 600 (owner read and write only), so whoever can read the data volume's backup can read its
/// secrets: the backup carries the key with it, which is documented for the server.
/// A blob is <c>aesgcm:v1:</c> then base64 of key id (8) | nonce (12) | tag (16) | ciphertext. The purpose bytes are the
/// associated data, so a blob of one kind fails to open as another. The key id is how a blob sealed with another key
/// file is told apart from damage: that case is reported once through the warning callback, and the value reads as
/// nothing, never as itself.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    public const string BlobPrefix = "aesgcm:v1:";
    public const int KeyBytes = 32;
    public const string DefaultFileName = "secrets.key";

    private const int KeyIdBytes = 8;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly byte[] KeyIdLabel = Encoding.UTF8.GetBytes("Inbound.SecretKey.Id.v1");

    private readonly byte[] _key;
    private readonly byte[] _keyId;
    private readonly Action<string>? _warn;
    private int _warnedOtherKey;
    private int _warnedOtherProtector;

    private AesGcmSecretProtector(byte[] key, string keyFile, Action<string>? warn)
    {
        _key = key;
        _keyId = KeyId(key);
        KeyFile = keyFile;
        _warn = warn;
    }

    public string Prefix => BlobPrefix;

    /// <summary>The key file in use, for messages. Its contents are never logged.</summary>
    public string KeyFile { get; }

    /// <summary>
    /// Reads the key file, or creates it with a new random key when there is none. A file of the wrong size is refused
    /// with an exception that says what to do: guessing a key would make every saved secret unreadable without saying so.
    /// </summary>
    public static AesGcmSecretProtector OpenOrCreate(string keyFile, Action<string>? warn = null)
    {
        var full = Path.GetFullPath(keyFile);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!File.Exists(full)) TryCreate(full);

        var key = File.ReadAllBytes(full);
        if (key.Length != KeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException(
                $"The secret key file {full} holds {key.Length} bytes, not {KeyBytes}. Restore it from the backup it came with. " +
                "Deleting it starts a new key, and the saved passwords and keys then have to be entered again.");
        }

        if (!OperatingSystem.IsWindows()) Tighten(full, warn);
        return new AesGcmSecretProtector(key, full, warn);
    }

    /// <summary>
    /// <see cref="OpenOrCreate"/> for a data folder that may already hold sealed secrets. Before any store opens, the folder
    /// is searched for blobs, and when one was sealed with another key than the key file's (or the key file is gone, and
    /// a new one would be made), this refuses with <see cref="SecretKeyMismatchException"/>. Starting anyway would read
    /// those secrets as nothing, and the stores drop a value that reads as nothing on their next save, so restoring the
    /// right key file afterwards would recover nothing. <paramref name="discardUnreadable"/> is the owner's explicit
    /// "start anyway": the secrets are then entered again. A missing key file is not created while refusing, so putting
    /// the old one back is all it takes.
    /// </summary>
    public static AesGcmSecretProtector OpenForDataFolder(string keyFile, string dataFolder, bool discardUnreadable, Action<string>? warn = null)
    {
        var full = Path.GetFullPath(keyFile);
        var sealedWith = KeyIdsIn(dataFolder, skip: full);
        if (sealedWith.Count > 0 && !File.Exists(full) && !discardUnreadable)
        {
            throw new SecretKeyMismatchException(
                $"The secret key file {full} is missing, but {dataFolder} holds saved passwords and keys sealed with it. " +
                $"Restore the key file from the backup it came with. To start without them (they have to be entered again), " +
                $"set {DiscardVariable}=1 once.");
        }

        var protector = OpenOrCreate(full, warn);
        var foreign = sealedWith.Where(id => !id.SequenceEqual(protector._keyId)).Count();
        if (foreign > 0)
        {
            if (!discardUnreadable)
            {
                throw new SecretKeyMismatchException(
                    $"{dataFolder} holds saved passwords and keys sealed with another key than {full}. Restore the key file " +
                    $"that came with this data folder. To start without them (they have to be entered again), set {DiscardVariable}=1 once.");
            }

            warn?.Invoke($"Saved passwords and keys sealed with another key than {full} are discarded ({DiscardVariable}=1). Enter them again.");
        }

        return protector;
    }

    /// <summary>The owner's explicit choice to start with secrets this key can't open, which are then lost.</summary>
    public const string DiscardVariable = "DOWNLOADMANAGER_DISCARD_UNREADABLE_SECRETS";

    /// <summary>
    /// The key ids of every <see cref="BlobPrefix"/> blob in the data folder's files, logs left out (they never carry
    /// one). Each distinct id once. A file that can't be read is skipped: the stores report it themselves.
    /// </summary>
    internal static List<byte[]> KeyIdsIn(string dataFolder, string? skip = null)
    {
        var ids = new List<byte[]>();
        if (!Directory.Exists(dataFolder)) return ids;
        var logs = Path.Combine(Path.GetFullPath(dataFolder), "logs") + Path.DirectorySeparatorChar;
        // System.Text.Json writes + as \u002B by default (and a / may come as \/), so those spellings are read as well.
        var blob = new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(BlobPrefix) +
            @"((?:[A-Za-z0-9+/=]|\\u002[BbFf]|\\/)+)");
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in Directory.EnumerateFiles(dataFolder, "*", options))
        {
            var path = Path.GetFullPath(file);
            if (path.StartsWith(logs, StringComparison.Ordinal) || string.Equals(path, skip, StringComparison.Ordinal)) continue;
            string text;
            try
            {
                if (new FileInfo(path).Length > 64L * 1024 * 1024) continue;
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!text.Contains(BlobPrefix, StringComparison.Ordinal)) continue;
            foreach (System.Text.RegularExpressions.Match match in blob.Matches(text))
            {
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(match.Groups[1].Value
                        .Replace(@"\u002B", "+", StringComparison.OrdinalIgnoreCase)
                        .Replace(@"\u002F", "/", StringComparison.OrdinalIgnoreCase)
                        .Replace(@"\/", "/", StringComparison.Ordinal));
                }
                catch (FormatException)
                {
                    continue;
                }

                if (bytes.Length < KeyIdBytes + NonceBytes + TagBytes) continue;
                var id = bytes[..KeyIdBytes];
                if (!ids.Any(known => known.SequenceEqual(id))) ids.Add(id);
            }
        }

        return ids;
    }

    public string Protect(string plaintext, ReadOnlySpan<byte> purpose)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var blob = new byte[KeyIdBytes + NonceBytes + TagBytes + plain.Length];
        _keyId.CopyTo(blob, 0);
        var nonce = blob.AsSpan(KeyIdBytes, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(_key, TagBytes))
        {
            aes.Encrypt(nonce, plain, blob.AsSpan(KeyIdBytes + NonceBytes + TagBytes), blob.AsSpan(KeyIdBytes + NonceBytes, TagBytes), purpose);
        }

        CryptographicOperations.ZeroMemory(plain);
        return BlobPrefix + Convert.ToBase64String(blob);
    }

    public string? Unprotect(string protectedValue, ReadOnlySpan<byte> purpose)
    {
        if (!protectedValue.StartsWith(BlobPrefix, StringComparison.Ordinal))
        {
            if (protectedValue.StartsWith(DpapiSecretProtector.BlobPrefix, StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _warnedOtherProtector, 1) == 0)
                _warn?.Invoke("A saved secret was sealed by Windows (DPAPI) and can't be opened here. Enter it again.");
            return null;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(protectedValue[BlobPrefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (blob.Length < KeyIdBytes + NonceBytes + TagBytes) return null;
        if (!CryptographicOperations.FixedTimeEquals(blob.AsSpan(0, KeyIdBytes), _keyId))
        {
            if (Interlocked.Exchange(ref _warnedOtherKey, 1) == 0)
                _warn?.Invoke($"A saved secret was sealed with another key than {KeyFile}, so it can't be opened. " +
                    "Restore the key file that came with this data folder, or enter the secret again.");
            return null;
        }

        var plain = new byte[blob.Length - KeyIdBytes - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(blob.AsSpan(KeyIdBytes, NonceBytes), blob.AsSpan(KeyIdBytes + NonceBytes + TagBytes),
                blob.AsSpan(KeyIdBytes + NonceBytes, TagBytes), plain, purpose);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // Another purpose, or damage. Either way the value is unreadable, as a DPAPI blob of another purpose is.
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] KeyId(byte[] key)
    {
        var material = new byte[KeyIdLabel.Length + key.Length];
        KeyIdLabel.CopyTo(material, 0);
        key.CopyTo(material, KeyIdLabel.Length);
        var id = SHA256.HashData(material)[..KeyIdBytes];
        CryptographicOperations.ZeroMemory(material);
        return id;
    }

    /// <summary>
    /// Writes a new key to a file of its own, mode 600 from the moment it exists, then moves it into place without
    /// replacing one. Two processes starting together can't then end up with two keys: the second one's move fails and it
    /// reads the first one's file.
    /// </summary>
    private static void TryCreate(string keyFile)
    {
        var temporary = keyFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(key);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, keyFile, overwrite: false);
            }
            catch (IOException) when (File.Exists(keyFile))
            {
                // Another process made it first. Its key is the one.
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary key is only ever ours; the next start has its own name.
            }
        }
    }

    /// <summary>A key file others can read is made owner-only, and that is said, because a copy may already have been taken.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Tighten(string keyFile, Action<string>? warn)
    {
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var mode = File.GetUnixFileMode(keyFile);
        if ((mode & ~OwnerOnly) == 0) return;
        try
        {
            File.SetUnixFileMode(keyFile, OwnerOnly);
            warn?.Invoke($"The secret key file {keyFile} could be read by others (mode {Convert.ToString((int)mode, 8)}). It is owner-only now.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"The secret key file {keyFile} can be read by others (mode {Convert.ToString((int)mode, 8)}) and could not be changed: {ex.GetType().Name}.");
        }
    }
}

/// <summary>The data folder's saved secrets were sealed with another key than the key file's. The message says what to do.</summary>
public sealed class SecretKeyMismatchException(string message) : Exception(message);

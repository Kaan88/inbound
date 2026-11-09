using System.Security.Cryptography;
using DownloadManager.Persistence;
using Tmds.DBus.Protocol;

namespace DownloadManager.Host;

/// <summary>
/// The secret key in the session's keyring (the freedesktop Secret Service: KDE Wallet, GNOME Keyring, KeePassXC), in
/// place of DPAPI on Windows and a key file on the server. The keyring opens with the user's sign-in, so the key is out
/// of the data folder: a copy of the folder alone (a backup, another account) opens nothing. One item per data folder,
/// found by its attributes.
/// The "plain" session is used, as libsecret's own tools do when the bus is the user's own: the key crosses only this
/// user's session bus. When the keyring is locked and asks to be unlocked, the desktop shows its own prompt.
/// </summary>
internal sealed class SecretServiceKeyStore : ISecretKeyStore, IDisposable
{
    private const string Service = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string DefaultCollection = "/org/freedesktop/secrets/aliases/default";
    private const string Schema = "org.inbound.SecretKey";
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(2);

    private readonly DBusConnection _connection;
    private readonly string _dataFolder;
    private readonly string _session;

    private SecretServiceKeyStore(DBusConnection connection, string dataFolder, string session)
    {
        _connection = connection;
        _dataFolder = dataFolder;
        _session = session;
    }

    public string Describe => "the desktop keyring";

    /// <summary>Null when the session has no Secret Service; the reason is logged.</summary>
    public static SecretServiceKeyStore? TryOpen(string dataFolder, Action<string> log)
    {
        DBusConnection? connection = null;
        try
        {
            connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("there is no session bus."));
            connection.ConnectAsync().AsTask().GetAwaiter().GetResult();
            var session = connection.CallMethodAsync(BuildOpenSession(connection),
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    reader.ReadVariantValue();
                    return reader.ReadObjectPathAsString();
                }, null).GetAwaiter().GetResult();
            return new SecretServiceKeyStore(connection, Path.GetFullPath(dataFolder), session);
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or DBusReadException or IOException or InvalidOperationException)
        {
            log($"No desktop keyring: {ex.Message}");
            connection?.Dispose();
            return null;
        }
    }

    public byte[]? TryRead() => TryReadAsync().GetAwaiter().GetResult();

    public void Create(byte[] key) => CreateAsync(key).GetAwaiter().GetResult();

    private async Task<byte[]?> TryReadAsync()
    {
        var (unlocked, locked) = await _connection.CallMethodAsync(BuildSearch(_connection, _dataFolder),
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Paths(reader.ReadArrayOfObjectPath()), Paths(reader.ReadArrayOfObjectPath()));
            }, null).ConfigureAwait(false);
        var items = unlocked.ToList();
        if (items.Count == 0 && locked.Length > 0) items.AddRange(await UnlockAsync(locked).ConfigureAwait(false));
        if (items.Count == 0) return null;
        var secret = await _connection.CallMethodAsync(BuildGetSecret(_connection, items[0], _session),
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                reader.AlignStruct();
                reader.ReadObjectPathAsString();
                reader.ReadArrayOfByte();
                var value = reader.ReadArrayOfByte();
                reader.ReadString();
                return value;
            }, null).ConfigureAwait(false);
        return secret;
    }

    private async Task CreateAsync(byte[] key)
    {
        await UnlockAsync([DefaultCollection]).ConfigureAwait(false);
        var (item, prompt) = await _connection.CallMethodAsync(BuildCreateItem(_connection, _dataFolder, _session, key),
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
            }, null).ConfigureAwait(false);
        if (item == "/" && prompt != "/") await PromptAsync(prompt).ConfigureAwait(false);
        // Read back: a keyring that took the item but can't give it back would leave every saved secret unreadable.
        var check = await TryReadAsync().ConfigureAwait(false);
        var same = check is not null && CryptographicOperations.FixedTimeEquals(check, key);
        if (check is not null) CryptographicOperations.ZeroMemory(check);
        if (!same) throw new InvalidOperationException("The desktop keyring did not keep the secret key.");
    }

    /// <summary>The objects that are unlocked afterwards. A locked keyring shows the desktop's own unlock prompt.</summary>
    private async Task<string[]> UnlockAsync(string[] objects)
    {
        var (unlocked, prompt) = await _connection.CallMethodAsync(BuildUnlock(_connection, objects),
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Paths(reader.ReadArrayOfObjectPath()), reader.ReadObjectPathAsString());
            }, null).ConfigureAwait(false);
        if (prompt == "/") return unlocked;
        return await PromptAsync(prompt).ConfigureAwait(false) ? objects : [];
    }

    /// <summary>Shows a keyring prompt and waits for it. False when the user dismissed it.</summary>
    private async Task<bool> PromptAsync(string prompt)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = await _connection.WatchSignalAsync(Service, prompt, "org.freedesktop.Secret.Prompt", "Completed",
            static (message, _) => message.GetBodyReader().ReadBool(),
            (exception, dismissed) =>
            {
                if (exception is not null) completed.TrySetException(exception);
                else completed.TrySetResult(!dismissed);
            }, null, false, ObserverFlags.None).ConfigureAwait(false);
        await _connection.CallMethodAsync(BuildPrompt(_connection, prompt)).ConfigureAwait(false);
        try
        {
            return await completed.Task.WaitAsync(PromptTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The desktop keyring's prompt got no answer.");
        }
    }

    private static MessageBuffer BuildOpenSession(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "OpenSession", "sv");
        writer.WriteString("plain");
        writer.WriteVariantString(string.Empty);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildSearch(DBusConnection connection, string dataFolder)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "SearchItems", "a{ss}");
        WriteAttributes(ref writer, dataFolder);
        var message = writer.CreateMessage();
        writer.Dispose();
        return message;
    }

    private static MessageBuffer BuildUnlock(DBusConnection connection, string[] objects)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "Unlock", "ao");
        var array = writer.WriteArrayStart(DBusType.ObjectPath);
        foreach (var path in objects) writer.WriteObjectPath(path);
        writer.WriteArrayEnd(array);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildPrompt(DBusConnection connection, string prompt)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, prompt, "org.freedesktop.Secret.Prompt", "Prompt", "s");
        writer.WriteString(string.Empty);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildGetSecret(DBusConnection connection, string item, string session)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, item, "org.freedesktop.Secret.Item", "GetSecret", "o");
        writer.WriteObjectPath(session);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildCreateItem(DBusConnection connection, string dataFolder, string session, byte[] key)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, DefaultCollection, "org.freedesktop.Secret.Collection", "CreateItem", "a{sv}(oayays)b");
        var properties = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Label");
        writer.WriteVariantString($"{LinuxApp.Name} secret key ({dataFolder})");
        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Attributes");
        writer.WriteSignature("a{ss}");
        WriteAttributes(ref writer, dataFolder);
        writer.WriteDictionaryEnd(properties);
        writer.WriteStructureStart();
        writer.WriteObjectPath(session);
        writer.WriteArray(Array.Empty<byte>());
        writer.WriteArray(key);
        writer.WriteString("application/octet-stream");
        writer.WriteBool(false);
        var message = writer.CreateMessage();
        writer.Dispose();
        return message;
    }

    private static void WriteAttributes(ref MessageWriter writer, string dataFolder)
    {
        var attributes = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("xdg:schema");
        writer.WriteString(Schema);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("data-folder");
        writer.WriteString(dataFolder);
        writer.WriteDictionaryEnd(attributes);
    }

    private static string[] Paths(ObjectPath[] paths) => paths.Select(path => path.ToString()).ToArray();

    public void Dispose() => _connection.Dispose();
}

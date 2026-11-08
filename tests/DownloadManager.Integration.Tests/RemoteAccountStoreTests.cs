using DownloadManager.Core.Providers;
using DownloadManager.Persistence;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Integration.Tests;

public sealed class RemoteAccountStoreTests : IDisposable
{
    private const string Key = "0a1b2c3d-1111-4222-8333-444455556666";
    private const string Dashless = "0a1b2c3d111142228333444455556666";

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly List<string> _log = [];

    public RemoteAccountStoreTests()
    {
        _paths = new StoragePaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string AccountFile => Path.Combine(_paths.RemoteDirectory, "account-torbox.json");
    private Task<RemoteAccountStore> OpenAsync() => RemoteAccountStore.OpenAsync(_paths, "torbox", _log.Add);

    [Fact]
    public async Task NothingSavedMeansNoKey()
    {
        var store = await OpenAsync();
        Assert.Equal(RemoteKeyState.None, store.State);
        Assert.False(store.HasKey);
        Assert.Null(store.KeySource());
    }

    [Fact]
    public async Task SetKeepsTheKeyEncryptedAcrossARestart()
    {
        var store = await OpenAsync();
        var changes = 0;
        store.Changed += (_, _) => changes++;
        await store.SetKeyAsync($"  {Key}\r\n");

        Assert.Equal(RemoteKeyState.Present, store.State);
        Assert.True(store.HasKey);
        Assert.Equal(Key, store.KeySource());
        Assert.Equal(1, changes);

        var json = await File.ReadAllTextAsync(AccountFile);
        Assert.DoesNotContain(Key, json);
        Assert.DoesNotContain(Dashless, json);
        Assert.Contains(SecretProtector.Current.Prefix, json);
        Assert.Contains("\"provider\": \"torbox\"", json);

        var reopened = await OpenAsync();
        Assert.True(reopened.HasKey);
        Assert.Equal(Key, reopened.KeySource());
    }

    [Fact]
    public async Task ClearForgetsTheKeyAndTheFile()
    {
        var store = await OpenAsync();
        await store.SetKeyAsync(Key);
        var source = store.KeySource;
        await store.ClearAsync();

        Assert.Equal(RemoteKeyState.None, store.State);
        Assert.Null(source());
        Assert.False(File.Exists(AccountFile));
        Assert.Equal(RemoteKeyState.None, (await OpenAsync()).State);
    }

    [Fact]
    public async Task ClearingWithNothingSavedIsFine()
    {
        var store = await OpenAsync();
        await store.ClearAsync();
        Assert.Equal(RemoteKeyState.None, store.State);
    }

    [Fact]
    public async Task AKeySourceSeesAChangeAtOnce()
    {
        var store = await OpenAsync();
        var source = store.KeySource;
        Assert.Null(source());
        await store.SetKeyAsync(Key);
        Assert.Equal(Key, source());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has\u0001control")]
    public async Task UnusableKeysAreRefusedWithoutQuotingThem(string key)
    {
        var store = await OpenAsync();
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => store.SetKeyAsync(key));
        if (key.Trim().Length > 0) Assert.DoesNotContain(key, refused.Message);
        Assert.Equal(RemoteKeyState.None, store.State);
        Assert.False(File.Exists(AccountFile));
    }

    [Fact]
    [Trait("Platform", "Windows")] // the failure is made with a Windows share lock; Linux locks are only advisory
    public async Task AFailedSaveChangesNothing()
    {
        var store = await OpenAsync();
        await store.SetKeyAsync(Key);
        using (new FileStream(AccountFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => store.SetKeyAsync("ffffffff-1111-4222-8333-444455556666"));
        }

        Assert.Equal(Key, store.KeySource());
    }

    [Theory]
    [InlineData("dpapi:v1:AAAA")]
    [InlineData("dpapi:v1:not base64 at all")]
    [InlineData("plain-text-value")]
    public async Task ACiphertextThatWontDecryptIsUnreadableNotAbsent(string protectedKey)
    {
        await new RemoteAccountFile(_paths, "torbox").SaveAsync(new StoredRemoteAccount { Provider = "torbox", ProtectedKey = protectedKey });

        var store = await OpenAsync();
        Assert.Equal(RemoteKeyState.Unreadable, store.State);
        Assert.False(store.HasKey);
        Assert.Null(store.KeySource());
        Assert.Contains(_log, line => line.Contains("can't be read"));
        Assert.DoesNotContain(_log, line => line.Contains(protectedKey));
    }

    [Fact]
    public async Task ABlobFromAnotherPurposeCantStandInForTheKey()
    {
        await new RemoteAccountFile(_paths, "torbox").SaveAsync(new StoredRemoteAccount
        {
            Provider = "torbox",
            ProtectedKey = SecretProtector.ProtectProxy(Key),
        });
        Assert.Equal(RemoteKeyState.Unreadable, (await OpenAsync()).State);
        Assert.Null(SecretProtector.UnprotectProxy(SecretProtector.ProtectProviderKey(Key)));
        Assert.Null(SecretProtector.Unprotect(SecretProtector.ProtectProviderKey(Key)));
        Assert.Equal(Key, SecretProtector.UnprotectProviderKey(SecretProtector.ProtectProviderKey(Key)));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\": 99, \"protectedKey\": \"dpapi:v1:AAAA\"}")]
    public async Task ADamagedFileIsUnreadableAndStaysUntilReplaced(string content)
    {
        await File.WriteAllTextAsync(AccountFile, content);

        var store = await OpenAsync();
        Assert.Equal(RemoteKeyState.Unreadable, store.State);
        Assert.Equal(content, await File.ReadAllTextAsync(AccountFile));

        await store.SetKeyAsync(Key);
        Assert.True(store.HasKey);
        Assert.True((await OpenAsync()).HasKey);
    }

    [Fact]
    public async Task AnUnreadableKeyCanBeCleared()
    {
        await File.WriteAllTextAsync(AccountFile, "{ damaged");
        var store = await OpenAsync();
        await store.ClearAsync();
        Assert.Equal(RemoteKeyState.None, store.State);
    }

    [Fact]
    public async Task EachProviderHasItsOwnFile()
    {
        var one = await RemoteAccountStore.OpenAsync(_paths, "one");
        var other = await RemoteAccountStore.OpenAsync(_paths, "other");
        await one.SetKeyAsync(Key);
        Assert.True(one.HasKey);
        Assert.False(other.HasKey);
        Assert.True(File.Exists(Path.Combine(_paths.RemoteDirectory, "account-one.json")));
        Assert.False((await RemoteAccountStore.OpenAsync(_paths, "other")).HasKey);
        await Assert.ThrowsAsync<ArgumentException>(() => RemoteAccountStore.OpenAsync(_paths, "..\\evil"));
    }

    [Fact]
    public async Task TheStoreNeverPrintsItsKey()
    {
        var store = await OpenAsync();
        await store.SetKeyAsync(Key);
        Assert.DoesNotContain(Key, store.ToString());
        Assert.DoesNotContain(Key, string.Join('\n', _log));
    }

    [Theory]
    [InlineData("https://example.test/f?a=" + Key, true)]
    [InlineData("https://example.test/f?a=" + Dashless, true)]
    [InlineData("https://example.test/f?a=0A1B2C3D-1111-4222-8333-444455556666", true)]
    [InlineData("https://example.test/f?a=0a1b2c3d%2D1111%2D4222%2D8333%2D444455556666", true)]
    [InlineData("https://example.test/f?a=0a1b2c3d%252D1111%252D4222%252D8333%252D444455556666", true)]
    [InlineData("https://example.test/" + Key + "/file.bin", true)]
    [InlineData("https://example.test/" + Dashless, true)]
    [InlineData("https://" + Dashless + "@example.test/", true)]
    [InlineData("https://example.test/f#" + Key, true)]
    [InlineData("https://example.test/f?a=xx" + Dashless + "yy", false)]
    [InlineData("https://example.test/f?a=0a1b2c3d", false)]
    [InlineData("https://example.test/f", false)]
    [InlineData("", false)]
    public void TheKeyIsFoundInEveryFormButOnlyWhenItStandsAlone(string text, bool expected)
    {
        var pattern = new RemoteKeyPattern(Key);
        Assert.Equal(expected, pattern.Holds(text));
    }

    [Theory]
    [InlineData("https://example.test/f?a=" + Key + "&b=1", "https://example.test/f?a=***&b=1")]
    [InlineData("https://example.test/" + Dashless + "/file.bin", "https://example.test/***/file.bin")]
    [InlineData("https://example.test/f?a=" + Key + "&c=" + Dashless, "https://example.test/f?a=***&c=***")]
    [InlineData("https://example.test/f?a=0a1b2c3d%2D1111%2D4222%2D8333%2D444455556666&b=1", "https://example.test/***")]
    [InlineData("nothing here", "nothing here")]
    public void MaskingRemovesEveryForm(string text, string expected)
    {
        var pattern = new RemoteKeyPattern(Key);
        var masked = pattern.Mask(text);
        Assert.Equal(expected, masked);
        Assert.False(pattern.Holds(masked));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void AKeyTooShortToBeRealMatchesNothing(string? key)
    {
        var pattern = new RemoteKeyPattern(key);
        Assert.False(pattern.Holds("https://example.test/short"));
        Assert.Equal("https://example.test/short", pattern.Mask("https://example.test/short"));
    }

    [Fact]
    public async Task TheLogFileNeverGetsTheKeyWhateverTheLineSays()
    {
        var store = await OpenAsync();
        await store.SetKeyAsync(Key);
        var registry = new RemoteProviderRegistry();
        registry.Add(new NamedProvider("torbox"), store);
        var logger = new DownloadManager.Host.FileLogger(_paths.LogsDirectory) { Secrets = registry };

        logger.Info($"Clipboard URL detected: https://example.test/f?ref={Key}");
        logger.Warn($"bare {Dashless} value");
        logger.Error($"https://example.test/{Key}/file.bin");

        var written = string.Concat(Directory.GetFiles(_paths.LogsDirectory).Select(File.ReadAllText));
        Assert.DoesNotContain(Key, written);
        Assert.DoesNotContain(Dashless, written);
        Assert.Contains("bare ***", written);
        Assert.Contains("https://example.test/***/file.bin", written);
    }

    [Fact]
    public async Task TheRegistryAnswersForEveryAccount()
    {
        var registry = new RemoteProviderRegistry();
        var one = await RemoteAccountStore.OpenAsync(_paths, "one");
        await one.SetKeyAsync(Key);
        var provider = new NamedProvider("one");
        var entry = registry.Add(provider, one);
        registry.Add(new NamedProvider("other"));

        Assert.Same(entry, registry.Find("one"));
        Assert.Null(registry.Find("missing"));
        Assert.Null(registry.Find(null));
        Assert.Equal(2, registry.All.Count);
        Assert.Throws<ArgumentException>(() => registry.Add(new NamedProvider("one")));
        Assert.True(registry.Holds($"https://example.test/{Key}"));
        Assert.Equal("https://example.test/***", registry.Mask($"https://example.test/{Key}"));
        Assert.Null(entry.Catalog);
        Assert.Null(entry.LinkResolver);
        Assert.Null(entry.AccountProblem());
        Assert.Null(registry.Find("other")!.AccountProblem());
    }

    private sealed class NamedProvider(string id) : IRemoteProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public RemoteCapabilities Capabilities => RemoteCapabilities.None;
        public RemoteTransferPolicy TransferPolicy { get; } = new();
        public Task<RemoteAccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RemoteAccountInfo());
    }
}

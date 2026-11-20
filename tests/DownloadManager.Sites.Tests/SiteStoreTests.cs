using System.Text.Json.Nodes;
using DownloadManager.Persistence;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>
/// Sealing for Sites tests on any OS. Windows seals with DPAPI on its own; elsewhere this process gets an AES-GCM key file in
/// a temp folder of its own, once, as a server does at start (see PortableSecrets in the integration tests).
/// </summary>
internal static class TestSecrets
{
    private static readonly Lazy<bool> Ready = new(() =>
    {
        var keyFile = Path.Combine(Path.GetTempPath(), "inbound-sites-tests-" + Environment.ProcessId, AesGcmSecretProtector.DefaultFileName);
        SecretProtector.Use(AesGcmSecretProtector.OpenOrCreate(keyFile));
        return true;
    });

    public static void Ensure() => _ = Ready.Value;
}

/// <summary>SiteStore on a temp folder of its own: records, file shape, refusals, damaged files and concurrency.</summary>
public sealed class SiteStoreTests : IDisposable
{
    private const string Plain = "pw-plain-123";
    private const string ValidHostKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl";
    private readonly string _state;
    private readonly List<string> _logs = [];
    private readonly SiteStore _store;

    public SiteStoreTests()
    {
        TestSecrets.Ensure();
        _state = Path.Combine(Path.GetTempPath(), "inbound-sites-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_state);
        _store = new SiteStore(_state, line => _logs.Add(line));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_state)) Directory.Delete(_state, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by the OS is left for the temp cleanup; the test result does not depend on it.
        }
    }

    private static SiteRecord Site(string? id = null, string name = "Home", string host = "files.example.com") => new()
    {
        Id = id ?? SiteStore.NewId(),
        Name = name,
        Kind = SiteKinds.Sftp,
        Host = host,
        Port = 22,
        User = "alice",
        AuthMethod = SiteAuthMethods.Password,
        SealedPassword = SecretProtector.ProtectSiteSecret(Plain),
        HostKeys = [ValidHostKey],
        RemoteFolder = "/home/alice",
        LocalFolder = null,
        CreatedAt = DateTimeOffset.Parse("2026-11-14T10:00:00+01:00"),
        ModifiedAt = DateTimeOffset.Parse("2026-11-14T10:05:00+01:00"),
    };

    private string FileOf(string id) => Path.Combine(_state, "sites", id + ".json");

    private static void AssertSameSite(SiteRecord expected, SiteRecord actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Host, actual.Host);
        Assert.Equal(expected.Port, actual.Port);
        Assert.Equal(expected.User, actual.User);
        Assert.Equal(expected.AuthMethod, actual.AuthMethod);
        Assert.Equal(expected.SealedPassword, actual.SealedPassword);
        Assert.Equal(expected.SealedPrivateKey, actual.SealedPrivateKey);
        Assert.Equal(expected.SealedKeyPassphrase, actual.SealedKeyPassphrase);
        Assert.Equal(expected.HostKeys, actual.HostKeys);
        Assert.Equal(expected.RemoteFolder, actual.RemoteFolder);
        Assert.Equal(expected.LocalFolder, actual.LocalFolder);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.ModifiedAt, actual.ModifiedAt);
    }

    [Fact]
    public async Task A_file_with_null_stars_and_host_keys_reads_as_empty_lists()
    {
        var site = Site();
        await _store.SaveAsync(site);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(FileOf(site.Id)))!.AsObject();
        json["stars"] = null;
        json["hostKeys"] = null;
        await File.WriteAllTextAsync(FileOf(site.Id), json.ToJsonString());

        var loaded = await _store.GetAsync(site.Id);

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Stars);
        Assert.Empty(loaded.HostKeys);
    }

    [Fact]
    public async Task SavesAtTheSameWallClockTimeReceiveDifferentRevisions()
    {
        var site = Site();
        await _store.SaveAsync(site);
        var original = (await _store.GetAsync(site.Id))!;
        await _store.SaveAsync(original with { Name = "Edited at the same time" });
        var edited = (await _store.GetAsync(site.Id))!;
        Assert.Equal(original.ModifiedAt, edited.ModifiedAt);
        Assert.NotEqual(Guid.Empty, original.Revision);
        Assert.NotEqual(original.Revision, edited.Revision);
    }

    [Fact]
    public async Task Save_then_Get_returns_an_equal_record()
    {
        var site = Site();
        await _store.SaveAsync(site);

        var loaded = await _store.GetAsync(site.Id);

        Assert.NotNull(loaded);
        AssertSameSite(site, loaded);
        Assert.Equal(new[] { ValidHostKey }, loaded.HostKeys);
    }

    [Fact]
    public async Task The_saved_file_is_compact_and_holds_no_plaintext_password()
    {
        var site = Site();
        await _store.SaveAsync(site);

        var path = FileOf(site.Id);
        Assert.True(File.Exists(path));
        var text = await File.ReadAllTextAsync(path);

        Assert.DoesNotContain("\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.DoesNotContain("  ", text);
        Assert.DoesNotContain(Plain, text);
        Assert.StartsWith("{", text);
    }

    [Fact]
    public async Task List_returns_sites_sorted_by_name_ignoring_case()
    {
        await _store.SaveAsync(Site(name: "charlie"));
        await _store.SaveAsync(Site(name: "Alpha"));
        await _store.SaveAsync(Site(name: "bravo"));

        var names = (await _store.ListAsync()).Select(s => s.Name).ToList();

        Assert.Equal(new[] { "Alpha", "bravo", "charlie" }, names);
    }

    [Fact]
    public async Task Remove_returns_true_then_false_and_Get_is_null_after()
    {
        var site = Site();
        await _store.SaveAsync(site);

        Assert.True(await _store.RemoveAsync(site.Id));
        Assert.False(await _store.RemoveAsync(site.Id));
        Assert.Null(await _store.GetAsync(site.Id));
    }

    public static IEnumerable<object[]> BadIds() =>
    [
        ["../evil"],
        ["ABCDEF0123456789ABCDEF0123456789"],
        [""],
        ["0123456789abcdef0123456789abcde"],
    ];

    [Theory]
    [MemberData(nameof(BadIds))]
    public async Task Ids_that_are_not_32_lowercase_hex_are_refused_by_Get_Save_and_Remove(string id)
    {
        await Assert.ThrowsAsync<SiteStoreException>(() => _store.GetAsync(id));
        await Assert.ThrowsAsync<SiteStoreException>(() => _store.SaveAsync(Site(id: id)));
        await Assert.ThrowsAsync<SiteStoreException>(() => _store.RemoveAsync(id));
        Assert.False(Directory.Exists(Path.Combine(_state, "sites")) && Directory.EnumerateFiles(Path.Combine(_state, "sites")).Any());
    }

    [Fact]
    public async Task Save_refuses_an_empty_name_and_writes_nothing()
    {
        var site = Site(name: "");

        var ex = await Assert.ThrowsAsync<SiteStoreException>(() => _store.SaveAsync(site));

        Assert.Contains("name", ex.Message);
        Assert.False(File.Exists(FileOf(site.Id)));
    }

    [Theory]
    [InlineData("host/path")]
    [InlineData("user@host")]
    [InlineData("exa mple.com")]
    [InlineData("")]
    public async Task Save_refuses_a_host_with_a_path_user_or_space(string host)
    {
        var site = Site(host: host);

        await Assert.ThrowsAsync<SiteStoreException>(() => _store.SaveAsync(site));

        Assert.False(File.Exists(FileOf(site.Id)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task Save_refuses_a_port_out_of_range(int port)
    {
        var site = Site() with { Port = port };

        await Assert.ThrowsAsync<SiteStoreException>(() => _store.SaveAsync(site));
    }

    [Fact]
    public async Task Save_refuses_a_kind_other_than_sftp()
    {
        var site = Site() with { Kind = "ftp" };

        await Assert.ThrowsAsync<SiteStoreException>(() => _store.SaveAsync(site));
    }

    [Fact]
    public async Task Save_refuses_an_unsealed_password()
    {
        var site = Site() with { SealedPassword = Plain };

        var ex = await Assert.ThrowsAsync<SiteStoreException>(() => _store.SaveAsync(site));

        Assert.DoesNotContain(Plain, ex.Message);
        Assert.False(File.Exists(FileOf(site.Id)));
    }

    [Theory]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 not*base64")]
    [InlineData("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl")]
    [InlineData("ssh-ed25519 AAAA")]
    public async Task Save_refuses_a_host_key_that_is_not_type_and_base64(string entry)
    {
        var site = Site() with { HostKeys = [entry] };

        await Assert.ThrowsAsync<SiteStoreException>(() => _store.SaveAsync(site));
    }

    [Fact]
    public async Task A_damaged_file_is_skipped_by_List_and_Get_is_null_and_it_is_left_unchanged()
    {
        var site = Site();
        await _store.SaveAsync(site);
        var path = FileOf(site.Id);
        await File.WriteAllTextAsync(path, "{not json");

        var listed = await _store.ListAsync();
        var fetched = await _store.GetAsync(site.Id);

        Assert.Empty(listed);
        Assert.Null(fetched);
        Assert.Equal("{not json", await File.ReadAllTextAsync(path));
        // List and Get each report the file once; every line names the file and none of it quotes the content.
        Assert.NotEmpty(_logs);
        Assert.All(_logs, line =>
        {
            Assert.Contains(Path.GetFileName(path), line);
            Assert.DoesNotContain("{not json", line);
        });
    }

    [Fact]
    public async Task A_file_of_a_newer_schema_is_skipped_and_left_in_place()
    {
        var site = Site();
        await _store.SaveAsync(site);
        var path = FileOf(site.Id);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        node["schemaVersion"] = 99;
        var newer = node.ToJsonString();
        await File.WriteAllTextAsync(path, newer);

        var listed = await _store.ListAsync();
        var fetched = await _store.GetAsync(site.Id);

        Assert.Empty(listed);
        Assert.Null(fetched);
        Assert.Equal(newer, await File.ReadAllTextAsync(path));
        Assert.Contains(_logs, line => line.Contains("newer version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_whose_id_does_not_match_its_name_is_skipped()
    {
        var site = Site();
        await _store.SaveAsync(site);
        var other = SiteStore.NewId();
        File.Copy(FileOf(site.Id), FileOf(other));

        var listed = await _store.ListAsync();

        var only = Assert.Single(listed);
        Assert.Equal(site.Id, only.Id);
        Assert.Null(await _store.GetAsync(other));
        Assert.True(File.Exists(FileOf(other)));
        Assert.Contains(_logs, line => line.Contains("names another site", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Twenty_concurrent_saves_of_different_sites_all_land()
    {
        var sites = Enumerable.Range(0, 20).Select(i => Site(name: "site" + i.ToString("00"))).ToList();

        await Task.WhenAll(sites.Select(s => _store.SaveAsync(s)));

        var listed = await _store.ListAsync();
        Assert.Equal(20, listed.Count);
        Assert.Equal(sites.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal),
            listed.Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal));
    }
}

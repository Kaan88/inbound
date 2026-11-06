using System.Text.RegularExpressions;
using DownloadManager.Contracts;
using Xunit;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Chrome and Edge each register the same native-messaging manifest under their own HKCU key. These
/// tests cover the pure logic and the text of Setup and the scripts; none of them touches the registry.
/// </summary>
public sealed class NativeHostRegistrationTests
{
    private const string StoreOnly =
        """{ "name": "com.kaank.downloadmanager", "allowed_origins": [ "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/" ] }""";

    private const string StoreAndUnpacked =
        """{ "allowed_origins": [ "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/", "chrome-extension://pknddmneejadnnngffojnoecojhdfalc/" ] }""";

    private const string UnpackedOnly =
        """{ "allowed_origins": [ "chrome-extension://pknddmneejadnnngffojnoecojhdfalc/" ] }""";

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    [Fact]
    public void Both_browsers_have_their_own_key_for_the_same_host_name()
    {
        Assert.Equal(@"Software\Google\Chrome\NativeMessagingHosts\com.kaank.downloadmanager", BrowserBridge.ChromeNativeHostKeyPath);
        Assert.Equal(@"Software\Microsoft\Edge\NativeMessagingHosts\com.kaank.downloadmanager", BrowserBridge.EdgeNativeHostKeyPath);
        Assert.Equal(
            [BrowserBridge.ChromeNativeHostKeyPath, BrowserBridge.EdgeNativeHostKeyPath],
            BrowserBridge.NativeHostKeyPaths);
    }

    [Theory]
    [InlineData(StoreOnly, null, true)]
    [InlineData(null, StoreOnly, true)]
    [InlineData(StoreAndUnpacked, StoreAndUnpacked, true)]
    [InlineData(null, null, false)]
    [InlineData(UnpackedOnly, null, false)]
    [InlineData(null, UnpackedOnly, false)]
    [InlineData(UnpackedOnly, UnpackedOnly, false)]
    [InlineData("not json", "", false)]
    [InlineData("""{ "allowed_origins": "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/" }""", null, false)]
    [InlineData("""[ "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/" ]""", null, false)]
    public void Registered_means_a_browser_can_reach_the_host(string? chrome, string? edge, bool expected)
    {
        Assert.Equal(expected, NativeHostRegistrationState.Evaluate(chrome, edge).IsRegistered);
    }

    [Fact]
    public void A_chrome_only_install_is_registered_and_not_reported_broken()
    {
        var state = NativeHostRegistrationState.Evaluate(StoreAndUnpacked, null);
        Assert.True(state.ChromeUsable);
        Assert.False(state.EdgeUsable);
        Assert.True(state.IsRegistered);
    }

    [Fact]
    public void A_manifest_from_before_the_store_id_does_not_count_for_that_browser()
    {
        var state = NativeHostRegistrationState.Evaluate(UnpackedOnly, StoreOnly);
        Assert.False(state.ChromeUsable);
        Assert.True(state.EdgeUsable);
    }

    [Fact]
    public void Origins_are_compared_without_regard_to_case()
    {
        Assert.True(NativeHostRegistrationState.IsManifestUsable(
            """{ "allowed_origins": [ "CHROME-EXTENSION://OLHBCDBJDNFOIINLLNLPANDGGADFABFM/" ] }"""));
    }

    [Fact]
    public void A_chrome_only_install_gets_the_edge_key_pointing_at_the_same_manifest()
    {
        const string manifest = @"C:\Users\a\AppData\Local\Programs\Inbound Download Manager\native-host\com.kaank.downloadmanager.json";
        Assert.Equal(manifest, NativeHostRegistrationState.PlanEdgeRepair(manifest, chromeUsable: true, edgeKeyValue: null, edgeManifestExists: false));
        Assert.Equal(manifest, NativeHostRegistrationState.PlanEdgeRepair(manifest, true, "", false));
        // A stale Edge key whose file is gone is replaced.
        Assert.Equal(manifest, NativeHostRegistrationState.PlanEdgeRepair(manifest, true, @"C:\gone.json", false));
    }

    [Fact]
    public void The_edge_key_is_left_alone_when_there_is_nothing_to_repair_or_it_is_not_ours_to_replace()
    {
        const string manifest = @"C:\x\com.kaank.downloadmanager.json";
        // Edge already registered with a manifest that exists.
        Assert.Null(NativeHostRegistrationState.PlanEdgeRepair(manifest, true, @"C:\other\host.json", edgeManifestExists: true));
        // Chrome itself is not set up: nothing to copy.
        Assert.Null(NativeHostRegistrationState.PlanEdgeRepair(manifest, chromeUsable: false, edgeKeyValue: null, edgeManifestExists: false));
        Assert.Null(NativeHostRegistrationState.PlanEdgeRepair(null, true, null, false));
        Assert.Null(NativeHostRegistrationState.PlanEdgeRepair("  ", true, null, false));
    }

    [Fact]
    public void Setup_registers_both_keys_with_the_same_manifest_and_removes_both_on_uninstall()
    {
        var iss = Read(@"installer\DownloadManager.iss");
        var lines = iss.Split('\n')
            .Where(l => l.StartsWith("Root: HKCU; Subkey: \"Software\\", StringComparison.Ordinal) && l.Contains("NativeMessagingHosts"))
            .ToList();

        Assert.Equal(2, lines.Count);
        foreach (var path in BrowserBridge.NativeHostKeyPaths)
        {
            var expected = path.Replace("com.kaank.downloadmanager", "{#NativeHostName}");
            var line = Assert.Single(lines, l => l.Contains($"Subkey: \"{expected}\""));
            Assert.Contains("uninsdeletekey", line);
        }

        var values = lines.Select(l => Regex.Match(l, "ValueData: \"([^\"]+)\"").Groups[1].Value).Distinct().ToList();
        Assert.Single(values);
        Assert.Equal(@"{app}\native-host\{#NativeHostName}.json", values[0]);
    }

    [Fact]
    public void Register_script_writes_both_keys_to_one_manifest_and_checks_conflicts_before_writing()
    {
        var script = Read(@"scripts\register-native-host.ps1");
        AssertNamesBothKeys(script);

        // One manifest file, one loop that points every browser key at it.
        Assert.Single(Regex.Matches(script, @"WriteAllText\(\$manifestPath"));
        Assert.Single(Regex.Matches(script, @"Set-Item -Path \$key -Value \$manifestPath"));

        // The conflict check covers every key and runs before the manifest or any key is written,
        // so one conflicting key cannot leave the other half written.
        var conflict = script.IndexOf("A different native host is already registered", StringComparison.Ordinal);
        Assert.True(conflict > 0);
        Assert.True(conflict < script.IndexOf("WriteAllText($manifestPath", StringComparison.Ordinal));
        Assert.True(conflict < script.IndexOf("Set-Item -Path $key", StringComparison.Ordinal));
        Assert.Contains("Nothing was changed", script);
        Assert.Contains("$Force", script);
    }

    [Fact]
    public void Uninstall_script_removes_both_keys_only_when_they_are_ours()
    {
        var script = Read(@"scripts\uninstall.ps1");
        AssertNamesBothKeys(script);
        Assert.Contains("Remove-Item $nativeKey", script);
        Assert.Contains("$registered -ne $ownManifest", script);
        Assert.Contains(@"native-host\$hostName.json", script);
    }

    [Fact]
    public void Install_script_registers_through_the_shared_script_and_names_both_keys()
    {
        var script = Read(@"scripts\install.ps1");
        Assert.Contains("register-native-host.ps1", script);
        foreach (var path in BrowserBridge.NativeHostKeyPaths)
        {
            Assert.Contains("HKCU\\" + path, script);
        }
    }

    [Fact]
    public void Repair_and_update_register_through_the_script_that_writes_both_keys()
    {
        Assert.Contains("register-native-host.ps1", Read(@"scripts\repair.ps1"));
        Assert.Contains("register-native-host.ps1", Read(@"scripts\apply-update.ps1"));
        Assert.Contains(@"scripts\register-native-host.ps1", Read(@"installer\DownloadManager.iss"));
    }

    private static void AssertNamesBothKeys(string script)
    {
        // The scripts spell the key as HKCU:\<path> with the host name in a variable.
        foreach (var path in BrowserBridge.NativeHostKeyPaths)
        {
            var spelled = "HKCU:\\" + path.Replace(BrowserBridge.NativeHostName, "$hostName");
            Assert.Contains(spelled, script);
        }
    }
}

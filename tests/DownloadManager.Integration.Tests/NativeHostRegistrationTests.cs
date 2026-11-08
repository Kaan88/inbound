using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DownloadManager.Contracts;
using Xunit;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Chrome and Edge each register the same native-messaging manifest under their own HKCU key. These
/// tests cover the pure logic, the text of Setup and the scripts, and the scripts' behaviour under a
/// harness with a fake registry and the HKCU: drive removed. None of them touches the registry.
/// </summary>
public sealed class NativeHostRegistrationTests
{
    private const string StoreOnly =
        """{ "name": "com.kaank.downloadmanager", "path": "host.exe", "allowed_origins": [ "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/" ] }""";

    private const string StoreAndUnpacked =
        """{ "path": "host.exe", "allowed_origins": [ "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/", "chrome-extension://pknddmneejadnnngffojnoecojhdfalc/" ] }""";

    private const string UnpackedOnly =
        """{ "path": "host.exe", "allowed_origins": [ "chrome-extension://pknddmneejadnnngffojnoecojhdfalc/" ] }""";

    private const string ChromeKey = BrowserBridge.ChromeNativeHostKeyPath;
    private const string EdgeKey = BrowserBridge.EdgeNativeHostKeyPath;
    private const string FakeChromeKey = @"HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.kaank.downloadmanager";
    private const string FakeEdgeKey = @"HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.kaank.downloadmanager";
    private const string Ours = @"C:\install\native-host\com.kaank.downloadmanager.json";
    private const string OursExe = @"C:\install\native-host\host.exe";

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

    /// <summary>The file without its comment lines, so text in a comment cannot satisfy a test.</summary>
    private static string Code(string relativePath, string commentPrefix) => string.Join('\n',
        Read(relativePath).Split('\n').Where(l => !l.TrimStart().StartsWith(commentPrefix, StringComparison.Ordinal)));

    private static string ScriptPath(string name) => Path.Combine(RepoRoot(), "scripts", name);

    [Fact]
    public void Both_browsers_have_their_own_key_for_the_same_host_name()
    {
        Assert.Equal(@"Software\Google\Chrome\NativeMessagingHosts\com.kaank.downloadmanager", BrowserBridge.ChromeNativeHostKeyPath);
        Assert.Equal(@"Software\Microsoft\Edge\NativeMessagingHosts\com.kaank.downloadmanager", BrowserBridge.EdgeNativeHostKeyPath);
        Assert.Equal(
            [BrowserBridge.ChromeNativeHostKeyPath, BrowserBridge.EdgeNativeHostKeyPath],
            BrowserBridge.NativeHostKeyPaths);
    }

    // --- Pure state logic ---

    [Theory]
    [InlineData(null, StoreOnly, true, NativeHostKeyState.Absent)]
    [InlineData("  ", StoreOnly, true, NativeHostKeyState.Absent)]
    [InlineData(Ours, StoreOnly, true, NativeHostKeyState.Usable)]
    [InlineData(Ours, StoreAndUnpacked, true, NativeHostKeyState.Usable)]
    [InlineData(Ours, null, false, NativeHostKeyState.Broken)]
    [InlineData(Ours, UnpackedOnly, true, NativeHostKeyState.Broken)]
    [InlineData(Ours, StoreOnly, false, NativeHostKeyState.Broken)]
    [InlineData(Ours, "not json", true, NativeHostKeyState.Broken)]
    [InlineData(Ours, """{ "allowed_origins": "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/" }""", true, NativeHostKeyState.Broken)]
    [InlineData(Ours, """[ "chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/" ]""", true, NativeHostKeyState.Broken)]
    public void A_key_is_absent_usable_or_broken(string? keyValue, string? manifest, bool executableExists, NativeHostKeyState expected)
    {
        Assert.Equal(expected, NativeHostRegistrationState.EvaluateKey(keyValue, manifest, executableExists));
    }

    [Theory]
    [InlineData(NativeHostKeyState.Usable, NativeHostKeyState.Usable, true, "connected")]
    // Edge may not be installed, and a 0.4.0 install has no Edge key until the Host adds it.
    [InlineData(NativeHostKeyState.Usable, NativeHostKeyState.Absent, true, "connected")]
    [InlineData(NativeHostKeyState.Usable, NativeHostKeyState.Broken, true, "connected; Edge registration is broken")]
    [InlineData(NativeHostKeyState.Absent, NativeHostKeyState.Usable, true, "connected; not registered for Chrome")]
    [InlineData(NativeHostKeyState.Broken, NativeHostKeyState.Usable, true, "connected; Chrome registration is broken")]
    [InlineData(NativeHostKeyState.Absent, NativeHostKeyState.Absent, false, "native host not registered")]
    [InlineData(NativeHostKeyState.Broken, NativeHostKeyState.Absent, false, "native host not registered")]
    [InlineData(NativeHostKeyState.Broken, NativeHostKeyState.Broken, false, "native host not registered")]
    public void The_status_names_the_browser_that_cannot_reach_the_host(
        NativeHostKeyState chrome, NativeHostKeyState edge, bool registered, string text)
    {
        var state = new NativeHostRegistrationState(chrome, edge);
        Assert.Equal(registered, state.IsRegistered);
        Assert.Equal(text, state.IntegrationText(integrationEnabled: true));
        Assert.Equal("disabled", state.IntegrationText(integrationEnabled: false));
    }

    [Fact]
    public void Origins_are_compared_without_regard_to_case()
    {
        Assert.True(NativeHostRegistrationState.IsManifestUsable(
            """{ "allowed_origins": [ "CHROME-EXTENSION://OLHBCDBJDNFOIINLLNLPANDGGADFABFM/" ] }"""));
    }

    [Fact]
    public void A_manifest_program_is_resolved_against_the_manifest_folder()
    {
        Assert.Equal(OursExe, NativeHostRegistrationState.ManifestExecutable(StoreOnly, Ours));
        Assert.Equal(
            @"D:\app\host.exe",
            NativeHostRegistrationState.ManifestExecutable("""{ "path": "D:\\app\\host.exe" }""", Ours));
        Assert.Null(NativeHostRegistrationState.ManifestExecutable("""{ "name": "x" }""", Ours));
        Assert.Null(NativeHostRegistrationState.ManifestExecutable("not json", Ours));
        Assert.Null(NativeHostRegistrationState.ManifestExecutable(null, Ours));
    }

    [Fact]
    public void A_missing_key_or_one_whose_file_is_gone_is_planned_from_the_usable_key()
    {
        Assert.Equal(Ours, NativeHostRegistrationState.PlanKeyRepair(Ours, sourceUsable: true, targetKeyValue: null, targetManifestExists: false));
        Assert.Equal(Ours, NativeHostRegistrationState.PlanKeyRepair(Ours, true, "", false));
        Assert.Equal(Ours, NativeHostRegistrationState.PlanKeyRepair(Ours, true, @"C:\gone.json", false));
    }

    [Fact]
    public void A_key_is_left_alone_when_there_is_nothing_to_copy_or_it_is_not_ours_to_replace()
    {
        Assert.Null(NativeHostRegistrationState.PlanKeyRepair(Ours, true, @"C:\other\host.json", targetManifestExists: true));
        Assert.Null(NativeHostRegistrationState.PlanKeyRepair(Ours, sourceUsable: false, targetKeyValue: null, targetManifestExists: false));
        Assert.Null(NativeHostRegistrationState.PlanKeyRepair(null, true, null, false));
        Assert.Null(NativeHostRegistrationState.PlanKeyRepair("  ", true, null, false));
    }

    // --- Registrar over a fake store: the wiring between the reads, the plan and the write ---

    private sealed class FakeStore : INativeHostRegistrationStore
    {
        public Dictionary<string, string> Keys { get; } = [];
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExtraFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Key, string Value)> Writes { get; } = [];
        public Exception? WriteFailure { get; init; }
        public Exception? ReadFailure { get; init; }
        public int Reads { get; private set; }

        public string? ReadKeyValue(string keyPath)
        {
            Reads++;
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return Keys.GetValueOrDefault(keyPath);
        }

        public string? ReadTextFile(string path)
        {
            Reads++;
            return Files.GetValueOrDefault(path);
        }

        public bool FileExists(string path)
        {
            Reads++;
            return Files.ContainsKey(path) || ExtraFiles.Contains(path);
        }

        public void WriteKeyValue(string keyPath, string value)
        {
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }

            Writes.Add((keyPath, value));
            Keys[keyPath] = value;
        }

        /// <summary>Our manifest, on disk with the program it names.</summary>
        public FakeStore WithOurManifest()
        {
            Files[Ours] = StoreAndUnpacked;
            ExtraFiles.Add(OursExe);
            return this;
        }
    }

    [Fact]
    public void Repair_writes_the_edge_key_for_a_chrome_only_install()
    {
        var store = new FakeStore().WithOurManifest();
        store.Keys[ChromeKey] = Ours;

        Assert.Null(NativeHostRegistrar.TryRepair(store, null));

        Assert.Equal([(EdgeKey, Ours)], store.Writes);
        Assert.Equal(
            new NativeHostRegistrationState(NativeHostKeyState.Usable, NativeHostKeyState.Usable),
            NativeHostRegistrar.Read(store));
    }

    [Fact]
    public void Repair_writes_the_chrome_key_when_only_edge_works()
    {
        var store = new FakeStore().WithOurManifest();
        store.Keys[EdgeKey] = Ours;

        Assert.Null(NativeHostRegistrar.TryRepair(store, null));

        Assert.Equal([(ChromeKey, Ours)], store.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Repair_replaces_a_key_whose_manifest_is_gone(bool edgeIsTheDanglingOne)
    {
        var store = new FakeStore().WithOurManifest();
        store.Keys[edgeIsTheDanglingOne ? ChromeKey : EdgeKey] = Ours;
        store.Keys[edgeIsTheDanglingOne ? EdgeKey : ChromeKey] = @"C:\gone\com.kaank.downloadmanager.json";

        Assert.Null(NativeHostRegistrar.TryRepair(store, null));

        Assert.Equal([(edgeIsTheDanglingOne ? EdgeKey : ChromeKey, Ours)], store.Writes);
    }

    [Fact]
    public void Repair_leaves_an_existing_registration_alone_and_the_status_names_it_when_it_cannot_work()
    {
        const string other = @"C:\other\com.kaank.downloadmanager.json";
        var store = new FakeStore().WithOurManifest();
        store.Files[other] = UnpackedOnly;
        store.ExtraFiles.Add(@"C:\other\host.exe");
        store.Keys[ChromeKey] = Ours;
        store.Keys[EdgeKey] = other;

        Assert.Null(NativeHostRegistrar.TryRepair(store, null));

        Assert.Empty(store.Writes);
        var state = NativeHostRegistrar.Read(store);
        Assert.Equal(NativeHostKeyState.Broken, state.Edge);
        Assert.Equal("connected; Edge registration is broken", state.IntegrationText(true));
    }

    [Fact]
    public void A_manifest_whose_program_is_gone_is_broken_and_is_not_copied()
    {
        var store = new FakeStore();
        store.Files[Ours] = StoreAndUnpacked;
        store.Keys[ChromeKey] = Ours;

        Assert.Null(NativeHostRegistrar.TryRepair(store, null));

        Assert.Empty(store.Writes);
        Assert.Equal(NativeHostKeyState.Broken, NativeHostRegistrar.Read(store).Chrome);
    }

    [Fact]
    public void Repair_writes_nothing_when_both_keys_work_or_neither_does()
    {
        var both = new FakeStore().WithOurManifest();
        both.Keys[ChromeKey] = Ours;
        both.Keys[EdgeKey] = Ours;
        Assert.Null(NativeHostRegistrar.TryRepair(both, null));
        Assert.Empty(both.Writes);

        var neither = new FakeStore();
        Assert.Null(NativeHostRegistrar.TryRepair(neither, null));
        Assert.Empty(neither.Writes);
    }

    [Theory]
    [InlineData(@"C:\isolated-state")]
    [InlineData(@"relative")]
    public void Isolated_data_returns_before_any_read_or_write(string isolated)
    {
        var store = new FakeStore().WithOurManifest();
        store.Keys[ChromeKey] = Ours;

        Assert.Null(NativeHostRegistrar.TryRepair(store, isolated));

        Assert.Equal(0, store.Reads);
        Assert.Empty(store.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_isolated_data_repair_runs(string? isolated)
    {
        var store = new FakeStore().WithOurManifest();
        store.Keys[ChromeKey] = Ours;

        Assert.Null(NativeHostRegistrar.TryRepair(store, isolated));

        Assert.Single(store.Writes);
    }

    [Fact]
    public void A_write_failure_is_returned_for_the_log_and_the_state_does_not_change()
    {
        var store = new FakeStore { WriteFailure = new UnauthorizedAccessException("denied") }.WithOurManifest();
        store.Keys[ChromeKey] = Ours;

        Assert.Equal("denied", NativeHostRegistrar.TryRepair(store, null));

        Assert.Equal("connected", NativeHostRegistrar.Read(store).IntegrationText(true));
    }

    [Fact]
    public void A_key_that_cannot_be_read_counts_as_broken()
    {
        var store = new FakeStore { ReadFailure = new System.Security.SecurityException("denied") };

        Assert.False(NativeHostRegistrar.Read(store).IsRegistered);
        Assert.Equal(NativeHostKeyState.Broken, NativeHostRegistrar.Read(store).Chrome);
        Assert.Equal("denied", NativeHostRegistrar.TryRepair(store, null));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void The_host_passes_the_isolated_data_setting_and_repairs_at_start()
    {
        Assert.Contains(
            """NativeHostRegistrar.TryRepair(Store, Environment.GetEnvironmentVariable("DOWNLOADMANAGER_DATA"))""",
            Code(@"src\DownloadManager.Host\NativeHostRegistration.cs", "//"));
        Assert.Matches(@"(?m)^\s*TryRepairNativeHostKeys\(log\);", Code(@"src\DownloadManager.Host\Program.cs", "//"));
    }

    // --- Text of Setup and the scripts, with comments removed ---

    private static List<string> SetupKeyLines() => Code(@"installer\DownloadManager.iss", ";").Split('\n')
        .Where(l => l.StartsWith("Root: HKCU; Subkey: \"Software\\", StringComparison.Ordinal) && l.Contains("NativeMessagingHosts"))
        .ToList();

    [Fact]
    public void Setup_registers_both_keys_with_the_same_manifest()
    {
        // Firefox's key points at a manifest of its own; see the Firefox tests below.
        var lines = SetupKeyLines().Where(l => !l.Contains(@"Software\Mozilla")).ToList();

        Assert.Equal(2, lines.Count);
        foreach (var path in BrowserBridge.NativeHostKeyPaths)
        {
            var expected = path.Replace("com.kaank.downloadmanager", "{#NativeHostName}");
            Assert.Single(lines, l => l.Contains($"Subkey: \"{expected}\""));
        }

        var values = lines.Select(l => Regex.Match(l, "ValueData: \"([^\"]+)\"").Groups[1].Value).Distinct().ToList();
        Assert.Single(values);
        Assert.Equal(@"{app}\native-host\{#NativeHostName}.json", values[0]);
    }

    [Fact]
    public void In_setup_only_the_edge_key_may_fail_and_uninstall_decides_who_owns_a_key()
    {
        var lines = SetupKeyLines();
        Assert.DoesNotContain("noerror", Assert.Single(lines, l => l.Contains(@"Software\Google\Chrome")));
        Assert.Contains("noerror", Assert.Single(lines, l => l.Contains(@"Software\Microsoft\Edge")));
        Assert.All(lines, l => Assert.DoesNotContain("uninsdeletekey", l));

        var code = Code(@"installer\DownloadManager.iss", "//");
        Assert.Contains("procedure CurUninstallStepChanged", code);
        Assert.Matches(
            @"RegQueryStringValue\(HKCU, SubKey, '', Registered\) and FileExists\(Registered\) and\s+\(CompareText\(Registered, ExpandConstant\('\{app\}\\native-host\\\{#NativeHostName\}\.json'\)\) <> 0\) then\s+exit;\s+RegDeleteKeyIncludingSubkeys\(HKCU, SubKey\);",
            code);
        Assert.Contains(@"RemoveNativeHostKey('Software\Google\Chrome\NativeMessagingHosts\{#NativeHostName}');", code);
        Assert.Contains(@"RemoveNativeHostKey('Software\Microsoft\Edge\NativeMessagingHosts\{#NativeHostName}');", code);
    }

    [Fact]
    public void Uninstall_puts_back_another_programs_chrome_key_that_an_old_uninstall_log_deletes()
    {
        // Every release up to 0.5.3 wrote Chrome's key with uninsdeletekey, and an upgrade appends to
        // that uninstall log, so the old entry deletes the key after CurUninstallStepChanged ran.
        var code = Code(@"installer\DownloadManager.iss", "//");
        const string chrome = @"'Software\Google\Chrome\NativeMessagingHosts\{#NativeHostName}'";
        Assert.Matches(
            @"if CurUninstallStep = usUninstall then\s+begin\s+ForeignChromeRegistration := '';\s+if RegQueryStringValue\(HKCU, " + Regex.Escape(chrome) +
            @", '', Registered\) and\s+FileExists\(Registered\) and\s+\(CompareText\(Registered, ExpandConstant\('\{app\}\\native-host\\\{#NativeHostName\}\.json'\)\) <> 0\) then\s+ForeignChromeRegistration := Registered;",
            code);
        Assert.Matches(
            @"else if \(CurUninstallStep = usPostUninstall\) and \(ForeignChromeRegistration <> ''\) and\s+not RegValueExists\(HKCU, " + Regex.Escape(chrome) +
            @", ''\) then\s+RegWriteStringValue\(HKCU, " + Regex.Escape(chrome) + @", '', ForeignChromeRegistration\);",
            code);
    }

    // --- Firefox (EXT.E1): its own key and its own manifest, optional like Edge's ---

    private const string FakeFirefoxKey = @"HKCU:\Software\Mozilla\NativeMessagingHosts\com.kaank.downloadmanager";

    [Fact]
    public void Setup_registers_firefox_at_its_own_manifest_which_names_the_add_on_id()
    {
        var line = Assert.Single(SetupKeyLines(), l => l.Contains(@"Software\Mozilla"));
        var expected = BrowserBridge.FirefoxNativeHostKeyPath.Replace("com.kaank.downloadmanager", "{#NativeHostName}");
        Assert.Contains($"Subkey: \"{expected}\"", line);
        Assert.Contains(@"ValueData: ""{app}\native-host\{#NativeHostName}.firefox.json""", line);
        Assert.Contains("noerror", line);
        Assert.DoesNotContain("uninsdeletekey", line);

        var code = Code(@"installer\DownloadManager.iss", "//");
        Assert.Contains($"#define FirefoxExtensionId \"{BrowserBridge.FirefoxExtensionId}\"", code);
        Assert.Contains(@"WriteUtf8(ManifestDir + '\{#NativeHostName}.firefox.json',", code);
        Assert.Contains(@"'  ""allowed_extensions"": [ ""{#FirefoxExtensionId}"" ]'", code);
        Assert.Contains(@"RemoveFirefoxNativeHostKey('Software\Mozilla\NativeMessagingHosts\{#NativeHostName}');", code);
        Assert.Matches(
            @"RegQueryStringValue\(HKCU, SubKey, '', Registered\) and FileExists\(Registered\) and\s+\(CompareText\(Registered, ExpandConstant\('\{app\}\\native-host\\\{#NativeHostName\}\.firefox\.json'\)\) <> 0\) then\s+exit;\s+RegDeleteKeyIncludingSubkeys\(HKCU, SubKey\);",
            code);
    }

    [Fact]
    public void Register_script_writes_the_firefox_manifest_and_key()
    {
        using var sandbox = new ScriptSandbox();

        var result = RunWithFakeRegistry(sandbox.Root, ScriptPath("register-native-host.ps1"), $"-AppDir '{sandbox.AppDir}' -Force");

        Assert.True(result.ExitCode == 0, result.Output);
        var firefoxManifest = Path.Combine(sandbox.Root, "native-host", BrowserBridge.FirefoxManifestFileName);
        Assert.Equal(firefoxManifest, result.Hive[FakeFirefoxKey]);
        Assert.Contains("for Chrome and Edge and Firefox", result.Output);
        using var manifest = JsonDocument.Parse(File.ReadAllText(firefoxManifest));
        Assert.Equal(BrowserBridge.FirefoxExtensionId, Assert.Single(manifest.RootElement.GetProperty("allowed_extensions").EnumerateArray()).GetString());
        Assert.False(manifest.RootElement.TryGetProperty("allowed_origins", out _));
        Assert.Equal(Path.Combine(sandbox.AppDir, "DownloadManager.NativeHost.exe"), manifest.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void Register_script_survives_a_denied_firefox_key()
    {
        using var sandbox = new ScriptSandbox();

        var result = RunWithFakeRegistry(
            sandbox.Root, ScriptPath("register-native-host.ps1"), $"-AppDir '{sandbox.AppDir}' -Force", deniedKeys: FakeFirefoxKey);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(result.Hive.ContainsKey(FakeFirefoxKey));
        Assert.Equal(sandbox.ManifestPath, result.Hive[FakeChromeKey]);
        Assert.Contains("Could not register com.kaank.downloadmanager for Firefox", result.Output);
    }

    [Fact]
    public void Register_script_refuses_another_programs_firefox_registration()
    {
        using var sandbox = new ScriptSandbox();
        var other = Path.Combine(sandbox.Root, "other.json");
        File.WriteAllText(other, "{}");

        var result = RunWithFakeRegistry(
            sandbox.Root, ScriptPath("register-native-host.ps1"), $"-AppDir '{sandbox.AppDir}'",
            new Dictionary<string, string> { [FakeFirefoxKey] = other });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("A different native host is already registered for: Firefox", result.Output);
        Assert.Empty(result.Writes);
    }

    [Fact]
    public void Uninstall_checks_firefox_against_its_own_manifest()
    {
        var script = Code(@"scripts\uninstall.ps1", "#");
        Assert.Contains(@"""Firefox"" = ""HKCU:\Software\Mozilla\NativeMessagingHosts\$hostName""", script);
        Assert.Contains(@"$firefoxManifest = Join-Path $installRoot ""native-host\$hostName.firefox.json""", script);
        Assert.Contains(@"$ownManifest = if ($browser -eq ""Firefox"") { $firefoxManifest } else { $chromiumManifest }", script);
    }

    [Fact]
    public void Setup_does_not_state_an_unverified_edge_prompt_as_fact()
    {
        var iss = Read(@"installer\DownloadManager.iss");
        Assert.Contains("Edge may ask you to allow extensions from other stores.", iss);
        Assert.DoesNotContain("Edge asks you", iss);
    }

    [Fact]
    public void Install_script_registers_through_the_shared_script_and_names_both_keys()
    {
        var script = Code(@"scripts\install.ps1", "#");
        Assert.Matches(@"(?m)^& \(Join-Path \$PSScriptRoot ""register-native-host\.ps1""\) -AppDir \$appDir -Force", script);
        foreach (var path in BrowserBridge.NativeHostKeyPaths)
        {
            Assert.Contains("HKCU\\" + path, script);
        }
    }

    [Fact]
    public void Repair_and_update_call_the_script_next_to_them_and_setup_and_the_package_carry_it()
    {
        Assert.Matches(
            @"(?m)^& \(Join-Path \$PSScriptRoot ""register-native-host\.ps1""\) -AppDir \$AppDir -Force",
            Code(@"scripts\repair.ps1", "#"));
        Assert.Matches(
            @"\$register = Join-Path \$PackageDir ""register-native-host\.ps1""\s+if \(Test-Path \$register\) \{\s+& \$register -AppDir \$appDir -Force",
            Code(@"scripts\apply-update.ps1", "#"));
        Assert.Contains(@"Source: ""..\scripts\register-native-host.ps1""", Code(@"installer\DownloadManager.iss", ";"));
        Assert.Contains(@"Join-Path $PSScriptRoot ""register-native-host.ps1""", Code(@"scripts\package.ps1", "#"));
        Assert.Contains(@"Join-Path $PSScriptRoot ""repair.ps1""", Code(@"scripts\package.ps1", "#"));
    }

    [Fact]
    public void Uninstall_normalizes_the_root_and_removes_a_key_only_through_the_ownership_check()
    {
        var script = Code(@"scripts\uninstall.ps1", "#");
        Assert.Matches(@"\$installRoot = \[IO\.Path\]::GetFullPath\(\$InstallRoot\)\.TrimEnd\('\\'\)", script);
        Assert.Matches(
            @"if \(-not \(Test-NativeKeyIsOurs \$registered \$ownManifest\)\) \{\s+Write-Warning[^\n]+\s+continue\s+\}\s+Remove-Item \$nativeKey -Recurse -Force",
            script);
        // The keys are checked while the manifest is still on disk.
        Assert.True(script.IndexOf("Test-NativeKeyIsOurs $registered", StringComparison.Ordinal)
            < script.IndexOf("'native-host'", StringComparison.Ordinal));
    }

    // --- Script behaviour under a harness: fake registry, HKCU: drive removed ---

    private sealed record HarnessResult(int ExitCode, string Output, Dictionary<string, string> Hive, List<string> Writes);

    /// <summary>
    /// Runs a script in a fresh PowerShell with New-Item, Set-Item, Test-Path and Get-Item shadowed for
    /// HKCU: paths over an in-memory hive. The HKCU: drive is removed first, so a call that escaped the
    /// fakes would fail with "drive not found" instead of reaching the registry.
    /// </summary>
    private static HarnessResult RunWithFakeRegistry(
        string directory, string script, string arguments, Dictionary<string, string>? seed = null, params string[] deniedKeys)
    {
        var resultPath = Path.Combine(directory, "harness-result.json");
        var seedPath = Path.Combine(directory, "harness-seed.json");
        var deniedPath = Path.Combine(directory, "harness-denied.json");
        File.WriteAllText(seedPath, JsonSerializer.Serialize(seed ?? []));
        File.WriteAllText(deniedPath, JsonSerializer.Serialize(deniedKeys));
        var harness = Path.Combine(directory, "harness.ps1");
        File.WriteAllText(harness, $$"""
            $ErrorActionPreference = 'Stop'
            Remove-PSDrive -Name HKCU -Force
            if (Get-PSDrive -Name HKCU -ErrorAction SilentlyContinue) { throw 'The HKCU drive is still there.' }
            $global:hive = @{}
            (Get-Content -LiteralPath '{{seedPath}}' -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $global:hive[$_.Name] = $_.Value }
            $global:denied = @((Get-Content -LiteralPath '{{deniedPath}}' -Raw | ConvertFrom-Json) | ForEach-Object { $_ })
            $global:writes = New-Object System.Collections.ArrayList
            function global:Test-FakeWrite([string]$key) {
                if ($global:denied -contains $key) { throw [UnauthorizedAccessException]::new("Requested registry access is not allowed: $key") }
                [void]$global:writes.Add($key)
            }
            function global:Test-Path {
                param([Parameter(Position = 0)]$Path, $LiteralPath, $PathType)
                $target = if ($LiteralPath) { "$LiteralPath" } else { "$Path" }
                if ($target -like 'HKCU:*') { return $global:hive.ContainsKey($target) }
                if ($PathType) { return Microsoft.PowerShell.Management\Test-Path -LiteralPath $target -PathType $PathType }
                Microsoft.PowerShell.Management\Test-Path -LiteralPath $target
            }
            function global:Get-Item {
                param([Parameter(Position = 0)]$Path, $LiteralPath, [switch]$Force)
                $target = if ($LiteralPath) { "$LiteralPath" } else { "$Path" }
                if ($target -like 'HKCU:*') {
                    $value = $global:hive[$target]
                    $item = New-Object psobject
                    $item | Add-Member -MemberType ScriptMethod -Name GetValue -Value ([scriptblock]::Create("param(`$name) '$value'"))
                    return $item
                }
                Microsoft.PowerShell.Management\Get-Item -LiteralPath $target -Force:$Force
            }
            function global:New-Item {
                param($Path, $ItemType, [switch]$Force)
                if ("$Path" -like 'HKCU:*') { Test-FakeWrite "$Path"; if (-not $global:hive.ContainsKey("$Path")) { $global:hive["$Path"] = '' }; return }
                Microsoft.PowerShell.Management\New-Item -Path $Path -ItemType $ItemType -Force:$Force
            }
            function global:Set-Item {
                param($Path, $Value)
                if ("$Path" -notlike 'HKCU:*') { throw "Unexpected Set-Item: $Path" }
                Test-FakeWrite "$Path"
                $global:hive["$Path"] = $Value
            }
            $code = 0
            try { & '{{script}}' {{arguments}} }
            catch { Write-Output "THREW: $($_.Exception.Message)"; $code = 1 }
            if ($LASTEXITCODE) { $code = $LASTEXITCODE }
            [IO.File]::WriteAllText('{{resultPath}}', (@{ hive = $global:hive; writes = @($global:writes) } | ConvertTo-Json -Depth 4))
            exit $code
            """);

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        output += error.GetAwaiter().GetResult();

        Assert.True(File.Exists(resultPath), $"The harness did not finish: {output}");
        using var result = JsonDocument.Parse(File.ReadAllText(resultPath));
        var hive = result.RootElement.GetProperty("hive").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
        var writes = result.RootElement.GetProperty("writes").ValueKind == JsonValueKind.Array
            ? result.RootElement.GetProperty("writes").EnumerateArray().Select(w => w.GetString() ?? string.Empty).ToList()
            : [];
        return new HarnessResult(process.ExitCode, output, hive, writes);
    }

    private sealed class ScriptSandbox : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"))).FullName;
        public string AppDir => Path.Combine(Root, "app");
        public string ManifestPath => Path.Combine(Root, "native-host", "com.kaank.downloadmanager.json");

        public ScriptSandbox()
        {
            Directory.CreateDirectory(AppDir);
            // Never run: the script only checks that it exists and records its path.
            File.WriteAllText(Path.Combine(AppDir, "DownloadManager.NativeHost.exe"), "placeholder");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Register_script_points_both_keys_at_one_manifest()
    {
        using var sandbox = new ScriptSandbox();

        var result = RunWithFakeRegistry(sandbox.Root, ScriptPath("register-native-host.ps1"), $"-AppDir '{sandbox.AppDir}' -Force");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(sandbox.ManifestPath, result.Hive[FakeChromeKey]);
        Assert.Equal(sandbox.ManifestPath, result.Hive[FakeEdgeKey]);
        Assert.Contains("Registered com.kaank.downloadmanager for Chrome and Edge", result.Output);

        var manifest = File.ReadAllText(sandbox.ManifestPath);
        Assert.True(NativeHostRegistrationState.IsManifestUsable(manifest));
        Assert.Equal(
            Path.Combine(sandbox.AppDir, "DownloadManager.NativeHost.exe"),
            NativeHostRegistrationState.ManifestExecutable(manifest, sandbox.ManifestPath));
    }

    [Fact]
    public void Register_script_survives_a_denied_edge_key_and_still_writes_chrome()
    {
        using var sandbox = new ScriptSandbox();

        var result = RunWithFakeRegistry(
            sandbox.Root, ScriptPath("register-native-host.ps1"), $"-AppDir '{sandbox.AppDir}' -Force", deniedKeys: FakeEdgeKey);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(sandbox.ManifestPath, result.Hive[FakeChromeKey]);
        Assert.False(result.Hive.ContainsKey(FakeEdgeKey));
        Assert.Contains("Could not register com.kaank.downloadmanager for Edge", result.Output);
        Assert.Contains("Registered com.kaank.downloadmanager for Chrome", result.Output);
        Assert.DoesNotContain("Chrome and Edge", result.Output);
    }

    [Fact]
    public void Register_script_fails_when_the_chrome_key_is_denied()
    {
        using var sandbox = new ScriptSandbox();

        var result = RunWithFakeRegistry(
            sandbox.Root, ScriptPath("register-native-host.ps1"), $"-AppDir '{sandbox.AppDir}' -Force", deniedKeys: FakeChromeKey);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("THREW", result.Output);
    }

    [Fact]
    public void Register_script_refuses_a_conflict_in_either_key_before_writing_anything()
    {
        using var sandbox = new ScriptSandbox();
        var other = Path.Combine(sandbox.Root, "other.json");
        File.WriteAllText(other, "{}");

        var result = RunWithFakeRegistry(
            sandbox.Root, ScriptPath("register-native-host.ps1"), $"-AppDir '{sandbox.AppDir}'",
            new Dictionary<string, string> { [FakeEdgeKey] = other });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("A different native host is already registered for: Edge", result.Output);
        Assert.Empty(result.Writes);
        Assert.False(File.Exists(sandbox.ManifestPath));
    }

    [Fact]
    public void Register_script_treats_a_key_whose_manifest_is_gone_as_no_conflict()
    {
        using var sandbox = new ScriptSandbox();
        var gone = Path.Combine(sandbox.Root, "gone", "com.kaank.downloadmanager.json");

        var result = RunWithFakeRegistry(
            sandbox.Root, ScriptPath("register-native-host.ps1"), $"-AppDir '{sandbox.AppDir}'",
            new Dictionary<string, string> { [FakeEdgeKey] = gone });

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(sandbox.ManifestPath, result.Hive[FakeEdgeKey]);
        Assert.Equal(sandbox.ManifestPath, result.Hive[FakeChromeKey]);
    }

    private static (string Package, string Install, string ResultPath) BuildUpdate(ScriptSandbox sandbox, string registerScript)
    {
        var install = Path.Combine(sandbox.Root, "install");
        Directory.CreateDirectory(Path.Combine(install, "app"));
        File.WriteAllText(Path.Combine(install, "app", "DownloadManager.Host.exe"), "old");
        File.WriteAllText(Path.Combine(install, "install.json"), """{ "version": "0.4.0" }""");
        File.WriteAllText(Path.Combine(install, "register-native-host.ps1"), "# old register");
        File.WriteAllText(Path.Combine(install, "repair.ps1"), "# old repair");

        var package = Path.Combine(sandbox.Root, "package");
        Directory.CreateDirectory(Path.Combine(package, "app"));
        File.WriteAllText(Path.Combine(package, "app", "DownloadManager.Host.exe"), "new");
        File.WriteAllText(Path.Combine(package, "app", "DownloadManager.NativeHost.exe"), "new");
        File.WriteAllText(Path.Combine(package, "register-native-host.ps1"), registerScript);
        File.WriteAllText(Path.Combine(package, "repair.ps1"), "# new repair");

        return (package, install, Path.Combine(sandbox.Root, "updates", "result.json"));
    }

    [Fact]
    public void An_update_replaces_the_root_scripts_setup_installed_and_registers_both_keys()
    {
        using var sandbox = new ScriptSandbox();
        var registerScript = File.ReadAllText(ScriptPath("register-native-host.ps1"));
        var (package, install, resultPath) = BuildUpdate(sandbox, registerScript);

        var result = RunWithFakeRegistry(
            sandbox.Root, ScriptPath("apply-update.ps1"),
            $"-PackageDir '{package}' -InstallRoot '{install}' -Version '0.5.0' -ResultPath '{resultPath}' -NoStart");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "app", "DownloadManager.Host.exe")));
        Assert.Equal(registerScript, File.ReadAllText(Path.Combine(install, "register-native-host.ps1")));
        Assert.Equal("# new repair", File.ReadAllText(Path.Combine(install, "repair.ps1")));
        Assert.False(File.Exists(Path.Combine(install, "repair.ps1.tmp")));
        var manifest = Path.Combine(install, "native-host", "com.kaank.downloadmanager.json");
        Assert.Equal(manifest, result.Hive[FakeChromeKey]);
        Assert.Equal(manifest, result.Hive[FakeEdgeKey]);
    }

    [Fact]
    public void A_denied_edge_key_does_not_fail_an_update()
    {
        using var sandbox = new ScriptSandbox();
        var (package, install, resultPath) = BuildUpdate(sandbox, File.ReadAllText(ScriptPath("register-native-host.ps1")));

        var result = RunWithFakeRegistry(
            sandbox.Root, ScriptPath("apply-update.ps1"),
            $"-PackageDir '{package}' -InstallRoot '{install}' -Version '0.5.0' -ResultPath '{resultPath}' -NoStart",
            deniedKeys: FakeEdgeKey);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "app", "DownloadManager.Host.exe")));
        Assert.Contains("\"succeeded\"", File.ReadAllText(resultPath));
    }

    [Fact]
    public void A_failed_update_puts_the_root_scripts_back()
    {
        using var sandbox = new ScriptSandbox();
        var (package, install, resultPath) = BuildUpdate(sandbox, "throw 'register failed'");

        var result = RunWithFakeRegistry(
            sandbox.Root, ScriptPath("apply-update.ps1"),
            $"-PackageDir '{package}' -InstallRoot '{install}' -Version '0.5.0' -ResultPath '{resultPath}' -NoStart");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "app", "DownloadManager.Host.exe")));
        Assert.Equal("# old register", File.ReadAllText(Path.Combine(install, "register-native-host.ps1")));
        Assert.Equal("# old repair", File.ReadAllText(Path.Combine(install, "repair.ps1")));
        Assert.Contains("\"failed\"", File.ReadAllText(resultPath));
    }

    [Fact]
    public void An_update_adds_no_root_scripts_to_an_install_that_has_none()
    {
        using var sandbox = new ScriptSandbox();
        var (package, install, resultPath) = BuildUpdate(sandbox, File.ReadAllText(ScriptPath("register-native-host.ps1")));
        File.Delete(Path.Combine(install, "register-native-host.ps1"));
        File.Delete(Path.Combine(install, "repair.ps1"));

        var result = RunWithFakeRegistry(
            sandbox.Root, ScriptPath("apply-update.ps1"),
            $"-PackageDir '{package}' -InstallRoot '{install}' -Version '0.5.0' -ResultPath '{resultPath}' -NoStart");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(File.Exists(Path.Combine(install, "register-native-host.ps1")));
        Assert.False(File.Exists(Path.Combine(install, "repair.ps1")));
    }

    // uninstall.ps1 stops processes by name and removes the real shortcut, so it is never run. Only its
    // ownership function is, lifted out of the file with the PowerShell parser.
    private static string RunUninstallOwnershipCheck(string directory, string registered, string ownManifest)
    {
        var script = ScriptPath("uninstall.ps1");
        var probe = Path.Combine(directory, "probe.ps1");
        File.WriteAllText(probe, $$"""
            $ErrorActionPreference = 'Stop'
            Remove-PSDrive -Name HKCU -Force
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{script}}', [ref]$null, [ref]$null)
            $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-NativeKeyIsOurs' }, $true)
            Invoke-Expression $function.Extent.Text
            Write-Output (Test-NativeKeyIsOurs '{{registered}}' '{{ownManifest}}')
            """);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", probe })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output;
    }

    [Fact]
    public void Uninstall_owns_a_key_at_this_manifest_however_the_root_was_spelled()
    {
        using var sandbox = new ScriptSandbox();
        var manifest = Path.Combine(sandbox.Root, "custom", "native-host", "com.kaank.downloadmanager.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "{}");

        Assert.Equal("True", RunUninstallOwnershipCheck(sandbox.Root, manifest, @".\custom\native-host\com.kaank.downloadmanager.json"));
        Assert.Equal("True", RunUninstallOwnershipCheck(sandbox.Root, manifest,
            Path.Combine(sandbox.Root, "x", "..", "custom", "native-host", "com.kaank.downloadmanager.json")));
        Assert.Equal("True", RunUninstallOwnershipCheck(sandbox.Root, manifest.ToUpperInvariant(), manifest));
    }

    [Fact]
    public void Uninstall_removes_an_empty_or_dangling_key_and_leaves_another_working_registration()
    {
        using var sandbox = new ScriptSandbox();
        var own = Path.Combine(sandbox.Root, "native-host", "com.kaank.downloadmanager.json");
        var other = Path.Combine(sandbox.Root, "other.json");
        File.WriteAllText(other, "{}");

        Assert.Equal("True", RunUninstallOwnershipCheck(sandbox.Root, "", own));
        Assert.Equal("True", RunUninstallOwnershipCheck(sandbox.Root, Path.Combine(sandbox.Root, "gone.json"), own));
        Assert.Equal("False", RunUninstallOwnershipCheck(sandbox.Root, other, own));
    }
}

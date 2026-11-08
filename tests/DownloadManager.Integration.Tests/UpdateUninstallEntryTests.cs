using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Xunit;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// UP.B2: after an in-app update, Settings > Apps must show the new version. apply-update.ps1 updates the
/// uninstall entry Setup made, and only that one. Each test gives the script a key of its own under
/// HKCU\Software\AppDataLow\InboundDownloadManagerTests\ (writable at any integrity level) through
/// -UninstallKey; the real uninstall key is never read or written.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateUninstallEntryTests : IDisposable
{
    private const string EntryName = "{6F4B7C2E-3D91-4E8A-9B57-2C1D0A8E5F13}_is1";
    private const string TestParent = @"Software\AppDataLow\InboundDownloadManagerTests";
    private readonly string _testKey = $@"{TestParent}\{Guid.NewGuid():N}";
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"))).FullName;

    private string EntryKey => $@"{_testKey}\{EntryName}";
    private string EntryPath => $@"HKCU:\{EntryKey}";
    private string Install => Path.Combine(_root, "install");

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_testKey, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(TestParent))
        {
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
            {
                Registry.CurrentUser.DeleteSubKey(TestParent, throwOnMissingSubKey: false);
            }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void The_script_names_setups_own_uninstall_entry()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "DownloadManager.iss"));
        // Inno writes "{{" for a literal brace, and names the per-user entry "<AppId>_is1".
        Assert.Contains("AppId={{6F4B7C2E-3D91-4E8A-9B57-2C1D0A8E5F13}", iss);
        Assert.Contains("PrivilegesRequired=lowest", iss);
        Assert.Contains(
            @"'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + EntryName + "'",
            File.ReadAllText(ScriptPath()));
    }

    [Fact]
    public void An_update_writes_the_new_version_into_setups_entry()
    {
        CreateEntry(Install + @"\", "0.5.2", major: 0, minor: 5);
        var (package, resultPath) = BuildUpdate("# no registration in this test");

        var result = RunUpdate(package, resultPath, "0.6.1");

        Assert.True(result.ExitCode == 0, result.Output);
        using var entry = Registry.CurrentUser.OpenSubKey(EntryKey)!;
        Assert.Equal("0.6.1", entry.GetValue("DisplayVersion"));
        Assert.Equal(0, entry.GetValue("MajorVersion"));
        Assert.Equal(6, entry.GetValue("MinorVersion"));
        Assert.Equal(RegistryValueKind.DWord, entry.GetValueKind("MinorVersion"));
        // Only the version: what Setup wrote otherwise stays as it was.
        Assert.Equal("Inbound Download Manager", entry.GetValue("DisplayName"));
        Assert.Null(entry.GetValue("VersionMajor"));
    }

    [Fact]
    public void An_update_creates_no_entry_when_setup_made_none()
    {
        var (package, resultPath) = BuildUpdate("# no registration in this test");

        var result = RunUpdate(package, resultPath, "0.6.1");

        Assert.True(result.ExitCode == 0, result.Output);
        using var parent = Registry.CurrentUser.OpenSubKey(_testKey);
        Assert.Null(parent);
    }

    [Fact]
    public void An_update_leaves_an_entry_for_another_folder_alone()
    {
        CreateEntry(Path.Combine(_root, "elsewhere") + @"\", "0.5.2", major: 0, minor: 5);
        var (package, resultPath) = BuildUpdate("# no registration in this test");

        var result = RunUpdate(package, resultPath, "0.6.1");

        Assert.True(result.ExitCode == 0, result.Output);
        using var entry = Registry.CurrentUser.OpenSubKey(EntryKey)!;
        Assert.Equal("0.5.2", entry.GetValue("DisplayVersion"));
        Assert.Equal(5, entry.GetValue("MinorVersion"));
    }

    [Fact]
    public void A_failed_update_leaves_the_entry_at_the_old_version()
    {
        CreateEntry(Install + @"\", "0.5.2", major: 0, minor: 5);
        var (package, resultPath) = BuildUpdate("throw 'register failed'");

        var result = RunUpdate(package, resultPath, "0.6.1");

        Assert.Equal(1, result.ExitCode);
        using var entry = Registry.CurrentUser.OpenSubKey(EntryKey)!;
        Assert.Equal("0.5.2", entry.GetValue("DisplayVersion"));
        Assert.Contains("\"failed\"", File.ReadAllText(resultPath));
    }

    [Fact]
    public void Undoing_an_update_puts_the_old_values_back()
    {
        // A failure after the entry was updated (the new version would not start) can't be staged without
        // starting a program, so the two functions are lifted out of the script and run on their own.
        CreateEntry(Install + @"\", "0.5.2", major: 0, minor: 5);
        Directory.CreateDirectory(Install);
        var probe = Path.Combine(_root, "probe.ps1");
        File.WriteAllText(probe, $$"""
            $ErrorActionPreference = 'Stop'
            $UninstallKey = '{{EntryPath}}'
            $InstallRoot = '{{Install}}'
            $Version = '1.2.3'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{ScriptPath()}}', [ref]$null, [ref]$null)
            $script:uninstallValuesBefore = $null
            $uninstallVersionNames = @('DisplayVersion', 'MajorVersion', 'MinorVersion', 'VersionMajor', 'VersionMinor')
            foreach ($name in 'Update-UninstallEntry', 'Restore-UninstallEntry') {
                $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
                Invoke-Expression $function.Extent.Text
            }
            Update-UninstallEntry
            Write-Output ("updated " + (Get-ItemProperty -LiteralPath $UninstallKey).DisplayVersion)
            Restore-UninstallEntry
            Write-Output ("restored " + (Get-ItemProperty -LiteralPath $UninstallKey).DisplayVersion)
            """);

        var result = RunPowerShell(probe);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("updated 1.2.3", result.Output);
        Assert.Contains("restored 0.5.2", result.Output);
        using var entry = Registry.CurrentUser.OpenSubKey(EntryKey)!;
        Assert.Equal("0.5.2", entry.GetValue("DisplayVersion"));
        Assert.Equal(0, entry.GetValue("MajorVersion"));
        Assert.Equal(5, entry.GetValue("MinorVersion"));
        Assert.Equal(RegistryValueKind.DWord, entry.GetValueKind("MinorVersion"));
    }

    private void CreateEntry(string installLocation, string version, int major, int minor)
    {
        using var entry = Registry.CurrentUser.CreateSubKey(EntryKey);
        entry.SetValue("DisplayName", "Inbound Download Manager");
        entry.SetValue("DisplayVersion", version);
        entry.SetValue("InstallLocation", installLocation);
        entry.SetValue("MajorVersion", major, RegistryValueKind.DWord);
        entry.SetValue("MinorVersion", minor, RegistryValueKind.DWord);
    }

    private (string Package, string ResultPath) BuildUpdate(string registerScript)
    {
        Directory.CreateDirectory(Path.Combine(Install, "app"));
        File.WriteAllText(Path.Combine(Install, "app", "DownloadManager.Host.exe"), "old");
        File.WriteAllText(Path.Combine(Install, "install.json"), """{ "version": "0.5.2" }""");

        var package = Path.Combine(_root, "package");
        Directory.CreateDirectory(Path.Combine(package, "app"));
        File.WriteAllText(Path.Combine(package, "app", "DownloadManager.Host.exe"), "new");
        // Run by the script; a stand-in, so nothing here registers a browser bridge.
        File.WriteAllText(Path.Combine(package, "register-native-host.ps1"), registerScript);
        return (package, Path.Combine(_root, "updates", "result.json"));
    }

    private (int ExitCode, string Output) RunUpdate(string package, string resultPath, string version)
    {
        var probe = Path.Combine(_root, "run.ps1");
        File.WriteAllText(probe, $"""
            & '{ScriptPath()}' -PackageDir '{package}' -InstallRoot '{Install}' -Version '{version}' -ResultPath '{resultPath}' -NoStart -UninstallKey '{EntryPath}'
            exit $LASTEXITCODE
            """);
        return RunPowerShell(probe);
    }

    private (int ExitCode, string Output) RunPowerShell(string file)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _root,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.GetAwaiter().GetResult());
    }

    private static string ScriptPath() => Path.Combine(RepoRoot(), "scripts", "apply-update.ps1");

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

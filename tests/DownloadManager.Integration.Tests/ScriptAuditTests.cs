using System.Text.RegularExpressions;
using Xunit;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Text checks of the install, repair, uninstall, update and package scripts and of Setup (audit 4 M5-M8, M13,
/// L23). Nothing here runs a script that installs, removes, or closes anything, and nothing touches the registry.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class ScriptAuditTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>The file without its comment lines, so text in a comment cannot satisfy a test.</summary>
    private static string Code(string relativePath, string commentPrefix) => string.Join('\n',
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath)).Split('\n')
            .Where(l => !l.TrimStart().StartsWith(commentPrefix, StringComparison.Ordinal)));

    private static IReadOnlyList<string> UninstallDeleteNames()
    {
        var iss = Code(@"installer\DownloadManager.iss", ";");
        var start = iss.IndexOf("[UninstallDelete]", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = iss.IndexOf("\n[", start + 1, StringComparison.Ordinal);
        var section = iss[start..(end < 0 ? iss.Length : end)];
        return Regex.Matches(section, @"Name:\s*""\{app\}\\([^""]+)""").Select(m => m.Groups[1].Value).ToList();
    }

    [Fact]
    public void Setup_removes_the_app_folder_an_update_replaced_and_the_firefox_folder()
    {
        var names = UninstallDeleteNames();
        Assert.Contains("app", names);
        Assert.Contains("firefox", names);
    }

    [Fact]
    public void Uninstall_script_removes_everything_setup_removes()
    {
        var script = Code(@"scripts\uninstall.ps1", "#");
        foreach (var name in UninstallDeleteNames())
        {
            // app\ and its files go with $appDir; every other name must be in the leftovers list or patterns.
            if (name == "app" || name.StartsWith(@"app\", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(script.Contains($"'{name}'", StringComparison.Ordinal), $"uninstall.ps1 leaves {name} behind.");
        }
    }

    [Fact]
    public void Uninstall_closes_the_app_through_stop_for_setup_and_only_kills_its_own_copies()
    {
        var script = Code(@"scripts\uninstall.ps1", "#");
        Assert.Contains("stop-for-setup.ps1", script);
        Assert.DoesNotContain("host shutdown", script);
        Assert.Contains("inbound-torrent", script);
        // The fallback filters by this install's path before it stops anything.
        Assert.Matches(@"Where-Object \{ \$_\.Path -and \$_\.Path\.StartsWith\(\$ownRoot", script);
    }

    [Fact]
    public void Uninstall_removes_the_torrent_handlers_and_only_its_own_firewall_rule()
    {
        var script = Code(@"scripts\uninstall.ps1", "#");
        Assert.Contains("magnet", script);
        Assert.Contains(".torrent", script);
        Assert.Contains("netsh", script);
    }

    [Fact]
    public void Repair_closes_only_this_install_and_writes_the_shortcut_setup_writes()
    {
        var script = Code(@"scripts\repair.ps1", "#");
        Assert.Contains("stop-for-setup.ps1", script);
        Assert.DoesNotMatch(@"Get-Process[^\n]*\|\s*Stop-Process", script);
        Assert.Contains("\"--open\"", script);
        Assert.Contains("IconLocation", script);
    }

    [Fact]
    public void Package_copies_named_documents_and_the_stop_script()
    {
        var script = Code(@"scripts\package.ps1", "#");
        Assert.DoesNotContain(@"docs\*", script);
        Assert.Contains("stop-for-setup.ps1", script);
        foreach (var doc in Regex.Matches(script, @"""([a-z-]+\.md)""").Select(m => m.Groups[1].Value))
        {
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "docs", doc)), $"package.ps1 names docs\\{doc}, which is missing.");
        }
    }

    [Fact]
    public void Update_keeps_the_rollback_copies_through_restart()
    {
        // Confirm-Started only sees an early exit; a Host that fails later, or at the next sign-in, still needs the
        // previous version on disk. The next update replaces the copies once its own Host has exited.
        var script = Code(@"scripts\apply-update.ps1", "#");
        var succeeded = script.IndexOf("Write-Result \"succeeded\"", StringComparison.Ordinal);
        Assert.True(succeeded >= 0);
        var end = script.IndexOf("exit 0", succeeded, StringComparison.Ordinal);
        Assert.True(end > succeeded);
        var tail = script[succeeded..end];
        Assert.DoesNotContain("$backupDir", tail);
        Assert.DoesNotContain("$extensionBackupDir", tail);
        Assert.DoesNotContain("Remove-Directory", tail);
        Assert.Contains("\"stop-for-setup.ps1\"", script);
    }

    [Theory]
    [InlineData(@"scripts\uninstall.ps1")]
    [InlineData(@"scripts\repair.ps1")]
    public void The_stop_script_runs_with_errors_not_terminating(string relativePath)
    {
        // Under 'Stop', Windows PowerShell 5.1 makes captured native stderr a terminating error, which would end
        // uninstall before its path-filtered fallback and repair before its own message.
        var script = Code(relativePath, "#");
        var call = script.IndexOf("-File $stopScript", StringComparison.Ordinal);
        Assert.True(call >= 0);
        var before = script[..call];
        var lastContinue = before.LastIndexOf("$ErrorActionPreference = \"Continue\"", StringComparison.Ordinal);
        var lastStop = before.LastIndexOf("$ErrorActionPreference = \"Stop\"", StringComparison.Ordinal);
        Assert.True(lastContinue > lastStop, $"{relativePath} runs stop-for-setup.ps1 under 'Stop'.");
        Assert.Contains("$ErrorActionPreference = \"Stop\"", script[call..]);
    }

    [Fact]
    public void Test_script_runs_the_server_and_web_ui_tests()
    {
        var script = Code(@"scripts\test.ps1", "#");
        Assert.Contains(@"tests\DownloadManager.Server.Tests\DownloadManager.Server.Tests.csproj", script);
        Assert.Contains("npm test", script);
        Assert.Contains("svelte-check", script);
    }
}

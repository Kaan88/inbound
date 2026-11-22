using System.Text.RegularExpressions;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// SITES.8: <c>dm sites</c> through the real CLI and Host against a real SFTP server (rclone's own): the switch, adding a
/// site with the password on standard input (never as an argument), the server key asked about and trusted by its
/// fingerprint, a test, and the site's files (ls, mkdir, mv, rm, which asks for --yes first), then removing the site.
/// </summary>
[Trait("Platform", "Windows")]
public sealed partial class SiteTransfersEndToEndTests
{
    [SiteTransfersFact]
    public async Task TheCliAddsTrustsBrowsesAndChangesASite()
    {
        var port = StartServer(slow: false);
        await using var host = await SandboxHost.StartAsync("https://example.invalid/v1/api/");

        Assert.Contains("Remote files: off", (await host.RunCliAsync(null, "sites", "status")).Output);
        Assert.Contains("Remote files: on", (await host.RunCliAsync(null, "sites", "on")).Output);

        // A password as an argument is refused before anything is sent; on standard input it is used.
        var argument = await host.RunCliAsync(null, "sites", "add", "Box", "--kind", "sftp", "--host", "127.0.0.1", "--port", $"{port}",
            "--user", User, "--password", Password);
        Assert.Equal(2, argument.ExitCode);
        var added = await host.RunCliAsync(Password + "\n", "sites", "add", "Box", "--kind", "sftp", "--host", "127.0.0.1", "--port", $"{port}",
            "--user", User, "--folder", "/pub");
        Assert.True(added.ExitCode == 0, added.All);
        Assert.Contains("dm sites trust", added.Output);
        Assert.Contains($"{User}@127.0.0.1:{port}", (await host.RunCliAsync(null, "sites", "list")).Output);

        // The key is not trusted yet: the test says so and names the command.
        var untrusted = await host.RunCliAsync(null, "sites", "test", "Box");
        Assert.Equal(1, untrusted.ExitCode);
        Assert.Contains("sites trust", untrusted.Error);

        // Input that isn't a console gets no question, so nothing is trusted without a fingerprint; a wrong one trusts nothing.
        var shown = await host.RunCliAsync(null, "sites", "trust", "Box");
        Assert.Equal(1, shown.ExitCode);
        var fingerprint = Regex.Match(shown.Output, @"SHA256:\S+").Value;
        Assert.NotEmpty(fingerprint);
        Assert.Equal(1, (await host.RunCliAsync(null, "sites", "trust", "Box", "--fingerprint", "SHA256:not-the-key")).ExitCode);
        var trusted = await host.RunCliAsync(null, "sites", "trust", "Box", "--fingerprint", fingerprint);
        Assert.True(trusted.ExitCode == 0, trusted.All);
        var tested = await host.RunCliAsync(null, "sites", "test", "Box");
        Assert.True(tested.ExitCode == 0, tested.All);

        // The site's files.
        await File.WriteAllTextAsync(Path.Combine(Served, "pub", "keep.txt"), "keep");
        Assert.True((await host.RunCliAsync(null, "sites", "mkdir", "Box", "/pub/made")).ExitCode == 0);
        var listed = await host.RunCliAsync(null, "sites", "ls", "Box");
        Assert.Contains("made/", listed.Output);
        Assert.Contains("keep.txt", listed.Output);
        Assert.True(Directory.Exists(Path.Combine(Served, "pub", "made")));
        var renamed = await host.RunCliAsync(null, "sites", "mv", "Box", "/pub/made", "renamed");
        Assert.True(renamed.ExitCode == 0, renamed.All);
        Assert.True(Directory.Exists(Path.Combine(Served, "pub", "renamed")));
        Assert.Equal(2, (await host.RunCliAsync(null, "sites", "mv", "Box", "/pub/renamed", "a/b")).ExitCode);

        // rm says what would go and waits for --yes.
        var asked = await host.RunCliAsync(null, "sites", "rm", "Box", "/pub/renamed", "/pub/keep.txt");
        Assert.Equal(2, asked.ExitCode);
        Assert.Contains("--yes", asked.Error);
        Assert.True(File.Exists(Path.Combine(Served, "pub", "keep.txt")));
        var removedFiles = await host.RunCliAsync(null, "sites", "rm", "Box", "/pub/renamed", "/pub/keep.txt", "--yes");
        Assert.True(removedFiles.ExitCode == 0, removedFiles.All);
        Assert.False(File.Exists(Path.Combine(Served, "pub", "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(Served, "pub", "renamed")));

        // Removing the site leaves the server alone.
        Assert.True((await host.RunCliAsync(null, "sites", "remove", "Box")).ExitCode == 0);
        Assert.DoesNotContain("Box", (await host.RunCliAsync(null, "sites", "list")).Output);
        Assert.True(Directory.Exists(Path.Combine(Served, "pub")));
        Assert.Contains("Remote files: off", (await host.RunCliAsync(null, "sites", "off")).Output);
    }
}

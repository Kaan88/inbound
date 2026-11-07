using System.Text.RegularExpressions;

namespace DownloadManager.Core.Tests;

/// <summary>
/// Nothing says what a provider's account allows unless the provider said it. The queue limit in the
/// Scheduler is the app's own feature and isn't in these files.
/// </summary>
public sealed partial class ProviderWordingTests
{
    private static readonly string[] Places =
    [
        "src/DownloadManager.Desktop.WinUI/Remote",
        "src/DownloadManager.Desktop.WinUI/MainWindow.AddFlows.cs",
        "src/DownloadManager.Desktop.WinUI/MainWindow.Remote.cs",
        "src/DownloadManager.Desktop.WinUI/MyProviderWindow.cs",
        "src/DownloadManager.Desktop.WinUI/ContainerImportDialog.cs",
        "src/DownloadManager.Runtime/Remote",
        "src/DownloadManager.Providers.TorBox",
        "src/DownloadManager.Cli/Program.Remote.cs",
    ];

    [GeneratedRegex("\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\b(quota|allowance)\b|count against", RegexOptions.IgnoreCase)]
    private static partial Regex Claim();

    [Fact]
    public void No_text_about_a_provider_claims_a_quota_or_an_allowance()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);

        var read = 0;
        var claims = new List<string>();
        foreach (var place in Places)
        {
            var full = Path.Combine(directory.FullName, place);
            var files = File.Exists(full) ? [full] : Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories).ToArray();
            foreach (var file in files)
            {
                read++;
                foreach (var line in File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
                {
                    claims.AddRange(StringLiteral().Matches(line).Where(literal => Claim().IsMatch(literal.Value)).Select(literal => $"{Path.GetFileName(file)}: {literal.Value}"));
                }
            }
        }

        Assert.True(read >= 10, $"Only {read} files were read, so the places above have moved.");
        Assert.Empty(claims);
    }

    [Fact]
    public void The_approval_words_say_what_happens_and_nothing_about_limits()
    {
        var job = new DownloadManager.Contracts.RemoteJobDto { Name = "x", ProviderName = "TorBox", HeldForApproval = true };
        foreach (var words in new[]
        {
            DownloadManager.Desktop.WinUI.RemoteText.ApproveExplanation("TorBox"),
            DownloadManager.Desktop.WinUI.RemoteText.ApproveQuestion(job),
            DownloadManager.Desktop.WinUI.RemoteText.ApproveQuestion(new DownloadManager.Contracts.RemoteJobDto { Name = "x", ProviderName = "TorBox" }),
        })
        {
            Assert.DoesNotContain("limit", words, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(Claim(), words);
            Assert.Contains("TorBox", words, StringComparison.Ordinal);
        }
    }
}

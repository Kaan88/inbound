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
        "src/DownloadManager.Desktop.WinUI/ProviderPanel.cs",
        "src/DownloadManager.Desktop.WinUI/ContainerImportDialog.cs",
        "src/DownloadManager.Runtime/Remote",
        "src/DownloadManager.Providers.TorBox",
        "src/DownloadManager.Cli/Program.DirectAdd.cs",
    ];

    [GeneratedRegex("\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\b(quota|allowance)\b|count against", RegexOptions.IgnoreCase)]
    private static partial Regex Claim();

    /// <summary>Adding is the decision, so nothing on screen or in the tray waits for, or speaks of, an approval.</summary>
    [GeneratedRegex(@"approv", RegexOptions.IgnoreCase)]
    private static partial Regex Approval();

    [Fact]
    public void No_text_about_a_provider_claims_a_quota_or_an_allowance()
        => Assert.Empty(LiteralsMatching(Claim(), Places));

    [Fact]
    public void Nothing_shown_asks_for_or_mentions_an_approval()
    {
        // The tray's words are read too.
        var shown = Places.Append("src/DownloadManager.Host").ToArray();

        Assert.Empty(LiteralsMatching(Approval(), shown));
    }

    [Fact]
    public void The_words_for_a_row_nothing_was_sent_for_say_what_Enter_does()
    {
        var held = new DownloadManager.Contracts.RemoteJobDto { Name = "x", ProviderName = "TorBox", State = "intended", ApprovalNeeded = true, HeldForApproval = true };
        var notStarted = new DownloadManager.Contracts.RemoteJobDto { Name = "x", ProviderName = "TorBox", State = "intended", ApprovalNeeded = true };
        foreach (var row in new[] { DownloadManager.Desktop.WinUI.RemoteText.JobRow(held, DateTimeOffset.Now), DownloadManager.Desktop.WinUI.RemoteText.JobRow(notStarted, DateTimeOffset.Now) })
        {
            Assert.StartsWith("Enter ", row.Detail, StringComparison.Ordinal);
            Assert.Contains("TorBox", row.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("limit", row.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(Claim(), row.Detail);
        }
    }

    private static List<string> LiteralsMatching(Regex words, IEnumerable<string> places)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);

        var read = 0;
        var found = new List<string>();
        foreach (var place in places)
        {
            var full = Path.Combine(directory.FullName, place);
            var files = File.Exists(full) ? [full] : Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories).ToArray();
            foreach (var file in files)
            {
                read++;
                foreach (var line in File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
                {
                    found.AddRange(StringLiteral().Matches(line).Where(literal => words.IsMatch(literal.Value)).Select(literal => $"{Path.GetFileName(file)}: {literal.Value}"));
                }
            }
        }

        Assert.True(read >= 10, $"Only {read} files were read, so the places above have moved.");
        return found;
    }
}

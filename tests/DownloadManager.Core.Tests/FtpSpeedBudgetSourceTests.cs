using System.Text.RegularExpressions;
using Xunit;

namespace DownloadManager.Core.Tests;

/// <summary>
/// PERF.6: the global speed cap is one budget shared by HTTP and FTP. An FTP engine made without the HTTP engine's
/// budget gets a cap of its own, so HTTP and FTP together could run at twice the cap (the server's did, until 0.6.0).
/// </summary>
public class FtpSpeedBudgetSourceTests
{
    [Fact]
    public void EveryAppThatMakesAnFtpEngineGivesItTheHttpEnginesSpeedBudget()
    {
        var made = 0;
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Root(), path);
            if (relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(File.ReadAllText(path), @"new\s+(?:[\w.]+\.)?FtpDownloadEngine\s*\(([^)]*)\)"))
            {
                made++;
                Assert.True(match.Groups[1].Value.Contains("SpeedBudget", StringComparison.Ordinal),
                    $"{relative} makes an FTP engine without the shared speed budget: {match.Value}");
            }
        }

        // The Host and the server.
        Assert.True(made >= 2, $"Only {made} FTP engine(s) found; the search no longer sees them.");
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root wasn't found.");
    }
}

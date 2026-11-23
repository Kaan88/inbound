using System.Text.RegularExpressions;
using static DownloadManager.Core.Tests.WindowLocalFindingsTests;

namespace DownloadManager.Core.Tests;

/// <summary>
/// SITES.B1 (owner, 2026-11-23): choosing OneDrive, Google Drive or Dropbox in Add a remote crashed the Windows window.
/// The sign-in kinds move the client ID and secret under Advanced (MoveToAdvanced), and BuildForm had put the same two
/// boxes in the form as well (983f33af, SITES.11 R4): a XAML element can have one parent, so the second Add threw.
/// </summary>
public sealed class SiteDialogPlacementTests
{
    [Fact]
    public void A_control_moved_under_Advanced_is_added_to_no_other_panel()
    {
        var source = File.ReadAllText(WinUiFile("SiteDialog.cs"));
        var moved = Regex.Matches(source, @"MoveToAdvanced\((_\w+),").Select(match => match.Groups[1].Value).Distinct().ToList();
        Assert.Contains("_clientId", moved);
        Assert.Contains("_clientSecret", moved);
        foreach (var name in moved)
        {
            // Only MoveToAdvanced's own generic Add (of "control") places these.
            Assert.DoesNotMatch(@"Children\.(Add|Insert)\(([^)]*,\s*)?" + Regex.Escape(name) + @"\)", source);
        }
    }
}

using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.R41 (review 5:F1): when the main window's "get the extension" bar shows, and what Options says per browser.</summary>
public sealed class ExtensionPromoRulesTests
{
    private static readonly Dictionary<string, DateTimeOffset> None = new();
    private static readonly Dictionary<string, DateTimeOffset> ChromeSeen = new()
    {
        [ExtensionBrowsers.Chrome] = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void It_shows_on_this_PC_while_no_extension_has_ever_connected()
        => Assert.Equal(new ExtensionHintDecision(true, false), ExtensionPromoRules.Decide(false, true, "on", None, false));

    [Fact]
    public void Not_now_hides_it_for_good()
        => Assert.Equal(new ExtensionHintDecision(false, false), ExtensionPromoRules.Decide(true, true, "on", None, false));

    [Fact]
    public void Once_an_extension_has_connected_it_hides_and_is_marked_done()
        => Assert.Equal(new ExtensionHintDecision(false, true), ExtensionPromoRules.Decide(false, true, "on", ChromeSeen, false));

    [Fact]
    public void A_download_the_browser_sent_counts_as_connected()
        => Assert.Equal(new ExtensionHintDecision(false, true), ExtensionPromoRules.Decide(false, true, "on", None, true));

    [Fact]
    public void Done_stays_done_even_when_the_Host_forgets()
        => Assert.False(ExtensionPromoRules.Decide(true, true, "on", None, false).Show);

    [Fact]
    public void Another_server_or_a_Host_that_cant_say_shows_nothing_and_marks_nothing()
    {
        Assert.Equal(new ExtensionHintDecision(false, false), ExtensionPromoRules.Decide(false, false, "on", None, false));
        Assert.Equal(new ExtensionHintDecision(false, false), ExtensionPromoRules.Decide(false, true, "on", null, false));
    }

    [Fact]
    public void Capturing_turned_off_in_Options_is_a_choice_and_hides_it_without_marking_it_done()
        => Assert.Equal(new ExtensionHintDecision(false, false), ExtensionPromoRules.Decide(false, true, "disabled", None, false));

    [Fact]
    public void A_name_that_isnt_a_browser_doesnt_count()
        => Assert.True(ExtensionPromoRules.Decide(false, true, "on", new Dictionary<string, DateTimeOffset> { ["other"] = DateTimeOffset.UnixEpoch }, false).Show);

    [Theory]
    [InlineData("chromeAuto", true)]
    [InlineData("chromeContextMenu", true)]
    [InlineData("api", false)]
    [InlineData("clipboardMonitor", false)]
    [InlineData(null, false)]
    public void Downloads_from_the_browser_are_told_by_their_source(string? source, bool expected)
        => Assert.Equal(expected, ExtensionPromoRules.FromBrowser(source));

    [Fact]
    public void Each_browser_links_to_where_it_really_gets_the_extension()
    {
        var links = ExtensionPromoRules.Links(None, "on", canRepair: true);
        Assert.Equal(["Chrome", "Edge", "Firefox"], links.Select(link => link.Name));
        Assert.Equal("https://chromewebstore.google.com/detail/olhbcdbjdnfoiinllnlpandggadfabfm", links[0].Url);
        Assert.Equal("https://microsoftedge.microsoft.com/addons/detail/ifoagddcenmjpbklkhghbdmgplldcdci", links[1].Url);
        // Not listed on Firefox Add-ons (EXT.S4): the signed .xpi is on each GitHub release.
        Assert.Equal("https://github.com/Kaan88/inbound/releases/latest", links[2].Url);
        Assert.All(links, link => Assert.StartsWith("https://", link.Url, StringComparison.Ordinal));
    }

    [Fact]
    public void A_browser_that_connected_says_so_with_its_day_and_the_others_say_not_yet()
    {
        var links = ExtensionPromoRules.Links(ChromeSeen, "on", canRepair: true, CultureInfo.InvariantCulture);
        Assert.True(links[0].Connected);
        Assert.StartsWith("Connected · last ", links[0].Status, StringComparison.Ordinal);
        Assert.Contains("Nov 2026", links[0].Status, StringComparison.Ordinal);
        Assert.Equal("Not connected yet", links[1].Status);
        Assert.Equal("Not connected yet", links[2].Status);
    }

    [Fact]
    public void A_broken_registration_is_named_for_the_browser_it_breaks()
    {
        var links = ExtensionPromoRules.Links(None, "on; the extension can't reach Inbound in Edge", canRepair: true);
        Assert.Equal("Not connected yet", links[0].Status);
        Assert.StartsWith("Can't reach Inbound", links[1].Status, StringComparison.Ordinal);
        Assert.Equal("Not connected yet", links[2].Status);

        var none = ExtensionPromoRules.Links(None, "native host not registered", canRepair: true);
        Assert.StartsWith("Can't reach Inbound", none[0].Status, StringComparison.Ordinal);
        Assert.StartsWith("Can't reach Inbound", none[1].Status, StringComparison.Ordinal);
        // Firefox's registration isn't part of that status, so nothing is said about it.
        Assert.Equal("Not connected yet", none[2].Status);
    }

    [Fact]
    public void Without_the_Hosts_word_there_is_no_status_only_the_links()
        => Assert.All(ExtensionPromoRules.Links(null, null, canRepair: true), link => Assert.Null(link.Status));

    [Fact]
    public void What_blocks_a_browser_now_comes_before_it_connecting_once()
    {
        var broken = ExtensionPromoRules.Links(ChromeSeen, "on; the extension can't reach Inbound in Chrome", canRepair: true);
        Assert.False(broken[0].Connected);
        Assert.StartsWith("Can't reach Inbound", broken[0].Status, StringComparison.Ordinal);

        var off = ExtensionPromoRules.Links(ChromeSeen, "disabled", canRepair: true);
        Assert.False(off[0].Connected);
        Assert.Equal("Not connected · browser downloads are off", off[0].Status);
    }

    [Fact]
    public void Without_a_Repair_the_registration_is_not_blamed()
    {
        // Linux: the Host registers the native host itself and reports no registration state.
        var links = ExtensionPromoRules.Links(None, "native host not registered", canRepair: false);
        Assert.All(links, link => Assert.Equal("Not connected yet", link.Status));
        var seen = ExtensionPromoRules.Links(ChromeSeen, "native host not registered", canRepair: false, CultureInfo.InvariantCulture);
        Assert.True(seen[0].Connected);
        // Capturing turned off is still said: it is the user's own choice in Options.
        Assert.Equal("Not connected · browser downloads are off", ExtensionPromoRules.Links(None, "disabled", canRepair: false)[0].Status);
    }
}

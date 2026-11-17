using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public sealed class ResumeListTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Round_trips_and_drops_duplicates_and_junk()
    {
        var text = ResumeList.Serialize([A, B, A]);
        Assert.Equal([A, B], ResumeList.Parse(text));
        Assert.Equal([A], ResumeList.Parse("junk\n" + A + "\n\n" + A + "\r\nnot-a-guid"));
        Assert.Empty(ResumeList.Parse(null));
        Assert.Empty(ResumeList.Parse(""));
    }

    [Fact]
    public void Merge_keeps_both_lists_once()
    {
        Assert.Equal([A, B], ResumeList.Merge([A], [B, A]));
    }

    [Fact]
    public void Only_remembered_downloads_that_are_still_paused_count()
    {
        // Owner, S24: "201 downloads paused" for one download; old paused entries are not what Pause all paused.
        var states = new Dictionary<Guid, string> { [A] = "paused", [B] = "downloading" };
        Assert.Equal(1, ResumeList.StillPaused([A, B, Guid.NewGuid()], id => states.GetValueOrDefault(id)));
    }

    [Fact]
    public void A_very_long_list_keeps_the_newest_ids()
    {
        var ids = Enumerable.Range(0, ResumeList.MaxIds + 5).Select(_ => Guid.NewGuid()).ToList();
        var kept = ResumeList.Parse(ResumeList.Serialize(ids));
        Assert.Equal(ResumeList.MaxIds, kept.Count);
        Assert.Equal(ids[^1], kept[^1]);
    }
}

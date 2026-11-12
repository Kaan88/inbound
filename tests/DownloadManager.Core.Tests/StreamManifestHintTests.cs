using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

/// <summary>CUT.1: one rule for "is this a streaming playlist", at the job's start and on the server's answer.</summary>
public sealed class StreamManifestHintTests
{
    [Theory]
    [InlineData("https://radio.example/radio.m3u", "radio.m3u", "audio/x-mpegurl", false)]
    [InlineData("https://radio.example/listen?id=5", "radio.m3u", "audio/x-mpegurl", false)]
    [InlineData("https://radio.example/radio.m3u?id=5", "listen", "audio/x-mpegurl", false)]
    [InlineData("https://cdn.example/watch", "watch", "application/vnd.apple.mpegurl", true)]
    [InlineData("https://cdn.example/master.m3u8", "master.m3u8", null, true)]
    [InlineData("https://cdn.example/master.m3u8", "renamed.m3u", null, true)]
    [InlineData("https://cdn.example/file.zip", "file.zip", null, false)]
    public void HlsAtTheStartSparesAPlainM3uList(string url, string fileName, string? handoffType, bool expected)
    {
        var job = new DownloadJob { Url = url, FileName = fileName, HandoffContentType = handoffType };
        Assert.Equal(expected, StreamManifestHint.IsHls(job));
    }

    [Fact]
    public void ADescriptionThatIsTheHlsTypeSparesAPlainM3uListToo()
    {
        Assert.False(StreamManifestHint.IsHls(new DownloadJob { Url = "https://a.example/x.m3u", FileName = "x.m3u", Description = "audio/x-mpegurl" }));
        Assert.True(StreamManifestHint.IsHls(new DownloadJob { Url = "https://a.example/x", FileName = "x", Description = "audio/x-mpegurl" }));
    }

    [Fact]
    public void TheServersAnswerCountsAnyNameThatSaysM3u()
    {
        Assert.True(StreamManifestHint.IsPlaylistResponse("audio/x-mpegurl", "listen", "https://radio.example/listen?id=5", null));
        Assert.False(StreamManifestHint.IsPlaylistResponse("audio/x-mpegurl", "listen", "https://radio.example/listen?id=5", "station.m3u"));
        Assert.False(StreamManifestHint.IsPlaylistResponse("audio/x-mpegurl", "listen", "https://radio.example/a.m3u?x=1", null));
        // DASH has no plain-list twin.
        Assert.True(StreamManifestHint.IsPlaylistResponse("application/dash+xml", "station.m3u"));
        Assert.False(StreamManifestHint.IsPlaylistResponse("application/zip", "listen"));
    }
}

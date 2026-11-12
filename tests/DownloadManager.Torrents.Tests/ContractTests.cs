using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace DownloadManager.Torrents.Tests;

public sealed class ContractTests
{
    [Fact]
    public async Task FramesRoundTripAndAnEmptyStreamEndsCleanly()
    {
        using var stream = new MemoryStream();
        await TorrentFrames.WriteAsync(stream, new JsonObject { ["cmd"] = "add", ["id"] = "é" }, default);
        await TorrentFrames.WriteAsync(stream, new JsonObject(), default);
        stream.Position = 0;
        var first = await TorrentFrames.ReadAsync(stream, default);
        Assert.Equal("é", first!["id"]!.GetValue<string>());
        Assert.Empty((await TorrentFrames.ReadAsync(stream, default))!);
        Assert.Null(await TorrentFrames.ReadAsync(stream, default));
    }

    [Fact]
    public async Task AFrameAboveTheLimitOrCutShortIsRefused()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, TorrentFrames.MaxFrameBytes + 1u);
        await Assert.ThrowsAsync<InvalidDataException>(() => TorrentFrames.ReadAsync(new MemoryStream(header), default));

        BinaryPrimitives.WriteUInt32LittleEndian(header, 10);
        await Assert.ThrowsAsync<EndOfStreamException>(() => TorrentFrames.ReadAsync(new MemoryStream([.. header, 1, 2]), default));
        await Assert.ThrowsAsync<EndOfStreamException>(() => TorrentFrames.ReadAsync(new MemoryStream([1, 2]), default));
    }

    [Fact]
    public async Task TheFakeEngineAddsReportsAndCompletesWithoutSeeding()
    {
        await using var fake = new FakeTorrentEngine();
        await fake.SendAsync("add", new JsonObject { ["id"] = "a", ["savePath"] = "x" });
        await Assert.ThrowsAsync<TorrentCommandException>(() => fake.SendAsync("add", new JsonObject { ["id"] = "a", ["savePath"] = "x" }));
        await Assert.ThrowsAsync<TorrentCommandException>(() => fake.SendAsync("nonsense"));
        for (var i = 0; i < 4; i++) fake.Advance();
        await fake.SendAsync("shutdown");
        var events = new List<string>();
        await foreach (var e in fake.Events.ReadAllAsync()) events.Add(e["event"]!.GetValue<string>());
        Assert.Equal(["hello", "metadata", "progress", "progress", "progress", "progress", "completed", "state", "bye"], events);
        Assert.True(fake.Exited.IsCompleted);
        await Assert.ThrowsAsync<TorrentEngineExitedException>(() => fake.SendAsync("pause", new JsonObject { ["id"] = "a" }));
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Cli;
using DownloadManager.Contracts;
using DownloadManager.Tests.Shared;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// PERF.10 (Native AOT): what the CLI's --json modes print must not change by a byte when the rows come from generated
/// metadata instead of anonymous objects. The literals below were taken from the build that used anonymous objects.
/// </summary>
public sealed class CliJsonOutputTests
{
    private static JsonSerializerOptions ReflectionPretty() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public void AddBatchJsonIsOneRowPerAddressWithTheFieldsThatHaveAValue()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var job = new JobDto { Id = id, Url = "https://example.invalid/a.bin", FileName = "a.bin" };
        var text = Cli.Program.AddBatchJson(
        [
            ("https://example.invalid/a.bin", new AddJobResponse { Accepted = true, Job = job }),
            ("gopher://x", new AddJobResponse { Accepted = false, Error = "No." }),
            ("https://example.invalid/dup", new AddJobResponse { Accepted = true, Duplicate = true }),
        ]);

        var rows = JsonDocument.Parse(text).RootElement.EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(["url", "accepted", "duplicate", "job"], rows[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["url", "accepted", "duplicate", "error"], rows[1].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["url", "accepted", "duplicate"], rows[2].EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            "[\n  {\n    \"url\": \"gopher://x\",\n    \"accepted\": false,\n    \"duplicate\": false,\n    \"error\": \"No.\"\n  }\n]".Replace("\n", Environment.NewLine),
            Cli.Program.AddBatchJson([("gopher://x", new AddJobResponse { Error = "No." })]));
    }

    [Fact]
    public void FoldersJsonKeepsItsLowerCaseNamesAndLeavesOutWhatIsUnknown()
    {
        var text = Cli.Program.FoldersJson(
            [("favorite", "Movies", "/m"), ("default", "", "/d"), ("recent", "", "/r")],
            [new FolderSpaceDto { Path = "/m", FreeBytes = 10, TotalBytes = 20, InsideContainer = true }, new FolderSpaceDto { Path = "/d", FreeBytes = 5 }]);
        var expected = """
            [
              {
                "kind": "favorite",
                "name": "Movies",
                "path": "/m",
                "freeBytes": 10,
                "totalBytes": 20,
                "insideContainer": true
              },
              {
                "kind": "default",
                "path": "/d",
                "freeBytes": 5,
                "insideContainer": false
              },
              {
                "kind": "recent",
                "path": "/r",
                "insideContainer": false
              }
            ]
            """.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        Assert.Equal(expected, text);
    }

    [Fact]
    public void EveryDtoTheCliPrintsComesOutAsTheReflectionSerializerWouldWriteIt()
    {
        foreach (var variant in new[] { 0, 1, 2 })
        {
            Check(JsonSampleFiller.Create<SettingsDto>(variant));
            Check(JsonSampleFiller.Create<JobDto>(variant));
            Check(JsonSampleFiller.Create<SnapshotDto>(variant));
            Check(JsonSampleFiller.Create<List<QueueDto>>(variant));
            Check(JsonSampleFiller.Create<List<ScheduleDto>>(variant));
            Check(JsonSampleFiller.Create<List<SiteLoginDto>>(variant));
            Check(JsonSampleFiller.Create<List<DownloadGroupDto>>(variant));
            Check(JsonSampleFiller.Create<List<ConnectionProfileDto>>(variant));
            Check(JsonSampleFiller.Create<TorrentInspectResult>(variant));
            Check(JsonSampleFiller.Create<TorrentAddResult>(variant));
            Check(JsonSampleFiller.Create<TorrentFilesResult>(variant));
            Check(JsonSampleFiller.Create<TorrentActionResult>(variant));
            Check(JsonSampleFiller.Create<TorrentOptionsResult>(variant));
            Check(JsonSampleFiller.Create<TorrentDetailsResult>(variant));
            Check(JsonSampleFiller.Create<TorrentPeersResult>(variant));
            Check(JsonSampleFiller.Create<TorrentTrackersResult>(variant));
            Check(JsonSampleFiller.Create<TorrentPiecesResult>(variant));
            Check(JsonSampleFiller.Create<TorrentNetworkResult>(variant));
            Check(JsonSampleFiller.Create<UpdateCheckResult>(variant));
            Check(JsonSampleFiller.Create<UpdateInstallResult>(variant));
        }
    }

    private static void Check<T>(T value)
        => Assert.Equal(JsonSerializer.Serialize(value, ReflectionPretty()), Cli.Program.PrettyText(value));

}

using System.Text.Json.Nodes;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

public sealed class SiteBrowseTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("a/b", "/a/b")]
    [InlineData("/a//b/", "/a/b")]
    [InlineData("/srv/pub", "/srv/pub")]
    [InlineData("/a\\b", "/a\\b")]
    [InlineData("/a/.hidden", "/a/.hidden")]
    [InlineData("/a/../b", null)]
    [InlineData("/a/./b", null)]
    [InlineData("..", null)]
    [InlineData("/a\nb", null)]
    public void Paths_are_normalised_and_dot_segments_refused(string? input, string? expected) =>
        Assert.Equal(expected, SitePaths.Normalize(input));

    [Fact]
    public void A_path_longer_than_the_limit_is_refused() =>
        Assert.Null(SitePaths.Normalize("/" + new string('a', SitePaths.MaxLength)));

    [Fact]
    public void The_remote_argument_has_no_leading_slash()
    {
        Assert.Equal("", SitePaths.ToRemote("/"));
        Assert.Equal("srv/pub", SitePaths.ToRemote("/srv/pub"));
    }

    [Fact]
    public void A_listing_is_folders_first_then_by_name_with_sizes_and_times()
    {
        var listing = JsonNode.Parse("""
            {"list":[
              {"Path":"b.txt","Name":"b.txt","Size":5,"ModTime":"2026-11-01T10:00:00.5+01:00","IsDir":false},
              {"Path":"Zeta","Name":"Zeta","Size":-1,"ModTime":"2026-11-02T10:00:00Z","IsDir":true},
              {"Path":"a.txt","Name":"a.txt","Size":0,"ModTime":"0001-01-01T00:00:00Z","IsDir":false},
              {"Path":"B.txt","Name":"B.txt","Size":7,"IsDir":false},
              {"Path":"alpha","Name":"alpha","Size":-1,"IsDir":true},
              {"Name":""},
              "junk"
            ]}
            """)!.AsObject();

        var items = SiteListing.Read(listing);

        Assert.Equal(["alpha", "Zeta", "a.txt", "B.txt", "b.txt"], items.Select(i => i.Name));
        Assert.True(items[0].IsFolder && items[1].IsFolder && !items[2].IsFolder);
        Assert.Equal(-1, items[1].Size);
        Assert.Equal(0, items[2].Size);
        Assert.Null(items[2].Modified);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 0, 0, 500, TimeSpan.Zero), items[4].Modified);
        Assert.Null(items[3].Modified);
    }

    [Fact]
    public void A_listing_without_a_list_is_empty() => Assert.Empty(SiteListing.Read(new JsonObject()));
}

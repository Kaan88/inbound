namespace DownloadManager.Core.Tests;

public sealed class RobotsRulesTests
{
    private const string Agent = "DownloadManagerGrabber/0.1";

    private const string Sample = """
        # comment
        User-agent: OtherBot
        Disallow: /

        User-agent: *
        Disallow: /private/   # trailing comment
        Allow: /private/open.html
        Disallow: /files/blocked*
        Disallow: /*.tmp$
        """;

    [Theory]
    [InlineData("/", true)]
    [InlineData("/index.html", true)]
    [InlineData("/private/", false)]
    [InlineData("/private/secret.html", false)]
    [InlineData("/private/open.html", true)]
    [InlineData("/files/ok.bin?size=1", true)]
    [InlineData("/files/blocked.bin?size=1", false)]
    [InlineData("/a/b.tmp", false)]
    [InlineData("/a/b.tmp?x=1", true)]
    public void StarGroupAppliesLongestMatch(string path, bool allowed)
        => Assert.Equal(allowed, RobotsRules.Parse(Sample, Agent).IsAllowed(path));

    [Fact]
    public void SpecificAgentGroupOverridesStar()
    {
        var rules = RobotsRules.Parse(Sample, "OtherBot/2.0");
        Assert.False(rules.IsAllowed("/index.html"));
    }

    [Fact]
    public void AgentGroupForUsReplacesStarGroup()
    {
        var rules = RobotsRules.Parse(
            "User-agent: *\nDisallow: /\n\nUser-agent: downloadmanagergrabber\nDisallow: /x/\n", Agent);
        Assert.True(rules.IsAllowed("/index.html"));
        Assert.False(rules.IsAllowed("/x/y"));
    }

    [Fact]
    public void ConsecutiveUserAgentLinesShareAGroup()
    {
        var rules = RobotsRules.Parse("User-agent: a\nUser-agent: *\nDisallow: /p\n", Agent);
        Assert.False(rules.IsAllowed("/p/q"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("User-agent: *\nDisallow:\n")]
    [InlineData("garbage without colons")]
    public void EmptyOrPermissiveFilesAllowEverything(string? content)
        => Assert.True(RobotsRules.Parse(content, Agent).IsAllowed("/anything"));

    [Fact]
    public void LeadingBomDoesNotDropTheFirstGroup()
    {
        var rules = RobotsRules.Parse("﻿User-agent: *\nDisallow: /p\n", Agent);
        Assert.False(rules.IsAllowed("/p"));
    }

    [Fact]
    public void EmptyUserAgentLineDoesNotMatchUs()
    {
        var rules = RobotsRules.Parse("User-agent:\nAllow: /\n\nUser-agent: *\nDisallow: /p\n", Agent);
        Assert.False(rules.IsAllowed("/p"));
    }

    [Fact]
    public void PrefixOfOurTokenIsNotOurGroup()
    {
        var rules = RobotsRules.Parse("User-agent: download\nDisallow: /\n\nUser-agent: *\nDisallow: /p\n", Agent);
        Assert.True(rules.IsAllowed("/index.html"));
        Assert.False(rules.IsAllowed("/p"));
    }

    [Theory]
    [InlineData("Disallow: /my folder/", "/my%20folder/x")]
    [InlineData("Disallow: /my%20folder/", "/my%20folder/x")]
    [InlineData("Disallow: /private/", "/%70rivate/x")]
    [InlineData("Disallow: /café/", "/caf%C3%A9/menu")]
    [InlineData("Disallow: /caf%c3%a9/", "/caf%C3%A9/menu")]
    public void PercentEncodingIsCanonicalizedOnBothSides(string rule, string path)
        => Assert.False(RobotsRules.Parse($"User-agent: *\n{rule}\n", Agent).IsAllowed(path));

    [Fact]
    public void EncodedSlashStaysDistinctFromSlash()
        => Assert.True(RobotsRules.Parse("User-agent: *\nDisallow: /a/b\n", Agent).IsAllowed("/a%2Fb"));

    [Fact]
    public void EqualLengthTieGoesToAllow()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /page\nAllow: /page\n", Agent);
        Assert.True(rules.IsAllowed("/page"));
    }
}

using DownloadManager.Providers.TorBox;

namespace DownloadManager.TorBox.Tests;

public sealed class TorBoxApiAddressTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5123/torbox/v1/api/", "http://127.0.0.1:5123/torbox/v1/api/")]
    [InlineData("http://127.0.0.1:5123/torbox/v1/api", "http://127.0.0.1:5123/torbox/v1/api/")]
    [InlineData("http://localhost:5123/", "http://localhost:5123/")]
    [InlineData("http://[::1]:5123/mock/", "http://[::1]:5123/mock/")]
    [InlineData("https://api.torbox.app/v1/api/", "https://api.torbox.app/v1/api/")]
    public void ASandboxMayPointAtThisComputerOrTheRealProvider(string value, string expected)
    {
        var uri = TorBoxApiAddress.FromOverride(value, sandboxProfile: true, out var problem);
        Assert.Null(problem);
        Assert.Equal(expected, uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://example.invalid/v1/api/")]
    [InlineData("http://api.torbox.app/v1/api/")]
    [InlineData("https://api.torbox.app:8443/v1/api/")]
    [InlineData("https://api.torbox.app.example.invalid/v1/api/")]
    [InlineData("http://127.0.0.1.example.invalid/")]
    [InlineData("http://localhost@example.invalid/")]
    [InlineData("http://user:secret@127.0.0.1:5123/")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("file:///C:/mock")]
    [InlineData("not an address")]
    public void TheKeyCanNotBeSentAnywhereElse(string value)
    {
        Assert.Null(TorBoxApiAddress.FromOverride(value, sandboxProfile: true, out var problem));
        Assert.NotNull(problem);
        Assert.DoesNotContain(value, problem);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5123/")]
    [InlineData("https://api.torbox.app/v1/api/")]
    public void OutsideASandboxProfileNoOverrideCounts(string value)
    {
        Assert.Null(TorBoxApiAddress.FromOverride(value, sandboxProfile: false, out var problem));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingSetIsNoProblem(string? value)
    {
        Assert.Null(TorBoxApiAddress.FromOverride(value, sandboxProfile: false, out var problem));
        Assert.Null(problem);
    }
}

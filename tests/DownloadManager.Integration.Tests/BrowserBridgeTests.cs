using DownloadManager.Contracts;
using Xunit;

namespace DownloadManager.Integration.Tests;

public sealed class BrowserBridgeTests
{
    [Theory]
    [InlineData("olhbcdbjdnfoiinllnlpandggadfabfm")]
    [InlineData("pknddmneejadnnngffojnoecojhdfalc")]
    public void Trusts_the_store_and_unpacked_extension_ids(string id) =>
        Assert.True(BrowserBridge.IsTrustedExtensionId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test-extension")]
    [InlineData("OLHBCDBJDNFOIINLLNLPANDGGADFABFM")]
    [InlineData("olhbcdbjdnfoiinllnlpandggadfabf")]
    public void Refuses_any_other_extension_id(string? id) =>
        Assert.False(BrowserBridge.IsTrustedExtensionId(id));

    [Theory]
    [InlineData("chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/")]
    [InlineData("chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm")]
    [InlineData("chrome-extension://pknddmneejadnnngffojnoecojhdfalc/")]
    public void Trusts_the_store_and_unpacked_origins(string origin) =>
        Assert.True(BrowserBridge.IsTrustedOrigin(origin));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("olhbcdbjdnfoiinllnlpandggadfabfm")]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    [InlineData("chrome-extension://olhbcdbjdnfoiinllnlpandggadfabfm/evil")]
    [InlineData("https://olhbcdbjdnfoiinllnlpandggadfabfm/")]
    public void Refuses_any_other_origin(string? origin) =>
        Assert.False(BrowserBridge.IsTrustedOrigin(origin));
}

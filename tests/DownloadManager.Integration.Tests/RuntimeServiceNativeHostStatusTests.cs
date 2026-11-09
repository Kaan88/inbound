using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

public sealed partial class RuntimeServiceTests
{
    [Fact]
    public async Task HostStatusNamesTheBrowserThatCannotReachTheNativeHost()
    {
        await using var ctx = await ServiceContext.CreateAsync(_server, autoResume: false);

        // No registration reader at all, as in a run with no Host wiring.
        Assert.False(ctx.Service.GetSnapshot().Status.NativeHostRegistered);
        Assert.Equal("native host not registered", ctx.Service.GetSnapshot().Status.BrowserIntegration);

        ctx.Service.NativeHostState = () => new NativeHostRegistrationState(NativeHostKeyState.Usable, NativeHostKeyState.Absent);
        Assert.True(ctx.Service.GetSnapshot().Status.NativeHostRegistered);
        Assert.Equal("on", ctx.Service.GetSnapshot().Status.BrowserIntegration);

        ctx.Service.NativeHostState = () => new NativeHostRegistrationState(NativeHostKeyState.Absent, NativeHostKeyState.Usable);
        Assert.True(ctx.Service.GetSnapshot().Status.NativeHostRegistered);
        Assert.Equal("on; not registered for Chrome", ctx.Service.GetSnapshot().Status.BrowserIntegration);

        ctx.Service.NativeHostState = () => new NativeHostRegistrationState(NativeHostKeyState.Usable, NativeHostKeyState.Broken);
        Assert.Equal("on; Edge registration is broken", ctx.Service.GetSnapshot().Status.BrowserIntegration);
    }
}

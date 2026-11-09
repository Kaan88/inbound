using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The client's check that the pipe it opened is this user's Host (<see cref="PipeOwner"/>): what
/// <see cref="PipeOptions.CurrentUserOnly"/> checks, plus this user's own SID, so an elevated client (the token's default
/// owner is then the Administrators group) still reaches its own user's Host. The CLI and native host tests in
/// <see cref="HostHelloPipeTests"/> go through it against the real Host.
/// </summary>
[Trait("Platform", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class PipeOwnerTests
{
    private const string NoApi = "https://example.invalid/v1/api/";

    private static readonly SecurityIdentifier User = new("S-1-5-21-1000000000-2000000000-3000000000-1001");
    private static readonly SecurityIdentifier OtherUser = new("S-1-5-21-1000000000-2000000000-3000000000-1002");
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    [Fact]
    public void OnlyAPipeOwnedByThisUserOrItsTokensDefaultOwnerIsTrusted()
    {
        // Not elevated: the default owner is the user.
        Assert.True(PipeOwner.IsTrusted(User, User, User));
        Assert.False(PipeOwner.IsTrusted(OtherUser, User, User));
        Assert.False(PipeOwner.IsTrusted(LocalSystem, User, User));
        Assert.False(PipeOwner.IsTrusted(Administrators, User, User));
        Assert.False(PipeOwner.IsTrusted(null, User, User));

        // Elevated (an administrator terminal): the default owner is Administrators. A Host that isn't elevated owns its
        // pipe as the user, which CurrentUserOnly alone would refuse; an elevated Host owns it as Administrators.
        Assert.True(PipeOwner.IsTrusted(User, User, Administrators));
        Assert.True(PipeOwner.IsTrusted(Administrators, User, Administrators));
        Assert.False(PipeOwner.IsTrusted(OtherUser, User, Administrators));
    }

    [Fact]
    public void TheHostServesOnlyAPipeOwnedByThisUserItself()
    {
        Assert.True(PipeOwner.IsHostsOwn(User, User));
        // Another account that made the pipe first, and an elevated Host's default owner: neither is served.
        Assert.False(PipeOwner.IsHostsOwn(OtherUser, User));
        Assert.False(PipeOwner.IsHostsOwn(Administrators, User));
        Assert.False(PipeOwner.IsHostsOwn(LocalSystem, User));
        Assert.False(PipeOwner.IsHostsOwn(null, User));
        Assert.False(PipeOwner.IsHostsOwn(User, null));
    }

    [Fact]
    public async Task TheRealHostsPipesAreOwnedByThisUserItselfAndPassTheCheck()
    {
        await using var host = await SandboxHost.StartAsync(NoApi);
        using var current = WindowsIdentity.GetCurrent();
        foreach (var name in new[] { host.PipeName, host.PipeName + "-hello" })
        {
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10_000);
            PipeOwner.Verify(pipe);
            // The user, not the token's default owner: an elevated Host must still be reachable from a client that isn't.
            Assert.Equal(current.User, (SecurityIdentifier?)pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        }
    }

    [Fact]
    public async Task APipeThisProcessMadeIsTrustedAndItsOwnerIsWhatTheCheckReads()
    {
        var name = $"DownloadManager-owner-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        await accepted;

        PipeOwner.Verify(client);
        using var current = WindowsIdentity.GetCurrent();
        var owner = (SecurityIdentifier?)client.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        Assert.True(owner == current.User || owner == current.Owner);
    }
}

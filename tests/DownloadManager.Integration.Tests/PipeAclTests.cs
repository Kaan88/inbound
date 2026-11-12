using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// The Host's named pipes let in the current user and LocalSystem only, and are owned by the current user (moved here
/// from tools/HostChecks). The DACL and owner are read back from the real Host's pipes, as a client sees them, rather
/// than from the security object the Host builds. The owner matters because clients check it (PipeOwner): an elevated
/// Host's pipe would otherwise be owned by Administrators, and a client that is not elevated would refuse it.
/// Deny rules are not checked: they can only narrow who gets in.
/// </summary>
[Trait("Platform", "Windows")]
[SupportedOSPlatform("windows")]
public sealed class PipeAclTests
{
    private const string NoApi = "https://example.invalid/v1/api/";

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    [Fact]
    public async Task TheRealHostsPipesAllowOnlyThisUserAndLocalSystem()
    {
        await using var host = await SandboxHost.StartAsync(NoApi);
        using var current = WindowsIdentity.GetCurrent();
        foreach (var name in new[] { host.PipeName, host.PipeName + "-hello" })
        {
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10_000);
            var problems = AclProblems(pipe.GetAccessControl(), current.User!);
            Assert.True(problems.Count == 0, $"{name}: {string.Join("; ", problems)}");
        }
    }

    /// <summary>
    /// The check itself refuses a pipe anyone may open, one that leaves out LocalSystem, and one owned by someone else.
    /// </summary>
    [Fact]
    public void TheCheckRefusesAWideOpenPipeAndAMissingLocalSystemRule()
    {
        using var current = WindowsIdentity.GetCurrent();
        var user = current.User!;

        var good = new PipeSecurity();
        good.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        good.AddAccessRule(new PipeAccessRule(LocalSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        good.SetOwner(user);
        Assert.Empty(AclProblems(good, user));

        var adminsOwn = new PipeSecurity();
        adminsOwn.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        adminsOwn.AddAccessRule(new PipeAccessRule(LocalSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        adminsOwn.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        Assert.Contains(AclProblems(adminsOwn, user), problem => problem.Contains("owned by"));

        foreach (var broad in new[] { WellKnownSidType.WorldSid, WellKnownSidType.AuthenticatedUserSid, WellKnownSidType.BuiltinUsersSid })
        {
            var open = new PipeSecurity();
            open.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            open.AddAccessRule(new PipeAccessRule(LocalSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
            open.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(broad, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            open.SetOwner(user);
            Assert.NotEmpty(AclProblems(open, user));
        }

        var userOnly = new PipeSecurity();
        userOnly.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        userOnly.SetOwner(user);
        Assert.NotEmpty(AclProblems(userOnly, user));
    }

    /// <summary>
    /// What is wrong with a pipe's DACL: every Allow rule must be this user's or LocalSystem's, and both must have
    /// full control, and the owner must be this user. Empty when it is right.
    /// </summary>
    private static List<string> AclProblems(PipeSecurity security, SecurityIdentifier user)
    {
        var problems = new List<string>();
        var userFull = false;
        var systemFull = false;
        foreach (PipeAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            var sid = (SecurityIdentifier)rule.IdentityReference;
            var full = (rule.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl;
            if (sid.Equals(user))
            {
                userFull |= full;
            }
            else if (sid.Equals(LocalSystem))
            {
                systemFull |= full;
            }
            else
            {
                problems.Add($"allows {sid.Value} ({rule.PipeAccessRights})");
            }
        }

        if (!userFull)
        {
            problems.Add("this user has no full-control rule");
        }

        if (!systemFull)
        {
            problems.Add("LocalSystem has no full-control rule");
        }

        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (owner is null || !owner.Equals(user))
        {
            problems.Add($"owned by {owner?.Value ?? "nobody"}, not this user");
        }

        return problems;
    }
}

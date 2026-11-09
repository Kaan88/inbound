using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DownloadManager.Tray;

public static class PipeSecurityFactory
{
    /// <summary>
    /// Full control for the current user and LocalSystem only. No Everyone,
    /// Authenticated Users, or built-in Users ACE is added. The owner is the user
    /// itself, also in an elevated process, whose default owner would otherwise be
    /// the Administrators group: clients check the owner (<c>PipeOwner</c>), and a
    /// client that isn't elevated must still reach an elevated Host.
    /// </summary>
    public static PipeSecurity CreateForCurrentUser()
    {
        var security = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User
                          ?? throw new InvalidOperationException("The current user has no SID.");
        security.AddAccessRule(new PipeAccessRule(
            currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.SetOwner(currentUser);
        return security;
    }
}

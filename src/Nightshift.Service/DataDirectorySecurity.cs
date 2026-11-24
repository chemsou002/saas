using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Nightshift.Service;

/// <summary>
/// The database decides what gets restored, so ordinary users must not be able to edit it.
/// When running as LocalSystem the data directory gets a protected ACL (SYSTEM + Administrators only), and an
/// existing directory owned by anyone else is treated as tampering: the service refuses to start.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirectorySecurity
{
    public static void EnsureSecure(string path, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        using var self = WindowsIdentity.GetCurrent();
        if (!self.IsSystem)
        {
            Directory.CreateDirectory(path);
#pragma warning disable CA1848, CA1873
            logger.LogWarning("Not running as LocalSystem (console/development mode): data directory {Path} is not ACL-hardened.", path);
#pragma warning restore CA1848, CA1873
            return;
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            var owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || (owner != system && owner != admins))
            {
                throw new UnauthorizedAccessException(
                    $"Data directory '{path}' is owned by {owner?.Value ?? "unknown"}, not SYSTEM/Administrators. Refusing to use it.");
            }
        }

        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (info.Exists)
        {
            info.SetAccessControl(acl);
        }
        else
        {
            info.Create(acl);
        }
    }
}

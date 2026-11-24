using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using Nightshift.Core.Engine;

namespace Nightshift.Ipc;

/// <summary>Establishes who is on the other end of a pipe connection. Identity never comes from the request body.</summary>
public interface IClientIdentityResolver
{
    /// <returns>The caller, or null if the caller cannot be authenticated (the connection is then refused).</returns>
    ClientIdentity? Resolve(NamedPipeServerStream pipe);
}

/// <summary>
/// Windows: impersonates the client at Identification level (the client connects with
/// TokenImpersonationLevel.Identification, so the service can read the token but never act as the user).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPipeClientIdentityResolver : IClientIdentityResolver
{
    public ClientIdentity? Resolve(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ClientIdentity? identity = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var wi = WindowsIdentity.GetCurrent(System.Security.Principal.TokenAccessLevels.Query);
                if (wi.IsAnonymous || wi.IsGuest || wi.User is null)
                {
                    return;
                }
                var principal = new WindowsPrincipal(wi);
                var isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator)
                              || wi.User.IsWellKnown(WellKnownSidType.LocalSystemSid);
                identity = new ClientIdentity(wi.User.Value, wi.Name, isAdmin);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        return identity;
    }
}

/// <summary>
/// Non-Windows development/test hosts only: the OS user running the process. The service itself is Windows-only.
/// </summary>
public sealed class ProcessUserIdentityResolver : IClientIdentityResolver
{
    public ClientIdentity? Resolve(NamedPipeServerStream pipe) =>
        new("unix:" + Environment.UserName, Environment.UserName, false);
}

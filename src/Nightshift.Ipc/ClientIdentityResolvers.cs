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

    /// <summary>Why the last resolution failed, for the service log.</summary>
    string? LastFailure => null;
}

/// <summary>
/// Windows: impersonates the client at Identification level (the client connects with
/// TokenImpersonationLevel.Identification, so the service can read the token but never act as the user).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPipeClientIdentityResolver : IClientIdentityResolver
{
    /// <summary>Why the last resolution failed (for the service log); null after a success.</summary>
    public string? LastFailure { get; private set; }

    public ClientIdentity? Resolve(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        SecurityIdentifier? sid = null;
        var isAdmin = false;
        var rejected = false;
        try
        {
            // Keep the impersonated section minimal: read the token's SID and group membership only.
            pipe.RunAsClient(() =>
            {
                using var wi = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                if (wi.IsAnonymous || wi.IsGuest || wi.User is null)
                {
                    rejected = true;
                    return;
                }
                sid = wi.User;
                isAdmin = wi.User.IsWellKnown(WellKnownSidType.LocalSystemSid)
                          || new WindowsPrincipal(wi).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
#pragma warning disable CA1031 // any failure to identify the caller means "not authenticated", never a crash
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LastFailure = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        if (rejected || sid is null)
        {
            LastFailure = "anonymous, guest or unidentified caller";
            return null;
        }

        // Account-name lookup happens outside impersonation (it may need access the client token lacks).
        string name;
        try
        {
            name = sid.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            name = sid.Value;
        }
        LastFailure = null;
        return new ClientIdentity(sid.Value, name, isAdmin);
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

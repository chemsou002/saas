using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightshift.Ipc;

public sealed record IpcServerOptions
{
    public string PipeName { get; init; } = IpcDefaults.PipeName;
    public int MaxConnections { get; init; } = 4;

    /// <summary>A connected client must deliver each complete request within this time.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Named-pipe server. Local only: .NET creates server pipes with PIPE_REJECT_REMOTE_CLIENTS, the ACL also
/// denies the NETWORK group, and only SYSTEM, Administrators, the service account and interactive users may connect.
/// Users may not create pipe instances, and the first instance is created with FILE_FLAG_FIRST_PIPE_INSTANCE so
/// the service refuses to start if another process already owns the name (pipe squatting).
/// </summary>
public sealed partial class IpcServer(
    IpcServerOptions options,
    IIpcRequestHandler handler,
    IClientIdentityResolver identityResolver,
    ILogger<IpcServer>? logger = null) : IAsyncDisposable
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];

    public bool IsRunning { get; private set; }

    /// <exception cref="IOException">The pipe name is already in use.</exception>
    /// <exception cref="UnauthorizedAccessException">Windows: another process already created the pipe (squatting).</exception>
    public void Start()
    {
        if (IsRunning)
        {
            return;
        }
        var first = CreatePipe(firstInstance: true);
        IsRunning = true;
        _loops.Add(Task.Run(() => AcceptLoopAsync(first, _cts.Token)));
        for (var i = 1; i < options.MaxConnections; i++)
        {
            _loops.Add(Task.Run(() => AcceptLoopAsync(null, _cts.Token)));
        }
        LogStarted(_logger, options.PipeName, options.MaxConnections);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }
        IsRunning = false;
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream? initial, CancellationToken ct)
    {
        var pipe = initial;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                pipe ??= CreatePipe(firstInstance: false);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogConnectionError(_logger, ex);
                try
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    pipe = null;
                }
            }
        }
        if (pipe is not null)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var caller = identityResolver.Resolve(pipe);
        if (caller is null)
        {
            await WriteAsync(pipe, IpcResponse.Failure(null, IpcErrorCodes.Unauthenticated, "Caller could not be authenticated."), ct).ConfigureAwait(false);
            return;
        }

        while (pipe.IsConnected && !ct.IsCancellationRequested)
        {
            byte[]? frame;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(options.RequestTimeout);
                try
                {
                    frame = await FrameCodec.ReadAsync(pipe, IpcDefaults.MaxFrameBytes, timeout.Token).ConfigureAwait(false);
                }
                catch (IpcProtocolException ex)
                {
                    await WriteAsync(pipe, IpcResponse.Failure(null, ex.Code, ex.Message), ct).ConfigureAwait(false);
                    return; // the stream position is unknown after a bad frame: drop the connection
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return; // idle or slow client
                }
            }
            if (frame is null)
            {
                return;
            }
            var response = handler.Handle(frame, caller);
            await WriteAsync(pipe, response, ct).ConfigureAwait(false);
        }
    }

    private static Task WriteAsync(Stream s, IpcResponse response, CancellationToken ct) =>
        FrameCodec.WriteAsync(s, JsonSerializer.SerializeToUtf8Bytes(response, IpcJson.Options), ct);

    private NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateSecuredWindowsPipe(firstInstance);
        }
        return new NamedPipeServerStream(options.PipeName, PipeDirection.InOut, options.MaxConnections,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateSecuredWindowsPipe(bool firstInstance)
    {
        var pipeOptions = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(options.PipeName, PipeDirection.InOut, options.MaxConnections,
            PipeTransmissionMode.Byte, pipeOptions, 0, 0, BuildPipeSecurity());
    }

    [SupportedOSPlatform("windows")]
    internal static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using (var self = WindowsIdentity.GetCurrent())
        {
            // The account the service runs as (SYSTEM in production; the developer when run as a console app).
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        // Interactive (logged-on) users may connect and exchange messages, but not create instances.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "IPC listening on pipe {Pipe} ({Max} connections)")]
    private static partial void LogStarted(ILogger logger, string pipe, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IPC connection error")]
    private static partial void LogConnectionError(ILogger logger, Exception ex);
}

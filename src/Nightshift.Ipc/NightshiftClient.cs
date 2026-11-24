using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Nightshift.Core.Engine;

namespace Nightshift.Ipc;

/// <summary>The service is not installed, not running, or not reachable within the timeout.</summary>
public sealed class ServiceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The service answered with an error.</summary>
public sealed class IpcRemoteException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Client used by the CLI today and by the tray UI in Phase 5. One connection per call.</summary>
public sealed class NightshiftClient(string pipeName = IpcDefaults.PipeName, TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public Task<SessionStatusSnapshot> StartSessionAsync(StartSessionPayload payload, CancellationToken ct = default) =>
        CallAsync<SessionStatusSnapshot>(IpcCommands.StartSession, JsonSerializer.SerializeToElement(payload, IpcJson.Options), ct);

    public Task<SessionStatusSnapshot> StopSessionAsync(CancellationToken ct = default) => CallAsync<SessionStatusSnapshot>(IpcCommands.StopSession, null, ct);

    public Task<SessionStatusSnapshot> PauseSessionAsync(CancellationToken ct = default) => CallAsync<SessionStatusSnapshot>(IpcCommands.PauseSession, null, ct);

    public Task<SessionStatusSnapshot> ResumeSessionAsync(CancellationToken ct = default) => CallAsync<SessionStatusSnapshot>(IpcCommands.ResumeSession, null, ct);

    public Task<SessionStatusResult> GetSessionStatusAsync(CancellationToken ct = default) => CallAsync<SessionStatusResult>(IpcCommands.GetSessionStatus, null, ct);

    public Task<SystemStatusSnapshot> GetSystemStatusAsync(CancellationToken ct = default) => CallAsync<SystemStatusSnapshot>(IpcCommands.GetSystemStatus, null, ct);

    public async Task<T> CallAsync<T>(string command, JsonElement? payload, CancellationToken ct = default)
    {
        var response = await SendAsync(new IpcRequest { Command = command, Payload = payload }, ct).ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new IpcRemoteException(response.Error?.Code ?? "UNKNOWN", response.Error?.Message ?? "Unknown error.");
        }
        if (response.Result is not { } result)
        {
            throw new IpcProtocolException(IpcErrorCodes.MalformedRequest, "Response has no result.");
        }
        return result.Deserialize<T>(IpcJson.Options) ?? throw new IpcProtocolException(IpcErrorCodes.MalformedRequest, "Empty result.");
    }

    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        // Identification: the service may learn who we are, but can never act as us.
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException && !ct.IsCancellationRequested)
        {
            throw new ServiceUnavailableException("The NIGHTSHIFT service is not running or not reachable.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ServiceUnavailableException("Access to the NIGHTSHIFT service was denied (only interactive local users may connect).", ex);
        }

        try
        {
            await FrameCodec.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request, IpcJson.Options), timeoutCts.Token).ConfigureAwait(false);
            var frame = await FrameCodec.ReadAsync(pipe, IpcDefaults.MaxFrameBytes, timeoutCts.Token).ConfigureAwait(false)
                ?? throw new ServiceUnavailableException("The service closed the connection without answering.");
            return JsonSerializer.Deserialize<IpcResponse>(frame, IpcJson.Options)
                ?? throw new IpcProtocolException(IpcErrorCodes.MalformedRequest, "Empty response.");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ServiceUnavailableException("The service did not answer in time.", ex);
        }
        catch (IOException ex)
        {
            throw new ServiceUnavailableException("The connection to the service was lost.", ex);
        }
        catch (JsonException ex)
        {
            throw new IpcProtocolException(IpcErrorCodes.MalformedRequest, $"Invalid response: {ex.Message}");
        }
    }
}

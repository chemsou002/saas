using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nightshift.Core.Engine;

namespace Nightshift.Ipc;

public interface IIpcRequestHandler
{
    /// <summary>Never throws: every failure becomes an error response.</summary>
    IpcResponse Handle(ReadOnlySpan<byte> requestJson, ClientIdentity caller);
}

/// <summary>Validates requests and dispatches them to the <see cref="SessionManager"/>.</summary>
public sealed partial class SessionCommandHandler(
    SessionManager manager,
    Func<SystemStatusSnapshot> systemStatus,
    ILogger<SessionCommandHandler>? logger = null) : IIpcRequestHandler
{
    public const int MaxApplications = 16;
    public const int MaxNameLength = 128;
    public const int MaxPathLength = 1024;

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public IpcResponse Handle(ReadOnlySpan<byte> requestJson, ClientIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(requestJson, IpcJson.Strict);
        }
        catch (JsonException)
        {
            return IpcResponse.Failure(null, IpcErrorCodes.MalformedRequest, "Request is not valid JSON for this protocol.");
        }
        if (request is null)
        {
            return IpcResponse.Failure(null, IpcErrorCodes.MalformedRequest, "Empty request.");
        }

        var id = request.RequestId;
        if (id is null || !RequestIdRegex().IsMatch(id))
        {
            return IpcResponse.Failure(null, IpcErrorCodes.MalformedRequest, "requestId must be 1-64 characters [A-Za-z0-9_-].");
        }
        if (request.Version != IpcDefaults.ProtocolVersion)
        {
            return IpcResponse.Failure(id, IpcErrorCodes.UnsupportedVersion, $"Protocol version {request.Version} is not supported (expected {IpcDefaults.ProtocolVersion}).");
        }
        if (request.Command is null || !IpcCommands.All.Contains(request.Command))
        {
            return IpcResponse.Failure(id, IpcErrorCodes.UnknownCommand, "Unknown command.");
        }
        if (request.Command != IpcCommands.StartSession && !IsEmptyPayload(request.Payload))
        {
            return IpcResponse.Failure(id, EngineErrorCodes.InvalidRequest, $"{request.Command} takes no payload.");
        }

        try
        {
            object result = request.Command switch
            {
                IpcCommands.StartSession => manager.StartSession(ParseStart(request.Payload, caller)),
                IpcCommands.StopSession => manager.StopSession(caller),
                IpcCommands.PauseSession => manager.PauseSession(caller),
                IpcCommands.ResumeSession => manager.ResumeSession(caller),
                IpcCommands.GetSessionStatus => new SessionStatusResult(manager.GetSessionStatus(), manager.GetLastSession()),
                IpcCommands.GetSystemStatus => systemStatus(),
                _ => throw new InvalidOperationException("unreachable"),
            };
            return new IpcResponse { RequestId = id, Ok = true, Result = JsonSerializer.SerializeToElement(result, IpcJson.Options) };
        }
        catch (SessionEngineException ex)
        {
            if (ex.Code == EngineErrorCodes.NotAuthorized)
            {
                LogRejected(_logger, request.Command, caller.UserName);
            }
            return IpcResponse.Failure(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            LogFailure(_logger, ex, request.Command);
            return IpcResponse.Failure(id, EngineErrorCodes.InternalError, "The service could not complete the request. See the service log.");
        }
    }

    internal static StartSessionRequest ParseStart(JsonElement? payload, ClientIdentity caller)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element)
        {
            throw Invalid("START_SESSION requires a payload object.");
        }
        StartSessionPayload p;
        try
        {
            p = element.Deserialize<StartSessionPayload>(IpcJson.Strict) ?? throw Invalid("Empty payload.");
        }
        catch (JsonException ex)
        {
            throw Invalid($"Invalid START_SESSION payload: {ex.Message}");
        }

        var mode = p.Mode?.ToLowerInvariant() switch
        {
            "timed" => SessionMode.Timed,
            "unlimited" => SessionMode.Unlimited,
            _ => throw Invalid("mode must be 'timed' or 'unlimited'."),
        };
        TimeSpan? duration = null;
        if (p.DurationSeconds is { } secs)
        {
            if (double.IsNaN(secs) || double.IsInfinity(secs) || secs < 1 || secs > TimeSpan.FromDays(7).TotalSeconds)
            {
                throw Invalid("durationSeconds must be between 1 and 604800.");
            }
            duration = TimeSpan.FromSeconds(Math.Round(secs));
        }
        var policy = p.PowerPolicy?.ToLowerInvariant() switch
        {
            null or "automatic" => PowerPolicy.Automatic,
            "ac-only" => PowerPolicy.AcOnly,
            _ => throw Invalid("powerPolicy must be 'automatic' or 'ac-only'."),
        };

        var apps = new List<SelectedApplication>();
        if (p.Applications is { } list)
        {
            if (list.Count > MaxApplications)
            {
                throw Invalid($"At most {MaxApplications} applications per session.");
            }
            foreach (var a in list)
            {
                apps.Add(ValidateApp(a));
            }
        }

        return new StartSessionRequest
        {
            Caller = caller,
            Mode = mode,
            Duration = duration,
            Applications = apps,
            PowerPolicy = policy,
            BatteryWarningPercent = p.BatteryWarningPercent ?? 25,
            BatteryCriticalPercent = p.BatteryCriticalPercent ?? 15,
            ConfigureLid = p.ConfigureLid ?? true,
            TurnOffDisplay = p.TurnOffDisplay ?? true,
            VentilationAcknowledged = p.VentilationAcknowledged,
        };
    }

    private static SelectedApplication ValidateApp(ApplicationPayload a)
    {
        if (a is null || string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > MaxNameLength || a.Name.Any(char.IsControl))
        {
            throw Invalid($"Application name must be 1-{MaxNameLength} printable characters.");
        }
        string? path = null;
        if (a.ExecutablePath is { } raw)
        {
            if (raw.Length is 0 or > MaxPathLength || raw.Any(char.IsControl) || raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0
                || !Path.IsPathFullyQualified(raw) || raw.StartsWith(@"\\", StringComparison.Ordinal))
            {
                throw Invalid("executablePath must be a local, fully qualified path.");
            }
            path = raw;
        }
        if (a.ProcessId is { } pid && pid <= 4)
        {
            throw Invalid("processId must identify a user process (> 4).");
        }
        return new SelectedApplication(a.Name.Trim(), path, a.ProcessId);
    }

    private static bool IsEmptyPayload(JsonElement? payload) =>
        payload is null
        || payload.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (payload.Value.ValueKind == JsonValueKind.Object && !payload.Value.EnumerateObject().Any());

    private static SessionEngineException Invalid(string message) => new(EngineErrorCodes.InvalidRequest, message);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex RequestIdRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected unauthorized {Command} from {User}")]
    private static partial void LogRejected(ILogger logger, string command, string user);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled error while processing {Command}")]
    private static partial void LogFailure(ILogger logger, Exception ex, string command);
}

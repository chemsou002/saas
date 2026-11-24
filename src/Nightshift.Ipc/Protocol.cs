using System.Text.Json;
using System.Text.Json.Serialization;
using Nightshift.Core.Engine;

namespace Nightshift.Ipc;

public static class IpcDefaults
{
    /// <summary>Local named pipe (\\.\pipe\NightshiftService.v1). Never network-reachable: remote clients are rejected.</summary>
    public const string PipeName = "NightshiftService.v1";
    public const int ProtocolVersion = 1;
    public const int MaxFrameBytes = 64 * 1024;
}

public static class IpcCommands
{
    public const string StartSession = "START_SESSION";
    public const string StopSession = "STOP_SESSION";
    public const string PauseSession = "PAUSE_SESSION";
    public const string ResumeSession = "RESUME_SESSION";
    public const string GetSessionStatus = "GET_SESSION_STATUS";
    public const string GetSystemStatus = "GET_SYSTEM_STATUS";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        StartSession, StopSession, PauseSession, ResumeSession, GetSessionStatus, GetSystemStatus,
    };
}

public static class IpcErrorCodes
{
    public const string MalformedRequest = "MALFORMED_REQUEST";
    public const string UnsupportedVersion = "UNSUPPORTED_VERSION";
    public const string UnknownCommand = "UNKNOWN_COMMAND";
    public const string FrameTooLarge = "FRAME_TOO_LARGE";
    public const string Unauthenticated = "UNAUTHENTICATED";
}

public sealed record IpcRequest
{
    public int Version { get; init; } = IpcDefaults.ProtocolVersion;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public required string Command { get; init; }
    public JsonElement? Payload { get; init; }
}

public sealed record IpcError(string Code, string Message);

public sealed record IpcResponse
{
    public string? RequestId { get; init; }
    public bool Ok { get; init; }
    public JsonElement? Result { get; init; }
    public IpcError? Error { get; init; }

    public static IpcResponse Failure(string? requestId, string code, string message) =>
        new() { RequestId = requestId, Ok = false, Error = new IpcError(code, message) };
}

public sealed record ApplicationPayload
{
    public required string Name { get; init; }
    public string? ExecutablePath { get; init; }
    public int? ProcessId { get; init; }
}

/// <summary>START_SESSION payload. Unknown fields are rejected.</summary>
public sealed record StartSessionPayload
{
    /// <summary>"timed" or "unlimited".</summary>
    public required string Mode { get; init; }
    public double? DurationSeconds { get; init; }
    public IReadOnlyList<ApplicationPayload>? Applications { get; init; }

    /// <summary>"automatic" (default) or "ac-only".</summary>
    public string? PowerPolicy { get; init; }
    public int? BatteryWarningPercent { get; init; }
    public int? BatteryCriticalPercent { get; init; }
    public bool? ConfigureLid { get; init; }
    public bool? TurnOffDisplay { get; init; }
    public bool VentilationAcknowledged { get; init; }
}

public sealed record SessionStatusResult(SessionStatusSnapshot Active, SessionStatusSnapshot? Last);

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 16,
    };

    /// <summary>Strict options for client-supplied payloads: unknown members are an error.</summary>
    public static readonly JsonSerializerOptions Strict = new(Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

using Nightshift.Core.Engine;

namespace Nightshift.Service;

/// <summary>Bound from the "Nightshift" section of appsettings.json.</summary>
public sealed class ServiceSettings
{
    public const string ServiceName = "NightshiftService";

    /// <summary>Default: %ProgramData%\Nightshift.</summary>
    public string? DataDirectory { get; set; }
    public string PipeName { get; set; } = Ipc.IpcDefaults.PipeName;
    public int TickMilliseconds { get; set; } = 1000;
    public int ServiceHeartbeatSeconds { get; set; } = 10;
    public int SessionHeartbeatSeconds { get; set; } = 10;
    public int BatteryPollSeconds { get; set; } = 30;
    public int AcLossGraceSeconds { get; set; } = 60;
    public uint DisplayIdleTimeoutSeconds { get; set; } = 60;
    public int MaxConsecutiveTickFailures { get; set; } = 5;

    public string ResolveDataDirectory() =>
        DataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nightshift");

    public EngineOptions ToEngineOptions() => new()
    {
        HeartbeatPersistInterval = TimeSpan.FromSeconds(Math.Clamp(SessionHeartbeatSeconds, 1, 300)),
        BatteryPollInterval = TimeSpan.FromSeconds(Math.Clamp(BatteryPollSeconds, 5, 300)),
        AcLossGracePeriod = TimeSpan.FromSeconds(Math.Clamp(AcLossGraceSeconds, 0, 3600)),
        DisplayIdleTimeoutSeconds = Math.Clamp(DisplayIdleTimeoutSeconds, 15u, 3600u),
        MaxConsecutiveTickFailures = Math.Clamp(MaxConsecutiveTickFailures, 1, 100),
    };
}

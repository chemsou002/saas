using System.Reflection;
using Nightshift.Core.Compatibility;
using Nightshift.Core.Engine;
using Nightshift.Core.Power;

namespace Nightshift.Service;

/// <summary>Service self-monitoring state, exposed through GET_SYSTEM_STATUS.</summary>
public sealed class ServiceHealth(TimeProvider time)
{
    public Guid InstanceId { get; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();
    public bool? PreviousShutdownClean { get; set; }
    public DateTimeOffset? LastLoopIteration { get; set; }
    public string? LastError { get; set; }

    public static string Version =>
        typeof(ServiceHealth).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Healthy = the monitoring loop ran recently and monitoring is not failing.</summary>
    public ServiceHealthSnapshot Snapshot(SessionManager manager, TimeSpan tickInterval)
    {
        var now = time.GetUtcNow();
        var lastTick = manager.LastTick;
        var loopAlive = lastTick is { } t && now - t < tickInterval * 5 + TimeSpan.FromSeconds(5);
        return new ServiceHealthSnapshot
        {
            InstanceId = InstanceId,
            Version = Version,
            StartedAt = StartedAt,
            Uptime = now - StartedAt,
            LastTick = lastTick,
            Healthy = loopAlive && manager.ConsecutiveTickFailures == 0,
            ConsecutiveTickFailures = manager.ConsecutiveTickFailures,
            RecoveryPending = manager.RecoveryPending,
            PreviousShutdownClean = PreviousShutdownClean,
            LastError = manager.LastError ?? LastError,
        };
    }

    public static SystemStatusSnapshot BuildSystemStatus(
        SessionManager manager, ServiceHealth health, ISystemCapabilitiesProvider caps, IPowerStatusProvider power,
        IPowerSettingsStore settings, TimeSpan tickInterval)
    {
        var c = caps.Get();
        var p = power.Get();
        var scheme = settings.GetActiveSchemeId();
        return new SystemStatusSnapshot
        {
            Capabilities = c,
            Power = p,
            Compatibility = CompatibilityAssessor.Assess(c, p,
                settings.Read(scheme, PowerSettingKeys.LidCloseAction, PowerSource.AC),
                settings.Read(scheme, PowerSettingKeys.LidCloseAction, PowerSource.DC)),
            Service = health.Snapshot(manager, tickInterval),
        };
    }
}

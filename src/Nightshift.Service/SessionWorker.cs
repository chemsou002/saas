using Microsoft.Extensions.Options;
using Nightshift.Core.Engine;
using Nightshift.Core.Power;
using Nightshift.Ipc;

namespace Nightshift.Service;

/// <summary>
/// Service lifecycle: record the instance, run startup recovery BEFORE accepting commands, subscribe to power
/// events, open the IPC pipe, then drive <see cref="SessionManager.Tick"/>. On stop: close IPC, end the session
/// (restoring settings), record a clean shutdown.
/// </summary>
public sealed partial class SessionWorker(
    SessionManager manager,
    ISessionRepository repository,
    IPowerEventSource powerEvents,
    IpcServer ipc,
    ServiceHealth health,
    IOptions<ServiceSettings> settings,
    TimeProvider time,
    ILogger<SessionWorker> logger) : BackgroundService
{
    private readonly ServiceSettings _settings = settings.Value;
    private bool _subscribed;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var previous = repository.RecordServiceStart(health.InstanceId, now, ServiceHealth.Version);
        health.PreviousShutdownClean = previous?.CleanShutdown;
        if (previous is { CleanShutdown: false })
        {
            LogUncleanShutdown(logger, previous.InstanceId, previous.LastHeartbeat);
            repository.AddEvent(null, "warning",
                $"The previous service instance did not shut down cleanly (last heartbeat {previous.LastHeartbeat:O}).");
        }

        var recovery = manager.RecoverOnStartup();
        LogRecovery(logger, recovery.InterruptedSessions.Count, recovery.Restore?.Success, recovery.RecoveryPending);

        powerEvents.PowerEventReceived += OnPowerEvent;
        _subscribed = true;
        try
        {
            powerEvents.Start();
        }
        catch (Exception ex)
        {
            // Sessions still work (battery is polled), but lid/AC events will be missing.
            health.LastError = $"Power notifications unavailable: {ex.Message}";
            LogPowerEventsFailed(logger, ex);
        }

        ipc.Start(); // throws if another process already owns the pipe name: refuse to run
        repository.AddEvent(null, "info", $"Service started (instance {health.InstanceId}, version {ServiceHealth.Version}).");
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(_settings.TickMilliseconds, 100, 10_000));
        var heartbeatEvery = TimeSpan.FromSeconds(Math.Clamp(_settings.ServiceHeartbeatSeconds, 1, 300));
        var nextHeartbeat = time.GetUtcNow();
        using var timer = new PeriodicTimer(tick, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                manager.Tick(); // never throws: failures are counted and trigger a safe stop
                var now = time.GetUtcNow();
                health.LastLoopIteration = now;
                if (now >= nextHeartbeat)
                {
                    nextHeartbeat = now + heartbeatEvery;
                    try
                    {
                        repository.RecordServiceHeartbeat(health.InstanceId, now);
                    }
                    catch (Exception ex)
                    {
                        health.LastError = $"Heartbeat write failed: {ex.Message}";
                        LogHeartbeatFailed(logger, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await ipc.DisposeAsync().ConfigureAwait(false);
        try
        {
            manager.Shutdown();
        }
        catch (Exception ex)
        {
            LogShutdownFailed(logger, ex);
        }
        if (_subscribed)
        {
            powerEvents.PowerEventReceived -= OnPowerEvent;
        }
        powerEvents.Dispose();
        try
        {
            repository.AddEvent(null, "info", "Service stopped cleanly.");
            repository.RecordServiceStop(health.InstanceId, time.GetUtcNow());
        }
        catch (Exception ex)
        {
            LogShutdownFailed(logger, ex);
        }
    }

    private void OnPowerEvent(PowerEvent e)
    {
        try
        {
            manager.HandlePowerEvent(e);
        }
        catch (Exception ex)
        {
            LogPowerEventHandlingFailed(logger, ex, e.Kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Previous service instance {Instance} terminated unexpectedly (last heartbeat {Heartbeat})")]
    private static partial void LogUncleanShutdown(ILogger logger, Guid instance, DateTimeOffset heartbeat);

    [LoggerMessage(Level = LogLevel.Information, Message = "Startup recovery: {Count} interrupted session(s), restore success {Restored}, still pending {Pending}")]
    private static partial void LogRecovery(ILogger logger, int count, bool? restored, bool pending);

    [LoggerMessage(Level = LogLevel.Error, Message = "Power notifications could not be registered")]
    private static partial void LogPowerEventsFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service heartbeat could not be persisted")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error during shutdown")]
    private static partial void LogShutdownFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling power event {Kind} failed")]
    private static partial void LogPowerEventHandlingFailed(ILogger logger, Exception ex, PowerEventKind kind);
}

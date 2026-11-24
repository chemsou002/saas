using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nightshift.Core.Engine;
using Nightshift.Core.Power;
using Nightshift.Ipc;
using Nightshift.Service;
using Nightshift.Storage;

namespace Nightshift.Engine.Tests;

/// <summary>
/// Runs the real <see cref="SessionWorker"/> (service lifecycle), real pipes and real SQLite, with the Windows power
/// layer replaced by fakes. "Service process" lifetimes are simulated by creating and discarding hosts.
/// Not a substitute for installing the service on Windows (see docs/TESTING.md).
/// </summary>
public sealed class ServiceHostTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakePowerStore _store = new();
    private readonly FakeCaps _caps = new(TestData.Laptop());
    private readonly FakePowerStatus _power = new(TestData.OnAc());
    private readonly string _dbPath;
    private readonly string _pipe = "nightshift-svc-" + Guid.NewGuid().ToString("N")[..12];

    public ServiceHostTests()
    {
        _dbPath = _dir.File("nightshift.db");
        _store.Set(PowerSettingKeys.LidCloseAction, ac: 1, dc: 1);
        _store.Set(PowerSettingKeys.VideoPowerDownTimeout, ac: 600, dc: 300);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private bool Original() =>
        _store.Get(PowerSettingKeys.LidCloseAction, PowerSource.AC) == 1 && _store.Get(PowerSettingKeys.VideoPowerDownTimeout, PowerSource.AC) == 600;

    private sealed record Host(SessionWorker Worker, SessionManager Manager, FakeKeepAwake KeepAwake, FakeEvents Events, SqliteSessionRepository Repository, ServiceHealth Health, IpcServer Ipc);

    private Host CreateHost()
    {
        var time = TimeProvider.System;
        var db = new Database(_dbPath);
        var repo = new SqliteSessionRepository(db, time);
        var keepAwake = new FakeKeepAwake();
        var events = new FakeEvents();
        var manager = new SessionManager(new SessionManagerDependencies
        {
            Settings = _store,
            KeepAwake = keepAwake,
            Capabilities = _caps,
            PowerStatus = _power,
            Recovery = new SqliteRecoveryStore(db, time),
            Repository = repo,
            Time = time,
        });
        var health = new ServiceHealth(time);
        var handler = new SessionCommandHandler(manager, () => ServiceHealth.BuildSystemStatus(manager, health, _caps, _power, _store, TimeSpan.FromMilliseconds(100)));
        var ipc = new IpcServer(new IpcServerOptions { PipeName = _pipe }, handler, new ProcessUserIdentityResolver());
        var settings = Options.Create(new ServiceSettings { TickMilliseconds = 100, ServiceHeartbeatSeconds = 1, PipeName = _pipe });
        var worker = new SessionWorker(manager, repo, events, ipc, health, settings, time, NullLogger<SessionWorker>.Instance);
        return new Host(worker, manager, keepAwake, events, repo, health, ipc);
    }

    private NightshiftClient Client() => new(_pipe, TimeSpan.FromSeconds(5));

    private static StartSessionPayload Payload(double? seconds) => new()
    {
        Mode = seconds is null ? "unlimited" : "timed",
        DurationSeconds = seconds,
        VentilationAcknowledged = true,
    };

    private static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail("Condition not reached in time.");
    }

    [Fact]
    public async Task Service_runs_a_timed_session_to_completion_without_any_client_connected()
    {
        var host = CreateHost();
        await host.Worker.StartAsync(CancellationToken.None);
        try
        {
            await Client().StartSessionAsync(Payload(2));
            Assert.True(host.KeepAwake.IsHeld);
            // The "UI" disconnects here: every client call above used its own short-lived connection.
            await WaitUntil(() => Task.FromResult(!host.Manager.GetSessionStatus().HasSession), TimeSpan.FromSeconds(10));

            Assert.True(Original());
            Assert.False(host.KeepAwake.IsHeld);
            var status = await Client().GetSessionStatusAsync();
            Assert.Equal(SessionStatus.Completed, status.Last!.Status);
        }
        finally
        {
            await host.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Power_events_reach_the_engine_through_the_worker()
    {
        var host = CreateHost();
        await host.Worker.StartAsync(CancellationToken.None);
        try
        {
            await Client().StartSessionAsync(Payload(null));
            Assert.True(host.Events.Started);
            host.Events.Raise(PowerEventKind.LidClosed);
            Assert.True((await Client().GetSessionStatusAsync()).Active.LidClosed);
        }
        finally
        {
            await host.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Clean_service_stop_restores_and_next_start_sees_clean_shutdown()
    {
        var first = CreateHost();
        await first.Worker.StartAsync(CancellationToken.None);
        await Client().StartSessionAsync(Payload(null));
        Assert.False(Original());
        await first.Worker.StopAsync(CancellationToken.None);

        Assert.True(Original());
        Assert.False(first.KeepAwake.IsHeld);
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => new NightshiftClient(_pipe, TimeSpan.FromMilliseconds(300)).GetSessionStatusAsync());

        var second = CreateHost();
        await second.Worker.StartAsync(CancellationToken.None);
        try
        {
            var system = await Client().GetSystemStatusAsync();
            Assert.True(system.Service.PreviousShutdownClean);
            Assert.False(system.Service.RecoveryPending);
            Assert.Equal(TerminationReason.ServiceStopping, (await Client().GetSessionStatusAsync()).Last!.TerminationReason);
        }
        finally
        {
            await second.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unexpected_service_termination_is_detected_and_recovered_on_restart()
    {
        var crashed = CreateHost();
        await crashed.Worker.StartAsync(CancellationToken.None);
        var started = await Client().StartSessionAsync(Payload(3600));
        // Simulated crash: the process vanishes. No StopAsync, no Shutdown, no restore. Only the pipe goes away.
        await crashed.Ipc.DisposeAsync();
        Assert.False(Original());

        var restarted = CreateHost();
        await restarted.Worker.StartAsync(CancellationToken.None); // startup recovery runs before IPC opens
        try
        {
            Assert.True(Original());
            var system = await Client().GetSystemStatusAsync();
            Assert.False(system.Service.PreviousShutdownClean);
            var last = (await Client().GetSessionStatusAsync()).Last!;
            Assert.Equal(started.SessionId, last.SessionId);
            Assert.Equal(SessionStatus.Interrupted, last.Status);
            Assert.Equal(TerminationReason.ServiceTerminatedUnexpectedly, last.TerminationReason);
            Assert.Contains(restarted.Repository.GetEvents(started.SessionId), e => e.Type == "recovery");

            // and it is usable again
            Assert.Equal(SessionStatus.Running, (await Client().StartSessionAsync(Payload(null))).Status);
        }
        finally
        {
            await restarted.Worker.StopAsync(CancellationToken.None);
        }
    }
}

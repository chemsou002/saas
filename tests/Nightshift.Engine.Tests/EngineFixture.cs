using Microsoft.Extensions.Time.Testing;
using Nightshift.Core.Engine;
using Nightshift.Core.Power;
using Nightshift.Storage;

namespace Nightshift.Engine.Tests;

/// <summary>
/// One "machine": fake power layer that survives service restarts, plus a real SQLite database on disk.
/// <see cref="NewManager"/> simulates a (re)started service process on the same machine.
/// </summary>
internal sealed class EngineFixture : IDisposable
{
    public static readonly ClientIdentity Alice = new("S-1-5-21-1-1001", "PC\\alice", false);
    public static readonly ClientIdentity Bob = new("S-1-5-21-1-1002", "PC\\bob", false);
    public static readonly ClientIdentity Admin = new("S-1-5-21-1-500", "PC\\admin", true);

    public TempDir Dir { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 24, 20, 0, 0, TimeSpan.Zero));
    public FakePowerStore Store { get; } = new();
    public FakeCaps Caps { get; } = new(TestData.Laptop());
    public FakePowerStatus Power { get; } = new(TestData.OnAc());
    public HashSet<int> DeadProcesses { get; } = [];
    public Database Db { get; }
    public SqliteSessionRepository Repository { get; }
    public SqliteRecoveryStore Recovery { get; }
    public FakeKeepAwake KeepAwake { get; private set; } = new();

    public EngineOptions Options { get; set; } = new()
    {
        HeartbeatPersistInterval = TimeSpan.FromSeconds(10),
        BatteryPollInterval = TimeSpan.FromSeconds(30),
        AcLossGracePeriod = TimeSpan.FromSeconds(60),
        PendingRestoreRetryInterval = TimeSpan.FromMinutes(1),
    };

    public EngineFixture()
    {
        Store.Set(PowerSettingKeys.LidCloseAction, ac: 1, dc: 1);
        Store.Set(PowerSettingKeys.VideoPowerDownTimeout, ac: 600, dc: 300);
        Db = new Database(Dir.File("nightshift.db"));
        Repository = new SqliteSessionRepository(Db, Time);
        Recovery = new SqliteRecoveryStore(Db, Time);
    }

    /// <summary>A fresh service process: new in-memory state and a new power request handle.</summary>
    public SessionManager NewManager(ISessionRepository? repository = null)
    {
        KeepAwake = new FakeKeepAwake();
        return new SessionManager(new SessionManagerDependencies
        {
            Settings = Store,
            KeepAwake = KeepAwake,
            Capabilities = Caps,
            PowerStatus = Power,
            Recovery = Recovery,
            Repository = repository ?? Repository,
            Time = Time,
            IsProcessAlive = pid => !DeadProcesses.Contains(pid),
        }, Options);
    }

    public static StartSessionRequest Timed(TimeSpan duration, ClientIdentity? caller = null) => new()
    {
        Caller = caller ?? Alice,
        Mode = SessionMode.Timed,
        Duration = duration,
        Applications = [new SelectedApplication("Claude Code", @"C:\Tools\claude.exe", 4242)],
        VentilationAcknowledged = true,
    };

    public static StartSessionRequest Unlimited(ClientIdentity? caller = null) => new()
    {
        Caller = caller ?? Alice,
        Mode = SessionMode.Unlimited,
        VentilationAcknowledged = true,
    };

    public uint? Lid(PowerSource s) => Store.Get(PowerSettingKeys.LidCloseAction, s);

    public uint? Video(PowerSource s) => Store.Get(PowerSettingKeys.VideoPowerDownTimeout, s);

    public bool SettingsAreOriginal() =>
        Lid(PowerSource.AC) == 1 && Lid(PowerSource.DC) == 1 && Video(PowerSource.AC) == 600 && Video(PowerSource.DC) == 300;

    public void Advance(TimeSpan by, SessionManager m, TimeSpan? step = null)
    {
        var s = step ?? TimeSpan.FromSeconds(1);
        for (var t = TimeSpan.Zero; t < by; t += s)
        {
            Time.Advance(s);
            m.Tick();
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Dir.Dispose();
    }
}

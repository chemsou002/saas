using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightshift.Core.Compatibility;
using Nightshift.Core.Power;
using Nightshift.Core.Recovery;
using Nightshift.Core.Session;
using Nightshift.Core.Util;
using Nightshift.Core.Verification;
using Nightshift.Windows;

namespace Nightshift.Poc;

internal static class Program
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "no-powercfg", "ack-ventilation", "launch-heartbeat", "keep-lid", "force", "help",
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> Main(string[] args)
    {
        CommandLine cl;
        try
        {
            cl = new CommandLine(args, Flags);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        try
        {
            return cl.Command switch
            {
                "heartbeat" => await HeartbeatAsync(cl).ConfigureAwait(false),
                "verify" => Verify(cl),
                "detect" or "session" or "restore" or "status" when !OperatingSystem.IsWindows() =>
                    Fail("This command needs Windows 10 or 11. ('heartbeat' and 'verify' run on any OS.)"),
                "detect" when OperatingSystem.IsWindows() => Detect(cl),
                "session" when OperatingSystem.IsWindows() => await SessionAsync(cl).ConfigureAwait(false),
                "restore" when OperatingSystem.IsWindows() => Restore(cl),
                "status" when OperatingSystem.IsWindows() => Status(cl),
                _ => Help(),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            NIGHTSHIFT Phase 1 proof of concept — Keep working. Even when closed.

            nightshift-poc detect   [--json] [--no-powercfg]
                Report power capabilities, current lid/sleep/display settings and a compatibility verdict. Changes nothing.

            nightshift-poc session  --duration <15m|2h|1h30m|unlimited>
                                    [--launch-heartbeat] [--watch-pid <pid>]...
                                    [--display-off auto|monitor-power|idle-timeout|none] [--display-off-delay <30s>]
                                    [--battery-critical <15>] [--battery-warning <25>] [--keep-lid] [--ack-ventilation]
                Back up settings, set lid-close to "Do nothing", block automatic sleep, let the display turn off,
                monitor, then restore everything. Ctrl+C stops the session and restores.

            nightshift-poc restore  [--force]
                Restore settings from a session that did not finish (crash, power loss, closed console).

            nightshift-poc status
                Show the pending recovery record, if any.

            nightshift-poc heartbeat --out <file> [--interval <1s>]
                Test application: writes a timestamp every interval. Gaps prove the system was suspended.

            nightshift-poc verify   --heartbeat <file> [--session-log <file>] [--max-gap <5s>] [--json]
                Check that the heartbeat kept running during every closed-lid interval and that settings were restored.
            """);
        return 0;
    }

    // ---------------------------------------------------------------- detect

    [SupportedOSPlatform("windows")]
    private static int Detect(CommandLine cl)
    {
        var json = cl.Flag("json");
        var noPowercfg = cl.Flag("no-powercfg");
        cl.EnsureAllUsed();

        var info = new WindowsSystemInfo();
        var store = new WindowsPowerSettingsStore();
        var caps = info.Get();
        var power = ((IPowerStatusProvider)info).Get();
        var scheme = store.GetActiveSchemeId();
        var settings = PowerSettingKeys.All.ToDictionary(
            k => k.Name,
            k => new { AC = store.Read(scheme, k, PowerSource.AC), DC = store.Read(scheme, k, PowerSource.DC) });
        var lidAc = store.Read(scheme, PowerSettingKeys.LidCloseAction, PowerSource.AC);
        var lidDc = store.Read(scheme, PowerSettingKeys.LidCloseAction, PowerSource.DC);
        var report = CompatibilityAssessor.Assess(caps, power, lidAc, lidDc);
        var displayMethod = SessionOptions.Resolve(DisplayOffMethod.Auto, caps);
        var powercfg = noPowercfg ? null : RunPowercfgAvailableStates();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                capabilities = caps,
                powerStatus = power,
                activeScheme = scheme,
                settings,
                compatibility = report,
                displayOffMethod = displayMethod,
                powercfgAvailableSleepStates = powercfg,
            }, Json));
            return 0;
        }

        Console.WriteLine("NIGHTSHIFT — hardware & power detection (read-only)\n");
        Section("System");
        Row("OS", $"{caps.OsDescription} (build {caps.OsBuild}, {(caps.IsWindows11 ? "Windows 11" : "Windows 10")})");
        Row("Elevated", Yes(caps.IsElevated));
        Section("Power capabilities (reported by Windows)");
        Row("Lid switch present", Yes(caps.LidPresent));
        Row("Modern Standby (S0 low power idle)", Yes(caps.ModernStandby) + (caps.ModernStandby ? caps.ModernStandbyNetworkConnected ? " — connected" : " — disconnected" : ""));
        Row("S1 / S2 / S3 sleep", $"{Yes(caps.SleepS1)} / {Yes(caps.SleepS2)} / {Yes(caps.SleepS3)}");
        Row("Hibernate (S4) / hiberfile", $"{Yes(caps.HibernateS4)} / {Yes(caps.HiberFilePresent)}");
        Row("Fast startup (hiberboot)", Yes(caps.FastStartup));
        Row("ACPI thermal control", Yes(caps.ThermalControl));
        Row("Battery present", Yes(caps.BatteryPresent));
        Row("Lid action set by Group Policy", Yes(caps.LidActionManagedByPolicy));
        Section("Power status");
        Row("Power source", power.OnAcPower switch { true => "AC (charger)", false => "Battery", null => "Unknown" });
        Row("Battery", power.BatteryPercent is { } p ? $"{p}%" : "n/a");
        Row("Battery saver", Yes(power.BatterySaverOn));
        Section($"Active power scheme {scheme}");
        Row("Lid close action (AC / battery)", $"{LidText(lidAc)} / {LidText(lidDc)}");
        foreach (var k in new[] { PowerSettingKeys.StandbyTimeout, PowerSettingKeys.HibernateTimeout, PowerSettingKeys.VideoPowerDownTimeout })
        {
            Row(k.Name + " (AC / battery)", $"{Seconds(store.Read(scheme, k, PowerSource.AC))} / {Seconds(store.Read(scheme, k, PowerSource.DC))}");
        }
        Section("Compatibility");
        Row("Verdict", report.Verdict.ToString());
        Row("Display-off method (auto)", displayMethod.ToString());
        foreach (var f in report.Findings)
        {
            Console.WriteLine($"  [{f.Severity.ToString().ToUpperInvariant()}] {f.Message}");
        }
        Console.WriteLine("\n  Note: this verdict says whether closed-lid mode can be CONFIGURED. Whether this laptop really keeps");
        Console.WriteLine("  running with the lid closed must be proven with: nightshift-poc session --duration 10m --launch-heartbeat");
        if (powercfg is not null)
        {
            Section("Cross-check: powercfg /a");
            Console.WriteLine(string.Join(Environment.NewLine, powercfg.Split('\n').Select(l => "  " + l.TrimEnd())));
        }
        return 0;
    }

    private static string? RunPowercfgAvailableStates()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", "/a")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
            {
                return null;
            }
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return output.Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- session

    [SupportedOSPlatform("windows")]
    private static async Task<int> SessionAsync(CommandLine cl)
    {
        var durationText = cl.Value("duration") ?? throw new ArgumentException("--duration is required (e.g. 10m, 2h, unlimited).");
        var options = new SessionOptions
        {
            Duration = DurationParser.Parse(durationText),
            ConfigureLid = !cl.Flag("keep-lid"),
            DisplayOff = ParseDisplayOff(cl.Value("display-off") ?? "auto"),
            DisplayOffDelay = DurationParser.Parse(cl.Value("display-off-delay") ?? "15s") ?? TimeSpan.FromSeconds(15),
            BatteryCriticalPercent = Percent(cl.Value("battery-critical") ?? "15"),
            BatteryWarningPercent = Percent(cl.Value("battery-warning") ?? "25"),
        };
        var launchHeartbeat = cl.Flag("launch-heartbeat");
        var ack = cl.Flag("ack-ventilation");
        var watched = cl.Values("watch-pid").Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToList();
        cl.EnsureAllUsed();
        if (options.BatteryWarningPercent <= options.BatteryCriticalPercent)
        {
            throw new ArgumentException("--battery-warning must be higher than --battery-critical.");
        }

        var paths = new AppPaths();
        paths.EnsureCreated();
        var recovery = new RecoveryStore(paths.RecoveryFile);
        var store = new WindowsPowerSettingsStore();

        if (!RecoverIfOrphaned(recovery, store))
        {
            return 1;
        }

        // The service and this PoC both modify the lid action: never run them at the same time.
        if (await ServiceSessionActiveAsync().ConfigureAwait(false))
        {
            return Fail("The NIGHTSHIFT service is running a session. Stop it first (nightshiftctl stop).");
        }

        // Mandatory ventilation warning: shown every session, acknowledgement required the first time.
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n" + SafetyText.VentilationWarning + "\n");
        Console.ResetColor();
        var settings = UserSettings.Load(paths.SettingsFile);
        if (!settings.VentilationAcknowledged)
        {
            if (!ack)
            {
                Console.Write($"Type YES to confirm: \"{SafetyText.VentilationAcknowledgement}\" > ");
                if (!string.Equals(Console.ReadLine()?.Trim(), "YES", StringComparison.Ordinal))
                {
                    return Fail("Not confirmed. No settings were changed.");
                }
            }
            settings = settings with { VentilationAcknowledged = true, VentilationAcknowledgedAt = DateTimeOffset.UtcNow };
            settings.Save(paths.SettingsFile);
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var logPath = Path.Combine(paths.LogDirectory, $"session-{stamp}.jsonl");
        var heartbeatPath = Path.Combine(paths.LogDirectory, $"heartbeat-{stamp}.log");

        Process? heartbeat = null;
        if (launchHeartbeat)
        {
            heartbeat = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["heartbeat", "--out", heartbeatPath])
            {
                UseShellExecute = false,
                CreateNoWindow = true, // own hidden console: Ctrl+C here does not reach the test app
            }) ?? throw new InvalidOperationException("Could not start the heartbeat test application.");
            watched.Add(heartbeat.Id);
            Console.WriteLine($"Heartbeat test application started (PID {heartbeat.Id}) → {heartbeatPath}");
        }

        using var log = new SessionEventLog(logPath);
        log.EntryWritten += PrintEntry;
        var info = new WindowsSystemInfo();
        using var keepAwake = new WindowsKeepAwakeRequest();
        using var window = new PowerEventWindow();
        var probes = new ProcessProbe();
        var session = new PocSession(options with { WatchedProcessIds = watched }, new SessionDependencies
        {
            Settings = store,
            KeepAwake = keepAwake,
            Capabilities = info,
            PowerStatus = info,
            Display = window,
            PowerEvents = window,
            Recovery = recovery,
            Log = log,
            IsProcessAlive = probes.IsAlive,
        });

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        // Closing the console window, logoff or shutdown: restore synchronously before Windows ends the process.
        using var onClose = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => session.RestoreNow());
        using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => session.RestoreNow());

        Console.WriteLine($"Session log → {logPath}");
        Console.WriteLine("Press Ctrl+C to stop the session and restore your settings.\n");
        var result = await session.RunAsync(cts.Token).ConfigureAwait(false);

        if (heartbeat is not null)
        {
            if (!heartbeat.HasExited)
            {
                heartbeat.Kill(); // our own test child only
                await heartbeat.WaitForExitAsync().ConfigureAwait(false);
            }
            heartbeat.Dispose();
        }

        Console.WriteLine();
        Section("Session summary");
        Row("Stop reason", result.Reason.ToString());
        Row("Elapsed", result.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        Row("Lid closures", result.LidCloseCount.ToString(CultureInfo.InvariantCulture));
        Row("Unexpected suspends", result.SuspendCount.ToString(CultureInfo.InvariantCulture));
        Row("Settings restored", result.Restore is null ? "nothing to restore" : result.Restore.Success ? "yes" : "NO — run 'nightshift-poc restore'");
        if (result.Error is not null)
        {
            Row("Error", result.Error);
        }

        if (launchHeartbeat && File.Exists(heartbeatPath))
        {
            Section("Verification");
            var report = HeartbeatAnalyzer.Analyze(HeartbeatAnalyzer.ReadSamples(heartbeatPath), SessionEventLog.Read(logPath).ToList(), TimeSpan.FromSeconds(5));
            PrintVerification(report);
        }
        else
        {
            Console.WriteLine($"\nVerify a test app with: nightshift-poc verify --heartbeat <file> --session-log \"{logPath}\"");
        }
        return result.Error is null && result.Restore?.Success != false ? 0 : 1;
    }

    private static async Task<bool> ServiceSessionActiveAsync()
    {
        try
        {
            var status = await new Nightshift.Ipc.NightshiftClient(timeout: TimeSpan.FromSeconds(2)).GetSessionStatusAsync().ConfigureAwait(false);
            return status.Active.HasSession;
        }
        catch (Exception ex) when (ex is Nightshift.Ipc.ServiceUnavailableException or Nightshift.Ipc.IpcRemoteException or Nightshift.Ipc.IpcProtocolException)
        {
            return false; // service not installed / not running
        }
    }

    /// <summary>Crash recovery: a record whose owner process is gone means an interrupted session.</summary>
    [SupportedOSPlatform("windows")]
    private static bool RecoverIfOrphaned(RecoveryStore recovery, IPowerSettingsStore store)
    {
        var record = recovery.Load();
        if (record is null)
        {
            return true;
        }
        if (IsNightshiftProcess(record.OwnerProcessId))
        {
            Console.Error.WriteLine($"Another NIGHTSHIFT session is active (PID {record.OwnerProcessId}).");
            return false;
        }
        Console.WriteLine($"Found an unfinished session from {record.StartedAt.ToLocalTime():g}. Restoring its settings first…");
        var report = new PowerConfigurator(store, recovery).Restore(record);
        PrintRestore(report);
        return report.Success;
    }

    // ---------------------------------------------------------------- restore / status

    [SupportedOSPlatform("windows")]
    private static int Restore(CommandLine cl)
    {
        var force = cl.Flag("force");
        cl.EnsureAllUsed();
        var recovery = new RecoveryStore(new AppPaths().RecoveryFile);
        var record = recovery.Load();
        if (record is null)
        {
            Console.WriteLine("Nothing to restore: no unfinished session.");
            return 0;
        }
        if (!force && IsNightshiftProcess(record.OwnerProcessId))
        {
            return Fail($"Session is still running in PID {record.OwnerProcessId}. Stop it with Ctrl+C, or use --force.");
        }
        var report = new PowerConfigurator(new WindowsPowerSettingsStore(), recovery).Restore(record);
        PrintRestore(report);
        return report.Success ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    private static int Status(CommandLine cl)
    {
        cl.EnsureAllUsed();
        var record = new RecoveryStore(new AppPaths().RecoveryFile).Load();
        if (record is null)
        {
            Console.WriteLine("No active or unfinished session.");
            return 0;
        }
        Console.WriteLine(JsonSerializer.Serialize(record, Json));
        Console.WriteLine(IsNightshiftProcess(record.OwnerProcessId)
            ? $"Owner process {record.OwnerProcessId} is running (session active)."
            : "Owner process is gone: run 'nightshift-poc restore'.");
        return 0;
    }

    // ---------------------------------------------------------------- heartbeat / verify (any OS)

    private static async Task<int> HeartbeatAsync(CommandLine cl)
    {
        var path = cl.Value("out") ?? throw new ArgumentException("--out is required.");
        var interval = DurationParser.Parse(cl.Value("interval") ?? "1s") ?? TimeSpan.FromSeconds(1);
        cl.EnsureAllUsed();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.WriteLine($"Heartbeat → {path} every {interval.TotalSeconds:0.#}s (Ctrl+C to stop)");
        await HeartbeatWriter.RunAsync(path, interval, TimeProvider.System, cts.Token).ConfigureAwait(false);
        return 0;
    }

    private static int Verify(CommandLine cl)
    {
        var hb = cl.Value("heartbeat") ?? throw new ArgumentException("--heartbeat is required.");
        var sessionLog = cl.Value("session-log");
        var maxGap = DurationParser.Parse(cl.Value("max-gap") ?? "5s") ?? TimeSpan.FromSeconds(5);
        var json = cl.Flag("json");
        cl.EnsureAllUsed();
        var entries = sessionLog is null ? [] : SessionEventLog.Read(sessionLog).ToList();
        var report = HeartbeatAnalyzer.Analyze(HeartbeatAnalyzer.ReadSamples(hb), entries, maxGap);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { report, overall = report.Overall }, Json));
        }
        else
        {
            PrintVerification(report);
        }
        return report.Overall == "PASS" ? 0 : 1;
    }

    // ---------------------------------------------------------------- output helpers

    private static void PrintVerification(VerificationReport r)
    {
        Row("Heartbeat samples", $"{r.Samples} ({r.First?.ToLocalTime():T} → {r.Last?.ToLocalTime():T})");
        Row($"Gaps > {r.MaxAllowedGap.TotalSeconds:0}s", r.Gaps.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var g in r.Gaps)
        {
            Console.WriteLine($"    {g.From.ToLocalTime():T} → {g.To.ToLocalTime():T} ({g.Length.TotalSeconds:0}s)");
        }
        foreach (var i in r.LidIntervals)
        {
            Console.WriteLine($"  Lid closed {i.Closed.ToLocalTime():T} → {(i.Opened is { } o ? o.ToLocalTime().ToString("T", CultureInfo.CurrentCulture) : "end of session")}: " +
                              $"{i.Verdict} (longest heartbeat gap {i.LongestGap.TotalSeconds:0.#}s)");
        }
        Row("Unexpected suspends", r.SuspendEvents.ToString(CultureInfo.InvariantCulture));
        Console.ForegroundColor = r.Overall == "PASS" ? ConsoleColor.Green : r.Overall.StartsWith("FAIL", StringComparison.Ordinal) ? ConsoleColor.Red : ConsoleColor.Yellow;
        Row("RESULT", r.Overall);
        Console.ResetColor();
    }

    private static void PrintRestore(RestoreReport report)
    {
        foreach (var i in report.Items)
        {
            Console.WriteLine($"  {i.Change.Name} [{i.Change.Source}] → {i.Outcome}{(i.Error is null ? "" : $": {i.Error}")}");
        }
        Console.WriteLine(report.Success ? "Original settings restored." : "Restore incomplete; the recovery record was kept.");
    }

    private static void PrintEntry(SessionLogEntry e)
    {
        Console.ForegroundColor = e.Type switch
        {
            "error" => ConsoleColor.Red,
            "warning" => ConsoleColor.Yellow,
            "state" => ConsoleColor.Cyan,
            "power" => ConsoleColor.Magenta,
            "change" or "restore" => ConsoleColor.Green,
            _ => ConsoleColor.Gray,
        };
        Console.WriteLine($"{e.Time.ToLocalTime():HH:mm:ss} {e.Type,-8} {e.Message}");
        Console.ResetColor();
    }

    private static void Section(string title) => Console.WriteLine($"\n== {title}");

    private static void Row(string label, string value) => Console.WriteLine($"  {label,-38} {value}");

    private static string Yes(bool b) => b ? "yes" : "no";

    private static string Seconds(uint? s) => s switch { null => "n/a", 0 => "never", _ => $"{s}s" };

    private static string LidText(uint? v) => v is null ? "n/a" : Enum.IsDefined(typeof(LidCloseAction), v.Value) ? ((LidCloseAction)v.Value).ToString() : v.Value.ToString(CultureInfo.InvariantCulture);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int Percent(string s)
    {
        var v = int.Parse(s, CultureInfo.InvariantCulture);
        return v is >= 1 and <= 99 ? v : throw new ArgumentException("Battery thresholds must be between 1 and 99.");
    }

    private static DisplayOffMethod ParseDisplayOff(string s) => s.ToLowerInvariant() switch
    {
        "auto" => DisplayOffMethod.Auto,
        "monitor-power" => DisplayOffMethod.MonitorPower,
        "idle-timeout" => DisplayOffMethod.IdleTimeout,
        "none" => DisplayOffMethod.None,
        _ => throw new ArgumentException("--display-off must be auto, monitor-power, idle-timeout or none."),
    };

    private static bool IsNightshiftProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && string.Equals(p.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Read-only liveness checks. Holds the Process handle so a recycled PID is not mistaken for the original.</summary>
internal sealed class ProcessProbe
{
    private readonly Dictionary<int, Process?> _processes = [];

    public bool IsAlive(int pid)
    {
        if (!_processes.TryGetValue(pid, out var p))
        {
            try
            {
                p = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                p = null;
            }
            _processes[pid] = p;
        }
        try
        {
            return p is not null && !p.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

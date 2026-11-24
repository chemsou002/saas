using System.Globalization;
using System.Text.Json;
using Nightshift.Core.Engine;
using Nightshift.Core.Util;
using Nightshift.Ipc;
using Nightshift.Poc;

namespace Nightshift.Ctl;

internal static class Program
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "ack-ventilation", "ac-only", "keep-lid", "keep-display",
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

        var client = new NightshiftClient(cl.Value("pipe") ?? IpcDefaults.PipeName);
        try
        {
            return cl.Command switch
            {
                "start" => await StartAsync(cl, client).ConfigureAwait(false),
                "stop" => Print(cl, await Simple(cl, () => client.StopSessionAsync()).ConfigureAwait(false)),
                "pause" => Print(cl, await Simple(cl, () => client.PauseSessionAsync()).ConfigureAwait(false)),
                "resume" => Print(cl, await Simple(cl, () => client.ResumeSessionAsync()).ConfigureAwait(false)),
                "status" => await StatusAsync(cl, client).ConfigureAwait(false),
                "system" => await SystemAsync(cl, client).ConfigureAwait(false),
                _ => Help(),
            };
        }
        catch (ServiceUnavailableException ex)
        {
            Console.Error.WriteLine($"{ex.Message} Is the service installed and running? (sc.exe query NightshiftService)");
            return 3;
        }
        catch (IpcRemoteException ex)
        {
            Console.Error.WriteLine($"[{ex.Code}] {ex.Message}");
            return 1;
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
            nightshiftctl — control the NIGHTSHIFT background service

              start   --duration <15m|2h|1h30m|unlimited> [--app <name>[=<path>]]... [--pid <pid>]...
                      [--ac-only] [--battery-warning 25] [--battery-critical 15] [--keep-lid] [--keep-display]
                      [--ack-ventilation]
              stop | pause | resume
              status  [--json]
              system  [--json]

            Common: --pipe <name> (default NightshiftService.v1)

            The session runs inside the service: closing this window, the terminal or a future UI does not end it.
            """);
        return 0;
    }

    private static async Task<int> StartAsync(CommandLine cl, NightshiftClient client)
    {
        var durationText = cl.Value("duration") ?? throw new ArgumentException("--duration is required (e.g. 2h or unlimited).");
        var duration = DurationParser.Parse(durationText);
        var apps = new List<ApplicationPayload>();
        foreach (var a in cl.Values("app"))
        {
            var eq = a.IndexOf('=', StringComparison.Ordinal);
            apps.Add(eq < 0 ? new ApplicationPayload { Name = a } : new ApplicationPayload { Name = a[..eq], ExecutablePath = a[(eq + 1)..] });
        }
        foreach (var p in cl.Values("pid"))
        {
            var pid = int.Parse(p, CultureInfo.InvariantCulture);
            apps.Add(new ApplicationPayload { Name = ProcessName(pid), ProcessId = pid });
        }
        var payload = new StartSessionPayload
        {
            Mode = duration is null ? "unlimited" : "timed",
            DurationSeconds = duration?.TotalSeconds,
            Applications = apps,
            PowerPolicy = cl.Flag("ac-only") ? "ac-only" : "automatic",
            BatteryWarningPercent = cl.Value("battery-warning") is { } w ? int.Parse(w, CultureInfo.InvariantCulture) : null,
            BatteryCriticalPercent = cl.Value("battery-critical") is { } c ? int.Parse(c, CultureInfo.InvariantCulture) : null,
            ConfigureLid = !cl.Flag("keep-lid"),
            TurnOffDisplay = !cl.Flag("keep-display"),
            VentilationAcknowledged = Acknowledge(cl.Flag("ack-ventilation")),
        };
        var json = cl.Flag("json");
        cl.Value("pipe");
        cl.EnsureAllUsed();
        if (!payload.VentilationAcknowledged)
        {
            Console.Error.WriteLine("Not confirmed. No session was started.");
            return 1;
        }
        var snapshot = await client.StartSessionAsync(payload).ConfigureAwait(false);
        return json ? PrintJson(snapshot) : PrintSnapshot(snapshot);
    }

    private static bool Acknowledge(bool preAcknowledged)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(SafetyText.VentilationWarning);
        Console.ResetColor();
        if (preAcknowledged)
        {
            Console.WriteLine($"Acknowledged: {SafetyText.VentilationAcknowledgement}");
            return true;
        }
        Console.Write($"Type YES to confirm \"{SafetyText.VentilationAcknowledgement}\" > ");
        return string.Equals(Console.ReadLine()?.Trim(), "YES", StringComparison.Ordinal);
    }

    private static async Task<SessionStatusSnapshot> Simple(CommandLine cl, Func<Task<SessionStatusSnapshot>> call)
    {
        cl.Flag("json");
        cl.Value("pipe");
        cl.EnsureAllUsed();
        return await call().ConfigureAwait(false);
    }

    private static int Print(CommandLine cl, SessionStatusSnapshot s) => cl.Flag("json") ? PrintJson(s) : PrintSnapshot(s);

    private static async Task<int> StatusAsync(CommandLine cl, NightshiftClient client)
    {
        var json = cl.Flag("json");
        cl.Value("pipe");
        cl.EnsureAllUsed();
        var r = await client.GetSessionStatusAsync().ConfigureAwait(false);
        if (json)
        {
            return PrintJson(r);
        }
        if (r.Active.HasSession)
        {
            PrintSnapshot(r.Active);
        }
        else
        {
            Console.WriteLine("No active session.");
            if (r.Last is { } last)
            {
                Console.WriteLine("\nLast session:");
                PrintSnapshot(last);
            }
        }
        return 0;
    }

    private static async Task<int> SystemAsync(CommandLine cl, NightshiftClient client)
    {
        var json = cl.Flag("json");
        cl.Value("pipe");
        cl.EnsureAllUsed();
        var s = await client.GetSystemStatusAsync().ConfigureAwait(false);
        if (json)
        {
            return PrintJson(s);
        }
        var c = s.Capabilities;
        Row("Service", $"{(s.Service.Healthy ? "healthy" : "UNHEALTHY")} · v{s.Service.Version} · up {s.Service.Uptime:d\\.hh\\:mm\\:ss}");
        Row("Previous shutdown clean", s.Service.PreviousShutdownClean?.ToString() ?? "first run");
        Row("Recovery pending", s.Service.RecoveryPending.ToString());
        if (s.Service.LastError is { } err)
        {
            Row("Last error", err);
        }
        Row("OS", $"{c.OsDescription} (build {c.OsBuild})");
        Row("Lid / Modern Standby / S3", $"{c.LidPresent} / {c.ModernStandby} / {c.SleepS3}");
        Row("Power", s.Power.OnAcPower switch { true => "AC", false => "Battery", null => "Unknown" } + (s.Power.BatteryPercent is { } p ? $" · {p}%" : ""));
        Row("Compatibility", s.Compatibility.Verdict.ToString());
        foreach (var f in s.Compatibility.Findings.Where(f => f.Severity != Nightshift.Core.Compatibility.FindingSeverity.Info))
        {
            Console.WriteLine($"  [{f.Severity}] {f.Message}");
        }
        return 0;
    }

    private static int PrintSnapshot(SessionStatusSnapshot s)
    {
        Row("Session", $"{s.SessionId} ({s.Status}, state {s.State})");
        Row("Mode", s.Mode == SessionMode.Unlimited ? "unlimited" : $"timed {s.Duration:hh\\:mm\\:ss}");
        Row("Elapsed", s.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        if (s.Remaining is { } r)
        {
            Row("Remaining", r.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        }
        Row("Power", $"{s.PowerSource} · policy {s.PowerPolicy} · battery {s.BatteryLevelCurrent?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}% (start {s.BatteryLevelAtStart?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}%)");
        Row("Sleep blocked", s.KeepAwakeHeld ? "yes" : "no");
        foreach (var m in s.ModifiedPowerSettings)
        {
            var o = s.OriginalPowerSettings.FirstOrDefault(x => x.Name == m.Name && x.Source == m.Source);
            Row($"  {m.Name} [{m.Source}]", $"{o?.Value.ToString(CultureInfo.InvariantCulture) ?? "?"} -> {m.Value}");
        }
        foreach (var a in s.Applications)
        {
            Row($"  App {a.Application.Name}", a.Running switch { true => "running", false => "exited", null => "not watched" });
        }
        if (s.TerminationReason is { } t)
        {
            Row("Ended", $"{s.EndTime?.ToLocalTime():g} · {t}");
        }
        foreach (var w in s.Warnings)
        {
            Console.WriteLine($"  [warning] {w}");
        }
        return 0;
    }

    private static int PrintJson<T>(T value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(IpcJson.Options) { WriteIndented = true }));
        return 0;
    }

    private static void Row(string label, string value) => Console.WriteLine($"  {label,-28} {value}");

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"No process with PID {pid}.");
        }
    }
}

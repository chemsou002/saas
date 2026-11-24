using System.Globalization;
using Nightshift.Core.Session;

namespace Nightshift.Core.Verification;

/// <summary>
/// The Phase 1 test application: appends a UTC timestamp at a fixed interval, flushing every line.
/// If the system sleeps or the process is frozen, the log shows a gap — objective evidence, no guessing.
/// </summary>
public static class HeartbeatWriter
{
    public static async Task RunAsync(string path, TimeSpan interval, TimeProvider time, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(fs);
        long seq = 0;
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            await writer.WriteLineAsync(
                $"{time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)}\t{seq++}\t{Environment.ProcessId}").ConfigureAwait(false);
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        while (await WaitAsync(timer, ct).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed record HeartbeatGap(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Length => To - From;
}

public enum IntervalVerdict
{
    /// <summary>The heartbeat kept running throughout the closed-lid interval.</summary>
    Pass,

    /// <summary>A gap longer than allowed occurred while the lid was closed.</summary>
    Fail,

    /// <summary>The heartbeat log does not cover the interval.</summary>
    NotCovered,
}

public sealed record LidInterval(DateTimeOffset Closed, DateTimeOffset? Opened, IntervalVerdict Verdict, TimeSpan LongestGap);

public sealed record VerificationReport(
    int Samples,
    DateTimeOffset? First,
    DateTimeOffset? Last,
    TimeSpan MaxAllowedGap,
    IReadOnlyList<HeartbeatGap> Gaps,
    IReadOnlyList<LidInterval> LidIntervals,
    int SuspendEvents,
    int RestoreFailures,
    bool RestoreCompleted)
{
    /// <summary>
    /// Overall PoC result: PASS needs at least one closed-lid interval, all intervals passing, no suspend,
    /// and a completed restore (when a session log is supplied).
    /// </summary>
    public string Overall =>
        Samples == 0 ? "INCONCLUSIVE (no heartbeat samples)"
        : LidIntervals.Count == 0 ? "INCONCLUSIVE (the lid was never closed during the logged session)"
        : LidIntervals.Any(i => i.Verdict == IntervalVerdict.Fail) || SuspendEvents > 0 ? "FAIL"
        : LidIntervals.Any(i => i.Verdict == IntervalVerdict.NotCovered) ? "INCONCLUSIVE (heartbeat did not cover every closed-lid interval)"
        : !RestoreCompleted || RestoreFailures > 0 ? "FAIL (settings were not fully restored)"
        : "PASS";
}

public static class HeartbeatAnalyzer
{
    public static IReadOnlyList<DateTimeOffset> ReadSamples(string path)
    {
        var list = new List<DateTimeOffset>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            var first = line.Split('\t', 2)[0];
            if (DateTimeOffset.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
            {
                list.Add(t);
            }
        }
        list.Sort();
        return list;
    }

    public static VerificationReport Analyze(
        IReadOnlyList<DateTimeOffset> samples,
        IEnumerable<SessionLogEntry> sessionLog,
        TimeSpan maxAllowedGap)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(sessionLog);
        var gaps = new List<HeartbeatGap>();
        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i] - samples[i - 1] > maxAllowedGap)
            {
                gaps.Add(new HeartbeatGap(samples[i - 1], samples[i]));
            }
        }

        var entries = sessionLog.ToList();
        var intervals = new List<LidInterval>();
        DateTimeOffset? closedAt = null;
        var sessionEnd = entries.Count > 0 ? entries[^1].Time : (DateTimeOffset?)null;
        foreach (var e in entries.Where(e => e.Type == "power"))
        {
            if (e.Event == "LidClosed" && closedAt is null)
            {
                closedAt = e.Time;
            }
            else if (e.Event == "LidOpened" && closedAt is { } c)
            {
                intervals.Add(Evaluate(c, e.Time, e.Time));
                closedAt = null;
            }
        }
        if (closedAt is { } open)
        {
            intervals.Add(Evaluate(open, null, sessionEnd ?? open));
        }

        var suspends = entries.Count(e => e.Event is "UnexpectedSuspend");
        var restoreFailures = entries.Count(e => e.Event == "SettingRestored" && e.Type == "error");
        var restoreCompleted = entries.Count == 0 || entries.Any(e => e.Type == "state" && e.State == nameof(SessionState.Idle) && e.Message.StartsWith("Restoring", StringComparison.Ordinal));

        return new VerificationReport(samples.Count, samples.Count > 0 ? samples[0] : null, samples.Count > 0 ? samples[^1] : null,
            maxAllowedGap, gaps, intervals, suspends, restoreFailures, restoreCompleted);

        LidInterval Evaluate(DateTimeOffset from, DateTimeOffset? opened, DateTimeOffset to)
        {
            if (samples.Count == 0 || samples[0] > from + maxAllowedGap || samples[^1] < to - maxAllowedGap)
            {
                return new LidInterval(from, opened, IntervalVerdict.NotCovered, TimeSpan.Zero);
            }
            // Longest distance between consecutive heartbeats overlapping [from, to].
            var longest = TimeSpan.Zero;
            for (var i = 1; i < samples.Count; i++)
            {
                if (samples[i] < from || samples[i - 1] > to)
                {
                    continue;
                }
                var g = samples[i] - samples[i - 1];
                if (g > longest)
                {
                    longest = g;
                }
            }
            return new LidInterval(from, opened, longest > maxAllowedGap ? IntervalVerdict.Fail : IntervalVerdict.Pass, longest);
        }
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Nightshift.Core.Util;

/// <summary>Parses session durations such as "15m", "2h", "1h30m", "90s" or "unlimited".</summary>
public static partial class DurationParser
{
    /// <returns>The duration, or null for an unlimited session.</returns>
    public static TimeSpan? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim().ToLowerInvariant();
        if (t is "unlimited" or "infinite" or "none")
        {
            return null;
        }

        var match = DurationRegex().Match(t);
        if (!match.Success || match.Length == 0)
        {
            throw new FormatException($"Invalid duration '{text}'. Use e.g. 15m, 2h, 1h30m, 90s or 'unlimited'.");
        }

        static int Part(Group g) => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
        var result = new TimeSpan(Part(match.Groups["h"]), Part(match.Groups["m"]), Part(match.Groups["s"]));
        if (result <= TimeSpan.Zero)
        {
            throw new FormatException("Duration must be greater than zero.");
        }
        if (result > TimeSpan.FromDays(7))
        {
            throw new FormatException("Timed sessions are limited to 7 days; use 'unlimited' instead.");
        }
        return result;
    }

    public static string Format(TimeSpan? duration) =>
        duration is null ? "unlimited" : duration.Value.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture).TrimStart('0', '.');

    [GeneratedRegex(@"^(?:(?<h>\d{1,4})h)?(?:(?<m>\d{1,5})m)?(?:(?<s>\d{1,6})s)?$")]
    private static partial Regex DurationRegex();
}

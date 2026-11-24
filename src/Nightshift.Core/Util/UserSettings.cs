using System.Text.Json;

namespace Nightshift.Core.Util;

public sealed record UserSettings
{
    public bool VentilationAcknowledged { get; init; }
    public DateTimeOffset? VentilationAcknowledgedAt { get; init; }

    public static UserSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new UserSettings();
        }
        try
        {
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new UserSettings();
        }
        catch (JsonException)
        {
            return new UserSettings(); // corrupt settings must never weaken safety: fall back to "not acknowledged"
        }
    }

    public void Save(string path) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

public static class SafetyText
{
    public const string VentilationWarning =
        "IMPORTANT: Your laptop will continue operating while its lid is closed. Place your device on a hard, flat, " +
        "well-ventilated surface. Never place it on a bed, pillow, blanket, carpet, or any surface that blocks its " +
        "ventilation openings. Avoid enclosed spaces. Excessive heat may damage your device.";

    public const string VentilationAcknowledgement = "I understand and will ensure proper ventilation.";
}

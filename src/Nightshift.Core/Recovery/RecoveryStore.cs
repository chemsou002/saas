using System.Text.Json;
using System.Text.Json.Serialization;
using Nightshift.Core.Util;

namespace Nightshift.Core.Recovery;

/// <summary>Stores the recovery record on disk using atomic writes.</summary>
public sealed class RecoveryStore(string path)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    public bool Exists => File.Exists(Path);

    public RecoveryRecord? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }
        var json = File.ReadAllText(Path);
        var record = JsonSerializer.Deserialize<RecoveryRecord>(json, JsonOptions)
            ?? throw new InvalidDataException($"Recovery record '{Path}' is empty.");
        if (record.FormatVersion > RecoveryRecord.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Recovery record was written by a newer NIGHTSHIFT (format {record.FormatVersion}). Refusing to guess.");
        }
        return record;
    }

    public void Save(RecoveryRecord record) =>
        AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(record, JsonOptions));

    public void Delete()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}

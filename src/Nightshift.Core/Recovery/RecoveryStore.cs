using System.Text.Json;
using System.Text.Json.Serialization;
using Nightshift.Core.Util;

namespace Nightshift.Core.Recovery;

/// <summary>Durable storage of the single active recovery record.</summary>
public interface IRecoveryStore
{
    bool Exists { get; }

    RecoveryRecord? Load();

    /// <summary>Must be durable when it returns: the caller writes system settings right after.</summary>
    void Save(RecoveryRecord record);

    void Delete();
}

/// <summary>Stores the recovery record in a JSON file using atomic writes (Phase 1 CLI).</summary>
public sealed class RecoveryStore(string path) : IRecoveryStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nightshift.Core.Session;

/// <summary>One line of the session log (JSON Lines). Contains no application content — only system/session facts.</summary>
public sealed record SessionLogEntry(
    DateTimeOffset Time,
    string Type,
    string Message,
    string? State = null,
    string? Event = null);

/// <summary>Append-only JSONL log, flushed per entry so it survives crashes and power loss as far as possible.</summary>
public sealed class SessionEventLog : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _gate = new();
    private readonly StreamWriter? _writer;
    private readonly TimeProvider _time;

    public SessionEventLog(string? path, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Path = path;
        if (path is not null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
        }
    }

    public string? Path { get; }

    /// <summary>Raised for every entry (used by the console front-end).</summary>
    public event Action<SessionLogEntry>? EntryWritten;

    public void Write(string type, string message, string? state = null, string? evt = null)
    {
        var entry = new SessionLogEntry(_time.GetUtcNow(), type, message, state, evt);
        lock (_gate)
        {
            if (_writer is not null)
            {
                _writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                _writer.Flush();
            }
        }
        EntryWritten?.Invoke(entry);
    }

    public static IEnumerable<SessionLogEntry> Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            SessionLogEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<SessionLogEntry>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // a torn final line after power loss
            }
            if (entry is not null)
            {
                yield return entry;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
        }
    }
}

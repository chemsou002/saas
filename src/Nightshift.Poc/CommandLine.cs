namespace Nightshift.Poc;

/// <summary>Minimal "--name value" / "--flag" parser (keeps the PoC dependency-free).</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public CommandLine(string[] args, IReadOnlySet<string> flagNames)
    {
        Command = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].ToLowerInvariant() : "help";
        for (var i = Command == "help" && (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) ? 0 : 1; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{a}'.");
            }
            var name = a[2..];
            if (flagNames.Contains(name))
            {
                _flags.Add(name);
                continue;
            }
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{a}' needs a value.");
            }
            if (!_values.TryGetValue(name, out var list))
            {
                _values[name] = list = [];
            }
            list.Add(args[++i]);
        }
    }

    public string Command { get; }

    public bool Flag(string name)
    {
        _used.Add(name);
        return _flags.Contains(name);
    }

    public string? Value(string name)
    {
        _used.Add(name);
        return _values.TryGetValue(name, out var v) ? v[^1] : null;
    }

    public IReadOnlyList<string> Values(string name)
    {
        _used.Add(name);
        return _values.TryGetValue(name, out var v) ? v : [];
    }

    /// <summary>Rejects options that the command did not read, so typos never silently change behaviour.</summary>
    public void EnsureAllUsed()
    {
        var unknown = _values.Keys.Concat(_flags).Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown option(s) for '{Command}': {string.Join(", ", unknown.Select(u => "--" + u))}");
        }
    }
}

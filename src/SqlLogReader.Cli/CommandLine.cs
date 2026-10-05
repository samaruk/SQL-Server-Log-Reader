namespace SqlLogReader.Cli;

/// <summary>Minimal "command --option value --flag" parser.</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-S"] = "--server",
        ["-d"] = "--database",
        ["-U"] = "--user",
        ["-P"] = "--password",
        ["-o"] = "--output",
        ["-f"] = "--format",
        ["-t"] = "--table",
        ["-h"] = "--help",
        ["-?"] = "--help",
    };

    /// <summary>Options that never take a value.</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--help", "--include-rolled-back", "--include-inactive", "--no-online-log", "--no-current-data",
        "--encrypt", "--no-trust-cert", "--all-transactions", "--integrated", "--raw", "--vlfs", "--transactions",
    };

    public string Command { get; private set; } = "";
    public List<string> Positional { get; } = new();

    public static CommandLine Parse(string[] args)
    {
        var cl = new CommandLine();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (Aliases.TryGetValue(arg, out var alias)) arg = alias;
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                string name = arg;
                string? value = null;
                int eq = arg.IndexOf('=');
                if (eq > 0)
                {
                    name = arg[..eq];
                    value = arg[(eq + 1)..];
                }
                else if (!Flags.Contains(name))
                {
                    if (i + 1 >= args.Length) throw new ArgumentException($"Option {name} needs a value.");
                    value = args[++i];
                }
                if (!cl._options.TryGetValue(name, out var list))
                    cl._options[name] = list = new List<string>();
                list.Add(value ?? "true");
            }
            else if (cl.Command.Length == 0)
            {
                cl.Command = arg.ToLowerInvariant();
            }
            else
            {
                cl.Positional.Add(arg);
            }
        }
        return cl;
    }

    public bool Has(string name)
    {
        _used.Add(name);
        return _options.ContainsKey(name);
    }

    public string? Get(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var list) ? list[^1] : null;
    }

    /// <summary>All values of a repeatable option; comma-separated values are split.</summary>
    public List<string> GetAll(string name, bool splitCommas = true)
    {
        _used.Add(name);
        if (!_options.TryGetValue(name, out var list)) return new List<string>();
        return splitCommas
            ? list.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
            : list.ToList();
    }

    public int GetInt(string name, int defaultValue)
    {
        var v = Get(name);
        if (v == null) return defaultValue;
        return int.TryParse(v, out var n) ? n : throw new ArgumentException($"Option {name} expects a number.");
    }

    /// <summary>Options given on the command line that the command did not ask for.</summary>
    public IEnumerable<string> Unused => _options.Keys.Where(k => !_used.Contains(k));
}

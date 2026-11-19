using System.Globalization;

namespace SqlToPg.Cli;

/// <summary>Thrown for a command line that cannot be acted on; exits 64.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>
/// <c>--name value</c> and <c>--flag</c> arguments. Every option a command reads is marked as used,
/// and <see cref="RejectUnknown"/> refuses whatever is left, so a mistyped <c>--dry-rn</c> is an error
/// rather than a real run.
/// </summary>
public sealed class ArgBag
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public ArgBag(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--", StringComparison.Ordinal) || a.Length < 3)
            {
                throw new UsageException($"unexpected argument '{a}'");
            }

            var eq = a.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                _values[a[2..eq]] = a[(eq + 1)..];
            }
            else if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                _values[a[2..]] = list[++i];
            }
            else
            {
                _values[a[2..]] = null;
            }
        }
    }

    public bool Flag(string name)
    {
        _used.Add(name);
        if (!_values.TryGetValue(name, out var v))
        {
            return false;
        }

        return v is null || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public string? Text(string name)
    {
        _used.Add(name);
        if (!_values.TryGetValue(name, out var v))
        {
            return null;
        }

        return v ?? throw new UsageException($"--{name} needs a value");
    }

    public string Required(string name) => Text(name) ?? throw new UsageException($"--{name} is required");

    public int Int(string name, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        var t = Text(name);
        if (t is null)
        {
            return fallback;
        }

        if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
        {
            throw new UsageException($"--{name} must be a whole number between {min} and {max}");
        }

        return n;
    }

    public double? Number(string name)
    {
        var t = Text(name);
        if (t is null)
        {
            return null;
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? d
            : throw new UsageException($"--{name} must be a positive number");
    }

    public IReadOnlyList<string> List(string name) =>
        Text(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    public TEnum Choice<TEnum>(string name, TEnum fallback) where TEnum : struct, Enum
    {
        var t = Text(name);
        if (t is null)
        {
            return fallback;
        }

        return Enum.TryParse<TEnum>(t, ignoreCase: true, out var v) && Enum.IsDefined(v)
            ? v
            : throw new UsageException($"--{name} must be one of: {string.Join(", ", Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()))}");
    }

    public void RejectUnknown()
    {
        var unknown = _values.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new UsageException("unknown option(s): " + string.Join(", ", unknown.Select(u => "--" + u)));
        }
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace SqlToPg.Safety;

/// <summary>
/// Table and column names cannot be bound as parameters, so they reach SQL text by interpolation.
/// Every name goes through here first: it must look like an ordinary identifier, and it is always
/// emitted quoted, so neither dialect can read it as anything else.
/// </summary>
internal static partial class Ident
{
    /// <summary>Letters, digits, underscore, space, dollar and hyphen; must not start with a digit or space. 128 characters at most.</summary>
    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{Nd}_$ \-]{0,127}$")]
    private static partial Regex Acceptable();

    public static string Check(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Acceptable().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an identifier this tool will place in SQL text.");
        }

        return name;
    }

    /// <summary>"name" for PostgreSQL.</summary>
    public static string Pg(string name) => "\"" + Check(name) + "\"";

    /// <summary>[name] for SQL Server.</summary>
    public static string Ms(string name) => "[" + Check(name) + "]";

    /// <summary>A schema-qualified PostgreSQL name: "schema"."table".</summary>
    public static string Pg(QualifiedName name) => Pg(name.Schema) + "." + Pg(name.Name);

    /// <summary>A schema-qualified SQL Server name: [schema].[table].</summary>
    public static string Ms(QualifiedName name) => Ms(name.Schema) + "." + Ms(name.Name);

    /// <summary>
    /// A name for a scratch object derived from <paramref name="parts"/> that fits PostgreSQL's 63-byte
    /// limit. A name that would overflow is cut and ends with a short hash of the whole, because the
    /// server would otherwise truncate it silently and two long names could meet in one object.
    /// </summary>
    public static string Bounded(string prefix, string body, int limit = 63)
    {
        var room = limit - prefix.Length;
        if (Encoding.UTF8.GetByteCount(body) <= room)
        {
            return prefix + body;
        }

        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..8];
        var keep = body;
        while (Encoding.UTF8.GetByteCount(keep) > room - 9)
        {
            keep = keep[..^1];
        }

        return prefix + keep + "_" + digest;
    }
}

/// <summary>A schema and an object name, kept apart so each is validated and quoted on its own.</summary>
public readonly record struct QualifiedName(string Schema, string Name)
{
    /// <summary>Parses "schema.table" or "table" (which takes <paramref name="defaultSchema"/>).</summary>
    public static QualifiedName Parse(string text, string defaultSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();
        var dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        var name = dot < 0
            ? new QualifiedName(defaultSchema, Strip(trimmed))
            : new QualifiedName(Strip(trimmed[..dot]), Strip(trimmed[(dot + 1)..]));
        Ident.Check(name.Schema);
        Ident.Check(name.Name);
        return name;
    }

    private static string Strip(string part) => part.Trim().Trim('[', ']', '"');

    public override string ToString() => Schema + "." + Name;
}

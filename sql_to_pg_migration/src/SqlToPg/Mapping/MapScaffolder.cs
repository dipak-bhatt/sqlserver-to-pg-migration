using System.Text;
using SqlToPg.Safety;
using SqlToPg.Schema;

namespace SqlToPg.Mapping;

/// <summary>
/// Drafts a mapping file by pairing the two catalogues by name. Names are compared in a folded form —
/// lower case, letters and digits only — so <c>dbo.EmployeeAddress.EmployeeID</c> finds
/// <c>public.employee_address.employee_id</c>. Everything it could not pair is written into the
/// draft for a person to decide: source tables without a target appear disabled, and each table lists
/// the columns left over on either side.
/// </summary>
public static class MapScaffolder
{
    public sealed record Options(string SourceSchema = "dbo", string TargetSchema = "public", string? StripSourcePrefix = null, string? AddTargetPrefix = null);

    public static MapDocument Draft(MssqlSchemaSnapshot source, PgSchemaSnapshot target, Options options)
    {
        var targets = target.Tables
            .Where(t => t.Name.Schema == options.TargetSchema)
            .GroupBy(t => Fold(t.Name.Name))
            .ToDictionary(g => g.Key, g => g.First());

        var doc = new MapDocument { SourceSchema = options.SourceSchema, TargetSchema = options.TargetSchema };
        foreach (var ms in source.Tables
            .Where(t => string.Equals(t.Name.Schema, options.SourceSchema, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name.Name, StringComparer.OrdinalIgnoreCase))
        {
            var wanted = ms.Name.Name;
            if (options.StripSourcePrefix is { Length: > 0 } strip && wanted.StartsWith(strip, StringComparison.OrdinalIgnoreCase))
            {
                wanted = wanted[strip.Length..];
            }

            wanted = (options.AddTargetPrefix ?? "") + wanted;
            if (!targets.TryGetValue(Fold(wanted), out var pg))
            {
                doc.Tables.Add(new TableDoc
                {
                    Source = ms.Name.ToString(),
                    Target = $"{options.TargetSchema}.{Snake(wanted)}",
                    Enabled = false,
                    Columns = [.. ms.Columns.Where(c => !c.Computed).Select(c => new ColumnDoc { Source = c.Name, Target = Snake(c.Name) })],
                    UnmappedTarget = ["(no target table matched; create it or fix the name, then enable)"],
                });
                continue;
            }

            doc.Tables.Add(PairColumns(ms, pg));
        }

        return doc;
    }

    private static TableDoc PairColumns(MsTable ms, PgTable pg)
    {
        var byFold = pg.Columns.Where(c => !c.Generated).GroupBy(c => Fold(c.Name)).ToDictionary(g => g.Key, g => g.First());
        var used = new HashSet<string>(StringComparer.Ordinal);
        var table = new TableDoc { Source = ms.Name.ToString(), Target = pg.Name.ToString(), Columns = [] };
        var leftover = new List<string>();
        foreach (var col in ms.Columns)
        {
            if (byFold.TryGetValue(Fold(col.Name), out var match) && used.Add(match.Name))
            {
                table.Columns.Add(new ColumnDoc
                {
                    Source = col.Name,
                    Target = match.Name,
                    TrimEnd = col.Type.StartsWith("char", StringComparison.OrdinalIgnoreCase) || col.Type.StartsWith("nchar", StringComparison.OrdinalIgnoreCase) ? true : default,
                });
            }
            else
            {
                leftover.Add($"{col.Name} {col.Type}");
            }
        }

        var unfed = pg.Columns.Where(c => !c.Generated && !used.Contains(c.Name)).Select(c => $"{c.Name} {c.Type}{(c.NotNull && !c.HasDefault ? " NOT NULL" : "")}").ToList();
        table.UnmappedSource = leftover.Count > 0 ? leftover : null;
        table.UnmappedTarget = unfed.Count > 0 ? unfed : null;
        return table;
    }

    /// <summary>Lower case, letters and digits only.</summary>
    internal static string Fold(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    /// <summary><c>EmployeeID</c> → <c>employee_id</c>, <c>HTTPLog</c> → <c>http_log</c>.</summary>
    internal static string Snake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (!char.IsLetterOrDigit(ch))
            {
                if (sb.Length > 0 && sb[^1] != '_')
                {
                    sb.Append('_');
                }

                continue;
            }

            if (char.IsUpper(ch) && i > 0 && sb.Length > 0 && sb[^1] != '_')
            {
                var prev = name[i - 1];
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextIsLower))
                {
                    sb.Append('_');
                }
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return Ident.Check(sb.ToString().Trim('_'));
    }
}

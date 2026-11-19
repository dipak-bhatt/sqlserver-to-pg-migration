using SqlToPg.Schema;
using SqlToPg.Transfer;

namespace SqlToPg.Mapping;

public enum Gravity
{
    /// <summary>For information.</summary>
    Note,

    /// <summary>Printed on every run; does not stop it.</summary>
    Warn,

    /// <summary>The run will not start.</summary>
    Block,
}

public sealed record Finding(Gravity Gravity, string Where, string Message)
{
    public override string ToString() => $"{Gravity switch { Gravity.Block => "BLOCK", Gravity.Warn => "warn ", _ => "note " }} {Where}: {Message}";
}

/// <summary>
/// The mapping held up against both catalogues before anything is written. A mistake found here costs
/// an edit of the mapping file; found mid-load it costs the whole run. The structural checks need no
/// data; <see cref="WithDataAsync"/> adds the ones that need to look at the source rows (nulls headed
/// for a <c>NOT NULL</c> column, rows belonging to another scope).
/// </summary>
public static class MapAudit
{
    public static List<Finding> Structural(TransferPlan plan, MssqlSchemaSnapshot source, PgSchemaSnapshot target)
    {
        var found = new List<Finding>();
        foreach (var spec in plan.Tables)
        {
            var at = spec.Target.ToString();
            var pg = target.Table(spec.Target);
            var ms = source.Table(spec.Source);
            if (pg is null)
            {
                found.Add(new(Gravity.Block, at, "the target table does not exist"));
                continue;
            }

            if (ms is null)
            {
                found.Add(new(Gravity.Block, at, $"the source table {spec.Source} does not exist"));
                continue;
            }

            foreach (var field in spec.Fields)
            {
                var where = $"{at}.{field.Target}";
                var col = pg.Column(field.Target);
                if (col is null)
                {
                    found.Add(new(Gravity.Block, where, "no such target column"));
                    continue;
                }

                if (col.Generated)
                {
                    found.Add(new(Gravity.Block, where, "is a generated column and cannot be written; remove it from the mapping"));
                }

                if (!CellCoercion.IsSupported(col.Type))
                {
                    found.Add(new(Gravity.Block, where, $"target type '{col.Type}' is not supported by the loader; map it through a holding view or change the column"));
                    continue;
                }

                if (field.Feed == FeedKind.Column)
                {
                    var src = ms.Column(field.SourceColumn!);
                    if (src is null)
                    {
                        found.Add(new(Gravity.Block, where, $"source column {spec.Source}.{field.SourceColumn} does not exist"));
                        continue;
                    }

                    if (TypeClash(src.Type, col.Type) is { } clash)
                    {
                        found.Add(new(clash.Gravity, where, clash.Message));
                    }
                }

                if (field.Feed == FeedKind.Literal)
                {
                    try
                    {
                        CellCoercion.For(col.Type).Apply(field.Literal);
                    }
                    catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or InvalidOperationException)
                    {
                        found.Add(new(Gravity.Block, where, $"literal '{field.Literal}' cannot be read as {col.Type}"));
                    }
                }
            }

            // A NOT NULL column with no default that nothing feeds fails on the first row.
            var fed = spec.Fields.Select(f => f.Target).ToHashSet(StringComparer.Ordinal);
            foreach (var col in pg.Columns.Where(c => c.NotNull && !c.HasDefault && !c.Generated && !fed.Contains(c.Name)))
            {
                found.Add(new(Gravity.Block, $"{at}.{col.Name}", "is NOT NULL with no default, and the mapping does not feed it"));
            }

            if (spec.IsScoped && spec.ScopeSourceColumn is not null && ms.Column(spec.ScopeSourceColumn) is null)
            {
                found.Add(new(Gravity.Block, at, $"scope source column {spec.Source}.{spec.ScopeSourceColumn} does not exist"));
            }

            if (spec.OnConflict == ConflictRule.Update && (spec.Key ?? pg.PrimaryKey).Count == 0)
            {
                found.Add(new(Gravity.Block, at, "onConflict 'update' needs a key and the target has no primary key; set \"key\""));
            }

            foreach (var parent in target.Links.ParentsOf(spec.Target).Where(p => plan.ByTarget(p) is null && p != spec.Target))
            {
                found.Add(new(Gravity.Note, at, $"references {parent}, which this mapping does not load; it must already hold the parent rows"));
            }
        }

        return found;
    }

    /// <summary>The checks that read source rows: nulls headed for NOT NULL columns, and the scope census.</summary>
    public static async Task<List<Finding>> WithDataAsync(
        TransferPlan plan, MssqlSchemaSnapshot source, PgSchemaSnapshot target, MssqlReader reader, ScopeFilter scope, bool strictScope, CancellationToken ct)
    {
        var found = new List<Finding>();
        foreach (var spec in plan.Tables)
        {
            var pg = target.Table(spec.Target);
            var ms = source.Table(spec.Source);
            if (pg is null || ms is null)
            {
                continue;
            }

            foreach (var field in spec.Fields.Where(f => f.Feed is FeedKind.Column or FeedKind.Expression))
            {
                var col = pg.Column(field.Target);
                var sourceNullable = field.Feed == FeedKind.Expression || ms.Column(field.SourceColumn!)?.Nullable == true;
                if (col is null || !col.NotNull || !sourceNullable)
                {
                    continue;
                }

                var (count, samples) = await reader.NullsAsync(spec, field, ms.PrimaryKey, scope, ct);
                found.Add(count == 0
                    ? new(Gravity.Note, $"{spec.Target}.{field.Target}", "source allows NULL, target is NOT NULL; no NULL present today")
                    : new(Gravity.Block, $"{spec.Target}.{field.Target}",
                        $"{count:N0} source row(s) are NULL but the target is NOT NULL{(samples.Count > 0 ? "; first: " + string.Join(" ", samples) : "")}"));
            }

            if (spec.IsScoped && spec.ScopeSourceColumn is not null && scope.Value is not null)
            {
                var census = await reader.CensusAsync(spec, scope, ct);
                if (census.Foreign > 0)
                {
                    found.Add(new(strictScope ? Gravity.Block : Gravity.Note, spec.Target.ToString(),
                        $"{census.Foreign:N0} source row(s) belong to another scope{(strictScope ? " (--strict-scope)" : " and are not moved")}"));
                }

                if (census.Blank > 0)
                {
                    found.Add(new(scope.ClaimBlank ? Gravity.Note : Gravity.Warn, spec.Target.ToString(), scope.ClaimBlank
                        ? $"{census.Blank:N0} row(s) with an empty scope will be stamped with '{scope.Value}' (--claim-blank)"
                        : $"{census.Blank:N0} row(s) with an empty scope will be left behind; pass --claim-blank to take them"));
                }
            }
        }

        return found;
    }

    /// <summary>A source/target type pairing worth a word, or null when the pairing is ordinary.</summary>
    internal static (Gravity Gravity, string Message)? TypeClash(string msType, string pgType)
    {
        var ms = Family(CellCoercion.Bare(msType));
        var pg = CellCoercion.WireTypeOf(pgType).ToString();
        var bare = CellCoercion.Bare(pgType);

        if (ms == "datetime" && bare is "timestamp with time zone" or "timestamptz")
        {
            return (Gravity.Note, $"naive {msType} into {pgType}: values are read in sourceTimeZone (or the column's timeZone)");
        }

        if (ms == "binary" && pg != "Bytea")
        {
            return (Gravity.Block, $"{msType} cannot become {pgType}; use bytea or an expression");
        }

        if (ms == "text" && pg is "Integer" or "Bigint" or "Smallint" or "Numeric" or "Uuid" or "Boolean" or "Date" or "Timestamp" or "TimestampTz")
        {
            return (Gravity.Warn, $"{msType} parsed as {pgType}; any value that does not parse fails the table");
        }

        if (ms is "integer" or "decimal" && pg is "Uuid" or "Bytea" or "Date" or "Timestamp" or "TimestampTz")
        {
            return (Gravity.Block, $"{msType} cannot become {pgType}");
        }

        if (msType.StartsWith("bigint", StringComparison.OrdinalIgnoreCase) && pg is "Integer" or "Smallint")
        {
            return (Gravity.Warn, $"{msType} narrowed to {pgType}; a value out of range fails the table");
        }

        if (TextLength(msType) is { } srcLen && TextLength(pgType) is { } dstLen && srcLen > dstLen)
        {
            return (Gravity.Warn, $"{msType} is wider than {pgType}; a longer value fails the table");
        }

        return null;
    }

    private static string Family(string t) => t switch
    {
        "tinyint" or "smallint" or "int" or "bigint" => "integer",
        "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "decimal",
        "datetime" or "datetime2" or "smalldatetime" => "datetime",
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "binary",
        "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" => "text",
        _ => t,
    };

    private static int? TextLength(string type)
    {
        var open = type.IndexOf('(', StringComparison.Ordinal);
        var close = type.IndexOf(')', StringComparison.Ordinal);
        if (open < 0 || close < open || !Family(CellCoercion.Bare(type)).Equals("text", StringComparison.Ordinal) && !type.Contains("char", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(type[(open + 1)..close], out var n) ? n : null;
    }
}

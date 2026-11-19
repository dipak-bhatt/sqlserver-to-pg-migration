using SqlToPg.Safety;

namespace SqlToPg.Mapping;

/// <summary>
/// Turns the file into a <see cref="TransferPlan"/>, refusing anything ambiguous up front: a target
/// column fed twice, a field with no feed or two, a name that is not an identifier, the same target
/// table mapped twice. These are the mistakes that otherwise surface mid-load, hours in.
/// </summary>
public static class MapResolver
{
    public static TransferPlan Resolve(MapDocument doc, IReadOnlyCollection<string>? onlyTables = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var problems = new List<string>();
        var specs = new List<TableSpec>();
        var seenTargets = new HashSet<QualifiedName>();
        var filter = onlyTables is { Count: > 0 } ? new HashSet<string>(onlyTables, StringComparer.OrdinalIgnoreCase) : null;

        foreach (var (table, index) in doc.Tables.Select((t, i) => (t, i)))
        {
            if (!table.Enabled)
            {
                continue;
            }

            var where = $"tables[{index}]";
            try
            {
                var spec = ResolveTable(doc, table, problems, where);
                if (spec is null)
                {
                    continue;
                }

                if (!seenTargets.Add(spec.Target))
                {
                    problems.Add($"{where}: target {spec.Target} is mapped more than once");
                    continue;
                }

                if (filter is null || filter.Contains(spec.Target.ToString()) || filter.Contains(spec.Target.Name)
                    || filter.Contains(spec.Source.ToString()) || filter.Contains(spec.Source.Name))
                {
                    specs.Add(spec);
                }
            }
            catch (ArgumentException ex)
            {
                problems.Add($"{where}: {ex.Message}");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException("the mapping has problems:\n  - " + string.Join("\n  - ", problems));
        }

        if (filter is not null && specs.Count == 0)
        {
            throw new InvalidDataException("--tables matched no enabled table in the mapping");
        }

        return new TransferPlan(specs, string.IsNullOrWhiteSpace(doc.SourceTimeZone) ? "UTC" : doc.SourceTimeZone!);
    }

    private static TableSpec? ResolveTable(MapDocument doc, TableDoc table, List<string> problems, string where)
    {
        var source = QualifiedName.Parse(table.Source, doc.SourceSchema);
        var target = QualifiedName.Parse(table.Target, doc.TargetSchema);
        var scope = table.NoScope ? null : table.Scope ?? doc.Scope;
        string? scopeTarget = scope is null ? null : Ident.Check(scope.Target);
        string? scopeSource = scope?.Source is { Length: > 0 } s ? Ident.Check(s) : null;

        if (table.Columns.Count == 0)
        {
            problems.Add($"{where} ({target}): no columns are mapped");
            return null;
        }

        var fields = new List<FieldSpec>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (column, i) in table.Columns.Select((c, i) => (c, i)))
        {
            var at = $"{where}.columns[{i}] ({target}.{column.Target})";
            Ident.Check(column.Target);
            if (!targets.Add(column.Target))
            {
                problems.Add($"{at}: the target column is fed more than once");
                continue;
            }

            if (scopeTarget is not null && column.Target == scopeTarget)
            {
                problems.Add($"{at}: this is the scope column; it is filled from --scope and must not be mapped");
                continue;
            }

            var feeds = (column.Source is not null ? 1 : 0) + (column.Expression is not null ? 1 : 0) + (column.Literal is not null ? 1 : 0);
            if (feeds != 1)
            {
                problems.Add($"{at}: give exactly one of source, expression or literal");
                continue;
            }

            var kind = column.Source is not null ? FeedKind.Column : column.Expression is not null ? FeedKind.Expression : FeedKind.Literal;
            if (kind == FeedKind.Column)
            {
                Ident.Check(column.Source!);
            }

            if (kind == FeedKind.Expression && column.Expression!.Contains(';', StringComparison.Ordinal))
            {
                problems.Add($"{at}: an expression may not contain ';'");
                continue;
            }

            fields.Add(new FieldSpec(column.Target, kind, column.Source, column.Expression, column.Literal,
                column.TimeZone, column.TrimEnd, column.StripNul));
        }

        if (scopeTarget is not null)
        {
            fields.Add(new FieldSpec(scopeTarget, FeedKind.Scope, null, null, null, null, false, false));
        }

        if (table.Where is { } filter && filter.Contains(';', StringComparison.Ordinal))
        {
            problems.Add($"{where} ({target}): 'where' may not contain ';'");
        }

        if (table.OnConflict != ConflictRule.Fail && table.Role == TableRole.Shared)
        {
            problems.Add($"{where} ({target}): a shared table is loaded only when empty; onConflict does not apply");
        }

        return new TableSpec(source, target, table.Role, table.Where, scopeSource, scopeTarget,
            table.Key is { Count: > 0 } k ? [.. k.Select(Ident.Check)] : null, table.OnConflict, fields);
    }
}

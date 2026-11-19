using SqlToPg.Safety;

namespace SqlToPg.Mapping;

/// <summary>Where a target column's value comes from.</summary>
public enum FeedKind
{
    Column,
    Expression,
    Literal,

    /// <summary>The run's scope value (<c>--scope</c>), stamped into every row.</summary>
    Scope,
}

/// <summary>One target column and what feeds it, validated.</summary>
public sealed record FieldSpec(
    string Target,
    FeedKind Feed,
    string? SourceColumn,
    string? Expression,
    string? Literal,
    string? TimeZone,
    bool TrimEnd,
    bool StripNul)
{
    /// <summary>The SQL Server select-list item for this field, or null when it is not read from the source.</summary>
    public string? SelectItem => Feed switch
    {
        FeedKind.Column => "src." + Ident.Ms(SourceColumn!),
        FeedKind.Expression => "(" + Expression + ")",
        _ => null,
    };
}

/// <summary>One source table to one target table, validated.</summary>
public sealed record TableSpec(
    QualifiedName Source,
    QualifiedName Target,
    TableRole Role,
    string? Where,
    string? ScopeSourceColumn,
    string? ScopeTargetColumn,
    IReadOnlyList<string>? Key,
    ConflictRule OnConflict,
    IReadOnlyList<FieldSpec> Fields)
{
    public bool IsScoped => ScopeTargetColumn is not null;

    /// <summary>The fields read from SQL Server, in select order.</summary>
    public IEnumerable<FieldSpec> SourcedFields => Fields.Where(f => f.SelectItem is not null);

    public string Label => $"{Source} -> {Target}";
}

/// <summary>Everything a run will move, in mapping-file order (the run re-orders by foreign keys).</summary>
public sealed record TransferPlan(IReadOnlyList<TableSpec> Tables, string DefaultTimeZone)
{
    public TableSpec? ByTarget(QualifiedName target) => Tables.FirstOrDefault(t => t.Target == target);
}

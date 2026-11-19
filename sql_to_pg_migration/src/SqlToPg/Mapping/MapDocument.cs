using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlToPg.Mapping;

/// <summary>
/// The mapping file as written on disk (JSON). It says which SQL Server table feeds which PostgreSQL
/// table and which source column (or expression) feeds each target column. Everything the tool does
/// is driven from here; <see cref="MapResolver"/> turns it into the validated <see cref="TransferPlan"/>.
/// </summary>
public sealed class MapDocument
{
    /// <summary>Schema assumed for a source table written without one.</summary>
    public string SourceSchema { get; set; } = "dbo";

    /// <summary>Schema assumed for a target table written without one.</summary>
    public string TargetSchema { get; set; } = "public";

    /// <summary>
    /// What a naive SQL Server <c>datetime</c> meant when it lands in a <c>timestamptz</c> column
    /// (a Windows or IANA zone id). <c>UTC</c> when omitted. A column may override it.
    /// </summary>
    public string? SourceTimeZone { get; set; }

    /// <summary>Applied to every table that does not name its own scope column.</summary>
    public ScopeDoc? Scope { get; set; }

    public List<TableDoc> Tables { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static MapDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"mapping file not found: {path}");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public static MapDocument Parse(string json, string origin = "<inline>")
    {
        try
        {
            return JsonSerializer.Deserialize<MapDocument>(json, Options)
                ?? throw new InvalidDataException($"{origin}: the mapping is empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{origin}: not a valid mapping ({ex.Message})", ex);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>
/// A multi-tenant (or any partitioned) load: only the rows of one scope value move, and the target
/// keeps its rows for other values untouched.
/// </summary>
public sealed class ScopeDoc
{
    /// <summary>The target column that holds the scope value (e.g. <c>tenant_id</c>).</summary>
    public string Target { get; set; } = "tenant_id";

    /// <summary>
    /// The source column holding the scope value. Omit it when the source database itself is the
    /// scope (one database per tenant): every row is taken and stamped with the run's value.
    /// </summary>
    public string? Source { get; set; }
}

public sealed class TableDoc
{
    /// <summary>e.g. <c>dbo.Employee</c>.</summary>
    public string Source { get; set; } = "";

    /// <summary>e.g. <c>public.employee</c>.</summary>
    public string Target { get; set; } = "";

    /// <summary>A disabled table stays in the file (for review) but is never touched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <c>owned</c> (default): cleared then reloaded by a <c>replace</c> run.
    /// <c>shared</c>: reference data loaded only while the target is empty, never cleared.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TableRole Role { get; set; } = TableRole.Owned;

    /// <summary>An optional SQL Server predicate over the source table (aliased <c>src</c>).</summary>
    public string? Where { get; set; }

    /// <summary>Overrides the document's scope; <c>"scope": null</c> inherits it, <see cref="NoScope"/> opts out.</summary>
    public ScopeDoc? Scope { get; set; }

    /// <summary>True for a table that has no scope column even though the document declares one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NoScope { get; set; }

    /// <summary>Target key columns, used to name a bad row and for <c>upsert</c>. Read from the target's primary key when omitted.</summary>
    public List<string>? Key { get; set; }

    /// <summary>How a row whose key already exists is treated when writing through a holding table.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ConflictRule OnConflict { get; set; } = ConflictRule.Fail;

    public List<ColumnDoc> Columns { get; set; } = [];

    /// <summary>Informational only: source columns no target column takes (written by <c>scaffold</c>).</summary>
    public List<string>? UnmappedSource { get; set; }

    /// <summary>Informational only: target columns no source column feeds (written by <c>scaffold</c>).</summary>
    public List<string>? UnmappedTarget { get; set; }
}

public sealed class ColumnDoc
{
    /// <summary>The target column.</summary>
    public string Target { get; set; } = "";

    /// <summary>The source column. Exactly one of this, <see cref="Expression"/> or <see cref="Literal"/>.</summary>
    public string? Source { get; set; }

    /// <summary>A SQL Server expression over the source row (aliased <c>src</c>), e.g. <c>UPPER(src.[Code])</c>.</summary>
    public string? Expression { get; set; }

    /// <summary>A fixed value for every row, given as text and converted to the target type.</summary>
    public string? Literal { get; set; }

    /// <summary>Overrides <see cref="MapDocument.SourceTimeZone"/> for this column.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Trim trailing blanks (fixed-width <c>char</c>/<c>nchar</c> sources).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TrimEnd { get; set; }

    /// <summary>Text: replace a NUL character with nothing instead of failing the table.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StripNul { get; set; }
}

public enum TableRole
{
    Owned,
    Shared,
}

public enum ConflictRule
{
    /// <summary>A duplicate key fails the table.</summary>
    Fail,

    /// <summary><c>ON CONFLICT DO NOTHING</c>.</summary>
    Skip,

    /// <summary><c>ON CONFLICT (key) DO UPDATE</c> every mapped column.</summary>
    Update,
}

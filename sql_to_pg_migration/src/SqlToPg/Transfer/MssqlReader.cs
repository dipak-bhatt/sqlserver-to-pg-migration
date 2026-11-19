using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using SqlToPg.Mapping;
using SqlToPg.Safety;
using Microsoft.Data.SqlClient;

namespace SqlToPg.Transfer;

/// <summary>
/// Which source rows belong to a scoped run.
/// </summary>
/// <param name="Value">The run's scope value (e.g. a tenant id); null for an unscoped run.</param>
/// <param name="ClaimBlank">Also take rows whose scope column is NULL, blank, or the all-zero GUID — and stamp them with <see cref="Value"/>.</param>
public sealed record ScopeFilter(string? Value, bool ClaimBlank)
{
    public static readonly ScopeFilter None = new(null, false);

    public const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
}

/// <summary>How a scoped source table's rows split: this scope's, unclaimed, and somebody else's.</summary>
public sealed record ScopeCensus(long Mine, long Blank, long Foreign)
{
    public static readonly ScopeCensus Empty = new(0, 0, 0);
}

/// <summary>
/// One SQL Server connection reading mapped tables. A reader serves one statement at a time, so the
/// scheduler gives every worker its own.
/// </summary>
public sealed class MssqlReader(string connectionString) : IAsyncDisposable
{
    private SqlConnection? _conn;

    public string Database => new SqlConnectionStringBuilder(connectionString).InitialCatalog;

    public async Task OpenAsync(CancellationToken ct)
    {
        _conn = new SqlConnection(connectionString);
        await _conn.OpenAsync(ct);
    }

    private SqlConnection Conn => _conn ?? throw new InvalidOperationException("OpenAsync has not been called");

    /// <summary>Rows the run would read from <paramref name="spec"/>.</summary>
    public async Task<long> CountAsync(TableSpec spec, ScopeFilter scope, CancellationToken ct)
    {
        await using var cmd = Command($"SELECT COUNT_BIG(*) FROM {Ident.Ms(spec.Source)} AS src{WhereClause(spec, scope)}", scope);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>One scan sorting every row of a scoped table into <see cref="ScopeCensus"/>'s three buckets.</summary>
    public async Task<ScopeCensus> CensusAsync(TableSpec spec, ScopeFilter scope, CancellationToken ct)
    {
        if (spec.ScopeSourceColumn is null || scope.Value is null)
        {
            return ScopeCensus.Empty;
        }

        var mine = MinePredicate(spec);
        var blank = BlankPredicate(spec);
        var userFilter = spec.Where is null ? "" : $" WHERE ({spec.Where})";
        await using var cmd = Command(
            $"SELECT COUNT_BIG(CASE WHEN {mine} THEN 1 END), " +
            $"COUNT_BIG(CASE WHEN {blank} THEN 1 END), " +
            $"COUNT_BIG(CASE WHEN NOT ({mine}) AND NOT ({blank}) THEN 1 END) " +
            $"FROM {Ident.Ms(spec.Source)} AS src{userFilter}", scope);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new ScopeCensus(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }

    /// <summary>Rows the run would read whose <paramref name="field"/> is NULL, with up to 10 of their keys.</summary>
    public async Task<(long Count, IReadOnlyList<string> Samples)> NullsAsync(
        TableSpec spec, FieldSpec field, IReadOnlyList<string> sourceKey, ScopeFilter scope, CancellationToken ct)
    {
        var isNull = $"{field.SelectItem} IS NULL";
        var where = WhereClause(spec, scope);
        where = where.Length == 0 ? $" WHERE {isNull}" : $"{where} AND {isNull}";
        long count;
        await using (var cmd = Command($"SELECT COUNT_BIG(*) FROM {Ident.Ms(spec.Source)} AS src{where}", scope))
        {
            count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        if (count == 0 || sourceKey.Count == 0)
        {
            return (count, []);
        }

        var keyList = string.Join(", ", sourceKey.Select(k => "src." + Ident.Ms(k)));
        var samples = new List<string>();
        await using (var cmd = Command($"SELECT TOP (10) {keyList} FROM {Ident.Ms(spec.Source)} AS src{where} ORDER BY {keyList}", scope))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                samples.Add("(" + string.Join(", ", sourceKey.Select((k, i) => $"{k}={r.GetValue(i)}")) + ")");
            }
        }

        return (count, samples);
    }

    /// <summary>
    /// Streams the select list of <paramref name="spec"/> (its <see cref="TableSpec.SourcedFields"/>,
    /// in order). Values are read synchronously on purpose: the writer runs this enumerator on a task of
    /// its own, and a per-cell async state machine costs more than the occasional blocking packet read.
    /// </summary>
    public async IAsyncEnumerable<object?[]> ReadAsync(TableSpec spec, ScopeFilter scope, [EnumeratorCancellation] CancellationToken ct)
    {
        var items = spec.SourcedFields.Select(f => f.SelectItem!).ToList();
        if (items.Count == 0)
        {
            items.Add("1"); // a table fed only by literals and the scope still has one row per source row
        }

        await using var cmd = Command(SelectSql(spec, scope, items), scope);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var width = spec.SourcedFields.Count();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[width];
            for (var i = 0; i < width; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            yield return row;
        }
    }

    internal static string SelectSql(TableSpec spec, ScopeFilter scope, IReadOnlyList<string> items) =>
        $"SELECT {string.Join(", ", items)} FROM {Ident.Ms(spec.Source)} AS src{WhereClause(spec, scope)}";

    /// <summary>The mapping's own filter and, for a scoped run, the scope predicate.</summary>
    internal static string WhereClause(TableSpec spec, ScopeFilter scope)
    {
        var parts = new List<string>();
        if (spec.Where is { Length: > 0 } w)
        {
            parts.Add($"({w})");
        }

        if (spec.ScopeSourceColumn is not null && scope.Value is not null)
        {
            parts.Add(scope.ClaimBlank ? $"(({MinePredicate(spec)}) OR ({BlankPredicate(spec)}))" : $"({MinePredicate(spec)})");
        }

        return parts.Count == 0 ? "" : " WHERE " + string.Join(" AND ", parts);
    }

    /// <summary>
    /// Compared in the column's own type (the nvarchar parameter converts to a uniqueidentifier or int
    /// column), so an index on the scope column is still usable.
    /// </summary>
    private static string MinePredicate(TableSpec spec) => $"src.{Ident.Ms(spec.ScopeSourceColumn!)} = @scope";

    private static string BlankPredicate(TableSpec spec)
    {
        var text = $"LTRIM(RTRIM(CONVERT(nvarchar(64), src.{Ident.Ms(spec.ScopeSourceColumn!)})))";
        return $"src.{Ident.Ms(spec.ScopeSourceColumn!)} IS NULL OR {text} IN (N'', N'{ScopeFilter.ZeroGuid}')";
    }

    private SqlCommand Command(string sql, ScopeFilter scope)
    {
        var cmd = new SqlCommand(sql, Conn) { CommandTimeout = 0 };
        if (sql.Contains("@scope", StringComparison.Ordinal))
        {
            cmd.Parameters.Add("@scope", SqlDbType.NVarChar, 200).Value = (object?)scope.Value ?? DBNull.Value;
        }

        return cmd;
    }

    /// <summary>
    /// <c>ALTER DATABASE … SET READ_ONLY | READ_WRITE WITH ROLLBACK IMMEDIATE</c>, issued from master.
    /// Freezing ends every other session in the database — this reader's own among them — so the pool
    /// is cleared and the connection reopened before returning.
    /// </summary>
    public async Task SetReadOnlyAsync(bool readOnly, CancellationToken ct)
    {
        var database = Database;
        if (string.IsNullOrEmpty(database))
        {
            throw new InvalidOperationException("the source connection string names no database (Initial Catalog); cannot freeze it");
        }

        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (var conn = new SqlConnection(master))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                $"ALTER DATABASE {Ident.Ms(database)} SET {(readOnly ? "READ_ONLY" : "READ_WRITE")} WITH ROLLBACK IMMEDIATE", conn)
            { CommandTimeout = 300 };
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (_conn is not null)
        {
            SqlConnection.ClearPool(_conn);
            await _conn.DisposeAsync();
            _conn = null;
            await OpenAsync(ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is not null)
        {
            await _conn.DisposeAsync();
        }
    }

    internal static string Describe(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        var sb = new StringBuilder();
        sb.Append(b.DataSource).Append('/').Append(b.InitialCatalog);
        if (b.TrustServerCertificate)
        {
            sb.Append(" (certificate not validated)");
        }

        return sb.ToString();
    }
}

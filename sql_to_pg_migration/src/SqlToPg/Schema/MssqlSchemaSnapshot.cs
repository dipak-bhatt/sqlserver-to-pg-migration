using SqlToPg.Safety;
using Microsoft.Data.SqlClient;

namespace SqlToPg.Schema;

/// <summary>A source column as SQL Server declares it.</summary>
public sealed record MsColumn(string Name, string Type, bool Nullable, bool Computed);

public sealed record MsTable(QualifiedName Name, IReadOnlyList<MsColumn> Columns, IReadOnlyList<string> PrimaryKey, long ApproxRows)
{
    public MsColumn? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The source database read once from the <c>sys</c> catalogue: tables, columns, primary keys and the
/// approximate row count (<c>sys.partitions</c> — no scan, no <c>VIEW DATABASE STATE</c>).
/// SQL Server names are matched case-insensitively, as the server itself does under a default collation.
/// </summary>
public sealed class MssqlSchemaSnapshot
{
    private readonly Dictionary<string, MsTable> _tables;

    internal MssqlSchemaSnapshot(IEnumerable<MsTable> tables) =>
        _tables = tables.ToDictionary(t => t.Name.ToString(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<MsTable> Tables => _tables.Values;

    public MsTable? Table(QualifiedName name) => _tables.GetValueOrDefault(name.ToString());

    public static async Task<MssqlSchemaSnapshot> CaptureAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var columns = new Dictionary<string, List<MsColumn>>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, QualifiedName>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(
            """
            SELECT s.name, t.name, c.name,
                   ty.name + CASE
                       WHEN ty.name IN ('varchar','char','varbinary','binary') THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                       WHEN ty.name IN ('nvarchar','nchar') THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                       WHEN ty.name IN ('decimal','numeric') THEN '(' + CAST(c.precision AS varchar(5)) + ',' + CAST(c.scale AS varchar(5)) + ')'
                       ELSE '' END,
                   c.is_nullable, c.is_computed
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty  ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var name = new QualifiedName(r.GetString(0), r.GetString(1));
                var key = name.ToString();
                if (!columns.TryGetValue(key, out var list))
                {
                    columns[key] = list = [];
                    names[key] = name;
                }

                list.Add(new MsColumn(r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5)));
            }
        }

        var keys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(
            """
            SELECT s.name, t.name, c.name
            FROM sys.indexes i
            JOIN sys.tables t         ON t.object_id = i.object_id
            JOIN sys.schemas s        ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 1
            ORDER BY s.name, t.name, ic.key_ordinal
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var key = r.GetString(0) + "." + r.GetString(1);
                if (!keys.TryGetValue(key, out var list))
                {
                    keys[key] = list = [];
                }

                list.Add(r.GetString(2));
            }
        }

        var rows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(
            """
            SELECT s.name, t.name, SUM(p.rows)
            FROM sys.tables t
            JOIN sys.schemas s    ON s.schema_id = t.schema_id
            JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
            GROUP BY s.name, t.name
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                rows[r.GetString(0) + "." + r.GetString(1)] = r.GetInt64(2);
            }
        }

        return new MssqlSchemaSnapshot(columns.Select(kv => new MsTable(
            names[kv.Key], kv.Value,
            keys.TryGetValue(kv.Key, out var pk) ? pk : [],
            rows.GetValueOrDefault(kv.Key))));
    }
}

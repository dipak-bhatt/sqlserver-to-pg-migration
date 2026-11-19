using SqlToPg.Safety;
using SqlToPg.Transfer;
using Npgsql;
using NpgsqlTypes;

namespace SqlToPg.Schema;

/// <summary>A target column as PostgreSQL actually declares it.</summary>
public sealed record PgColumn(string Name, string Type, bool NotNull, bool HasDefault, bool Generated)
{
    /// <summary>The binary COPY type; taken from here, never from the mapping, because COPY is strict about width.</summary>
    public NpgsqlDbType WireType => CellCoercion.WireTypeOf(Type);
}

/// <summary>A target table: its columns in ordinal order and its primary key.</summary>
public sealed record PgTable(QualifiedName Name, IReadOnlyList<PgColumn> Columns, IReadOnlyList<string> PrimaryKey)
{
    public PgColumn? Column(string name) => Columns.FirstOrDefault(c => c.Name == name);
}

/// <summary>
/// The target database read once: every user table's columns and keys, and the foreign keys between
/// them. Both the audit (does the mapping fit?) and the writer (which wire type?) read from this one
/// snapshot, so they cannot disagree.
/// </summary>
public sealed class PgSchemaSnapshot
{
    private readonly Dictionary<QualifiedName, PgTable> _tables;

    internal PgSchemaSnapshot(Dictionary<QualifiedName, PgTable> tables, ForeignKeyGraph links)
    {
        _tables = tables;
        Links = links;
    }

    public ForeignKeyGraph Links { get; }

    public IReadOnlyCollection<PgTable> Tables => _tables.Values;

    public PgTable? Table(QualifiedName name) => _tables.GetValueOrDefault(name);

    public static async Task<PgSchemaSnapshot> CaptureAsync(NpgsqlDataSource target, CancellationToken ct)
    {
        await using var conn = await target.OpenConnectionAsync(ct);
        var columns = new Dictionary<QualifiedName, List<PgColumn>>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT n.nspname, c.relname, a.attname, format_type(a.atttypid, a.atttypmod),
                   a.attnotnull, a.atthasdef, a.attgenerated <> ''
            FROM pg_attribute a
            JOIN pg_class c     ON c.oid = a.attrelid AND c.relkind IN ('r', 'p')
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE a.attnum > 0 AND NOT a.attisdropped
              AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'
              AND NOT c.relispartition
            ORDER BY n.nspname, c.relname, a.attnum
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var key = new QualifiedName(r.GetString(0), r.GetString(1));
                if (!columns.TryGetValue(key, out var list))
                {
                    columns[key] = list = [];
                }

                list.Add(new PgColumn(r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetBoolean(6)));
            }
        }

        var keys = new Dictionary<QualifiedName, List<string>>();
        var edges = new List<(QualifiedName Child, QualifiedName Parent)>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT cn.nspname, c.relname, k.contype::text,
                   pn.nspname, p.relname,
                   ARRAY(SELECT a.attname FROM unnest(k.conkey) WITH ORDINALITY u(n, o)
                         JOIN pg_attribute a ON a.attrelid = k.conrelid AND a.attnum = u.n ORDER BY u.o)
            FROM pg_constraint k
            JOIN pg_class c      ON c.oid = k.conrelid
            JOIN pg_namespace cn ON cn.oid = c.relnamespace
            LEFT JOIN pg_class p      ON p.oid = k.confrelid
            LEFT JOIN pg_namespace pn ON pn.oid = p.relnamespace
            WHERE k.contype IN ('p', 'f')
              AND cn.nspname NOT IN ('pg_catalog', 'information_schema')
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var table = new QualifiedName(r.GetString(0), r.GetString(1));
                if (r.GetString(2) == "p")
                {
                    keys[table] = [.. r.GetFieldValue<string[]>(5)];
                }
                else
                {
                    edges.Add((table, new QualifiedName(r.GetString(3), r.GetString(4))));
                }
            }
        }

        var tables = columns.ToDictionary(
            kv => kv.Key,
            kv => new PgTable(kv.Key, kv.Value, keys.TryGetValue(kv.Key, out var pk) ? pk : []));
        return new PgSchemaSnapshot(tables, new ForeignKeyGraph(edges));
    }

    /// <summary>Builds a snapshot from literal rows, for tests.</summary>
    internal static PgSchemaSnapshot FromTables(IEnumerable<PgTable> tables, IEnumerable<(QualifiedName Child, QualifiedName Parent)>? edges = null) =>
        new(tables.ToDictionary(t => t.Name), new ForeignKeyGraph(edges ?? []));
}

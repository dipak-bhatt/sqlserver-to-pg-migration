using SqlToPg.Safety;
using Npgsql;

namespace SqlToPg.Transfer;

/// <summary>
/// Ids are carried over from SQL Server, so every sequence behind a loaded <c>serial</c> or identity
/// column must be moved past the largest id now in its table — or the application's first insert
/// collides with a migrated row. The value is only ever raised (<c>GREATEST</c>): other scopes may
/// share the sequence, and lowering it would hand out their ids again. Then <c>ANALYZE</c>, because a
/// freshly loaded table has no statistics and the first queries would be planned blind.
/// </summary>
public sealed class IdentityRealigner(NpgsqlDataSource target)
{
    public sealed record Moved(string Sequence, QualifiedName Table, string Column, long From, long To);

    public async Task<IReadOnlyList<Moved>> RealignAsync(IReadOnlyCollection<QualifiedName> tables, CancellationToken ct)
    {
        if (tables.Count == 0)
        {
            return [];
        }

        await using var conn = await target.OpenConnectionAsync(ct);
        var owned = new List<(string Seq, long Oid, QualifiedName Table, string Column)>();
        await using (var find = new NpgsqlCommand(
            """
            SELECT quote_ident(sn.nspname) || '.' || quote_ident(s.relname), s.oid::bigint, tn.nspname, t.relname, a.attname
            FROM pg_depend d
            JOIN pg_class s      ON s.oid = d.objid AND s.relkind = 'S'
            JOIN pg_namespace sn ON sn.oid = s.relnamespace
            JOIN pg_class t      ON t.oid = d.refobjid
            JOIN pg_namespace tn ON tn.oid = t.relnamespace
            JOIN pg_attribute a  ON a.attrelid = t.oid AND a.attnum = d.refobjsubid
            WHERE d.classid = 'pg_class'::regclass AND d.deptype IN ('a', 'i')
              AND (tn.nspname || '.' || t.relname) = ANY($1)
            ORDER BY 1
            """, conn))
        {
            find.Parameters.AddWithValue(tables.Select(t => t.ToString()).ToArray());
            await using var r = await find.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                owned.Add((r.GetString(0), r.GetInt64(1), new QualifiedName(r.GetString(2), r.GetString(3)), r.GetString(4)));
            }
        }

        var moved = new List<Moved>();
        foreach (var (seq, oid, table, column) in owned)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);

            // Two runs realigning the same sequence must not interleave read-max and setval.
            await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", conn, tx))
            {
                lk.Parameters.AddWithValue(oid);
                await lk.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = new NpgsqlCommand(
                $"""
                 WITH m AS (SELECT max({Ident.Pg(column)})::bigint AS top FROM {Ident.Pg(table)})
                 SELECT s.last_value,
                        CASE WHEN m.top IS NULL OR m.top <= s.last_value THEN s.last_value
                             ELSE setval($1::regclass, m.top, true) END
                 FROM {seq} s, m
                 """, conn, tx)
            { CommandTimeout = 0 };
            cmd.Parameters.AddWithValue(seq);
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (await r.ReadAsync(ct) && r.GetInt64(0) != r.GetInt64(1))
                {
                    moved.Add(new Moved(seq, table, column, r.GetInt64(0), r.GetInt64(1)));
                }
            }

            await tx.CommitAsync(ct);
        }

        return moved;
    }

    public async Task AnalyzeAsync(IReadOnlyCollection<QualifiedName> tables, int lanes, CancellationToken ct) =>
        await Parallel.ForEachAsync(tables, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, lanes), CancellationToken = ct }, async (table, token) =>
        {
            await using var conn = await target.OpenConnectionAsync(token);
            await using var cmd = new NpgsqlCommand($"ANALYZE {Ident.Pg(table)}", conn) { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync(token);
        });
}

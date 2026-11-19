using SqlToPg.Mapping;
using SqlToPg.Safety;
using SqlToPg.Schema;
using Npgsql;

namespace SqlToPg.Transfer;

/// <summary>One table's source and target counts.</summary>
public sealed record CountMatch(QualifiedName Target, long Source, long Destination, bool Agrees, string? Note);

/// <summary>
/// After a load: the rows the run would read from SQL Server against the rows the target now holds
/// for the same scope. A <c>replace</c> run must agree exactly; an <c>append</c> run only needs the
/// target to hold at least as many; a shared table is reported but judged by neither.
/// </summary>
public sealed class CountReconciler(NpgsqlDataSource target, PgSchemaSnapshot schema)
{
    public async Task<IReadOnlyList<CountMatch>> CompareAsync(
        MssqlReader reader, IReadOnlyList<TableSpec> tables, ScopeFilter scope, bool appendMode, CancellationToken ct)
    {
        var results = new List<CountMatch>();
        await using var conn = await target.OpenConnectionAsync(ct);
        foreach (var spec in tables)
        {
            var source = await reader.CountAsync(spec, scope, ct);
            var sql = $"SELECT count(*) FROM {Ident.Pg(spec.Target)}";
            await using var cmd = new NpgsqlCommand { Connection = conn, CommandTimeout = 0 };
            if (spec.IsScoped && scope.Value is not null)
            {
                var type = schema.Table(spec.Target)?.Column(spec.ScopeTargetColumn!)?.Type ?? "text";
                sql += $" WHERE {Ident.Pg(spec.ScopeTargetColumn!)} = CAST($1 AS {type})";
                cmd.Parameters.AddWithValue(scope.Value);
            }

            cmd.CommandText = sql;
            var destination = (long)(await cmd.ExecuteScalarAsync(ct))!;
            results.Add(spec.Role == TableRole.Shared
                ? new CountMatch(spec.Target, source, destination, true, "shared (not judged)")
                : appendMode
                    ? new CountMatch(spec.Target, source, destination, destination >= source, "append: target >= source")
                    : new CountMatch(spec.Target, source, destination, source == destination, null));
        }

        return results;
    }
}

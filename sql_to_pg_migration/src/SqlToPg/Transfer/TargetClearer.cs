using SqlToPg.Mapping;
using SqlToPg.Safety;
using SqlToPg.Schema;
using Npgsql;

namespace SqlToPg.Transfer;

/// <summary>
/// Empties what a <c>replace</c> run is about to reload, which is what makes a run repeatable: the
/// second run reaches the same state as the first. Shared tables are never cleared.
/// <list type="bullet">
/// <item>Unscoped: one <c>TRUNCATE</c> of every owned table together (so foreign keys among them are
/// no obstacle). A foreign key from a table outside the mapping makes PostgreSQL refuse it, by name,
/// rather than this tool silently cascading into data nobody mapped.</item>
/// <item>Scoped: <c>DELETE … WHERE scope = value</c>, children before parents, one transaction per
/// table (the knotted tables share one), with the scope setting bound so a row-level-security policy
/// itself prevents touching another scope's rows.</item>
/// </list>
/// </summary>
public sealed class TargetClearer(NpgsqlDataSource target, PgSchemaSnapshot schema, string? scopeSetting, TextWriter log)
{
    public async Task<IReadOnlyDictionary<QualifiedName, long>> ClearAsync(
        IReadOnlyList<TableSpec> tables, TableOrder order, string? scopeValue, CancellationToken ct)
    {
        var owned = tables.Where(t => t.Role == TableRole.Owned).ToDictionary(t => t.Target);
        var removed = new Dictionary<QualifiedName, long>();
        if (owned.Count == 0)
        {
            return removed;
        }

        await using var conn = await target.OpenConnectionAsync(ct);
        var scoped = owned.Values.Where(t => t.IsScoped).ToList();
        var unscoped = owned.Values.Where(t => !t.IsScoped).ToList();

        if (unscoped.Count > 0)
        {
            var list = string.Join(", ", unscoped.Select(t => Ident.Pg(t.Target)));
            await using var cmd = new NpgsqlCommand($"TRUNCATE TABLE {list}", conn) { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync(ct);
            foreach (var t in unscoped)
            {
                removed[t.Target] = -1; // TRUNCATE does not count
            }

            log.WriteLine($"  truncated {unscoped.Count} unscoped table(s)");
        }

        if (scoped.Count == 0)
        {
            return removed;
        }

        if (scopeValue is null)
        {
            throw new InvalidOperationException("scoped tables are mapped, but no --scope value was given");
        }

        var knotted = order.Knotted.ToHashSet();
        NpgsqlTransaction? knotTx = null;
        try
        {
            foreach (var name in order.Clear.Where(n => owned.TryGetValue(n, out var s) && s.IsScoped))
            {
                var spec = owned[name];
                var inKnot = knotted.Contains(name);
                var tx = inKnot ? knotTx ??= await StartAsync(conn, scopeValue, ct) : await StartAsync(conn, scopeValue, ct);
                var column = schema.Table(name)?.Column(spec.ScopeTargetColumn!)
                    ?? throw new InvalidOperationException($"{name}.{spec.ScopeTargetColumn} is missing on the target");
                await using var cmd = new NpgsqlCommand(
                    $"DELETE FROM {Ident.Pg(name)} WHERE {Ident.Pg(spec.ScopeTargetColumn!)} = CAST($1 AS {column.Type})", conn, tx)
                { CommandTimeout = 0 };
                cmd.Parameters.AddWithValue(scopeValue);
                removed[name] = await cmd.ExecuteNonQueryAsync(ct);
                if (!inKnot)
                {
                    await tx.CommitAsync(ct);
                    await tx.DisposeAsync();
                }

                if (removed[name] > 0)
                {
                    log.WriteLine($"  cleared {removed[name],12:N0}  {name}");
                }
            }

            if (knotTx is not null)
            {
                await knotTx.CommitAsync(ct);
            }
        }
        finally
        {
            if (knotTx is not null)
            {
                await knotTx.DisposeAsync();
            }
        }

        return removed;
    }

    private async Task<NpgsqlTransaction> StartAsync(NpgsqlConnection conn, string scopeValue, CancellationToken ct)
    {
        var tx = await conn.BeginTransactionAsync(ct);
        if (scopeSetting is not null)
        {
            await using var bind = new NpgsqlCommand("SELECT set_config($1, $2, true)", conn, tx);
            bind.Parameters.AddWithValue(scopeSetting);
            bind.Parameters.AddWithValue(scopeValue);
            await bind.ExecuteNonQueryAsync(ct);
        }

        return tx;
    }
}

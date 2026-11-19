using System.Diagnostics;
using System.Threading.Channels;
using SqlToPg.Mapping;
using SqlToPg.Safety;
using Npgsql;

namespace SqlToPg.Transfer;

/// <summary>How rows reach a target table.</summary>
public enum WriteStrategy
{
    /// <summary>Binary <c>COPY</c> straight into the target, one transaction per table. Fastest.</summary>
    Direct,

    /// <summary>
    /// Binary <c>COPY</c> into an unlogged holding table, then one <c>INSERT … SELECT</c> into the
    /// target. Needed when the target has row-level security (COPY refuses it), when an
    /// <c>onConflict</c> rule applies, or when <c>GENERATED ALWAYS</c> identities must keep their ids.
    /// </summary>
    Holding,
}

/// <summary>What happened to one table.</summary>
public sealed record TableOutcome(QualifiedName Target, long Rows, TimeSpan Elapsed, string? Note = null)
{
    public double RowsPerSecond => Elapsed.TotalSeconds > 0 ? Rows / Elapsed.TotalSeconds : 0;
}

/// <summary>Settings for every table a run writes.</summary>
/// <param name="ScopeSetting">
/// A PostgreSQL setting (e.g. <c>app.current_tenant</c>) bound transaction-locally to the scope value
/// before the final insert, so a row-level-security policy can check every row.
/// </param>
public sealed record WriterOptions(WriteStrategy Strategy, string HoldingSchema, string? ScopeSetting, string? ScopeValue);

/// <summary>
/// Moves one table. Reading and writing overlap: a dedicated task pulls rows from SQL Server and
/// shapes them, handing batches through a bounded channel to the COPY — so neither side waits on the
/// other, and memory stays bounded whatever the table's size. One transaction per table: a failed
/// table commits nothing, which is also what makes a retry safe.
/// </summary>
public sealed class PgTableWriter(NpgsqlDataSource target, WriterOptions options)
{
    internal const int RowsPerBatch = 500;
    internal const int BatchesInFlight = 32;

    /// <param name="targetKey">The target's primary key, used by <c>onConflict: update</c> when the mapping names no key.</param>
    public async Task<TableOutcome> WriteAsync(
        MssqlReader reader, TableSpec spec, RowShaper shaper, ScopeFilter scope, IReadOnlyList<string> targetKey, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        if (spec.Role == TableRole.Shared)
        {
            return await WriteSharedAsync(reader, spec, shaper, scope, clock, ct);
        }

        var rows = options.Strategy == WriteStrategy.Holding || spec.OnConflict != ConflictRule.Fail
            ? await WriteThroughHoldingAsync(reader, spec, shaper, scope, targetKey, ct)
            : await WriteDirectAsync(reader, spec, shaper, scope, ct);
        return new TableOutcome(spec.Target, rows, clock.Elapsed);
    }

    private async Task<long> WriteDirectAsync(MssqlReader reader, TableSpec spec, RowShaper shaper, ScopeFilter scope, CancellationToken ct)
    {
        await using var conn = await target.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await BindScopeAsync(conn, tx, ct);
        var rows = await PumpAsync(conn, Ident.Pg(spec.Target), spec, shaper, reader, scope, ct);
        await tx.CommitAsync(ct);
        return rows;
    }

    /// <summary>
    /// A reference table shared by every scope: filled from this source only while it is empty, under
    /// a lock, so two runs cannot both fill it; never overwritten once it holds rows.
    /// </summary>
    private async Task<TableOutcome> WriteSharedAsync(MssqlReader reader, TableSpec spec, RowShaper shaper, ScopeFilter scope, Stopwatch clock, CancellationToken ct)
    {
        await using var conn = await target.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, $"LOCK TABLE {Ident.Pg(spec.Target)} IN SHARE ROW EXCLUSIVE MODE", ct);
        await using (var probe = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {Ident.Pg(spec.Target)})", conn, tx))
        {
            if ((bool)(await probe.ExecuteScalarAsync(ct))!)
            {
                await tx.RollbackAsync(ct);
                return new TableOutcome(spec.Target, 0, clock.Elapsed, "shared table already filled; left as is");
            }
        }

        var rows = await PumpAsync(conn, Ident.Pg(spec.Target), spec, shaper, reader, scope, ct);
        await tx.CommitAsync(ct);
        return new TableOutcome(spec.Target, rows, clock.Elapsed, "shared table was empty; filled");
    }

    private async Task<long> WriteThroughHoldingAsync(
        MssqlReader reader, TableSpec spec, RowShaper shaper, ScopeFilter scope, IReadOnlyList<string> targetKey, CancellationToken ct)
    {
        var holding = HoldingName(spec.Target, options.ScopeValue);
        var columns = ColumnList(spec);
        await using var conn = await target.OpenConnectionAsync(ct);
        try
        {
            // Unlogged: its contents can always be re-read from the source, so not worth WAL.
            await Exec(conn, null, $"DROP TABLE IF EXISTS {holding}", ct);
            await Exec(conn, null, $"CREATE UNLOGGED TABLE {holding} AS SELECT {columns} FROM {Ident.Pg(spec.Target)} WITH NO DATA", ct);
            var rows = await PumpAsync(conn, holding, spec, shaper, reader, scope, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);
            await BindScopeAsync(conn, tx, ct);

            // OVERRIDING SYSTEM VALUE keeps the source's ids in GENERATED ALWAYS identity columns;
            // PostgreSQL accepts it on any table, so it is applied uniformly.
            await Exec(conn, tx, $"INSERT INTO {Ident.Pg(spec.Target)} ({columns}) OVERRIDING SYSTEM VALUE SELECT {columns} FROM {holding}{ConflictClause(spec, targetKey)}", ct);
            await tx.CommitAsync(ct);
            return rows;
        }
        finally
        {
            // Even on cancellation: the holding table may hold a scope's rows with no policy on it.
            await Exec(conn, null, $"DROP TABLE IF EXISTS {holding}", CancellationToken.None);
        }
    }

    /// <summary>The <c>ON CONFLICT</c> tail for the table's <see cref="ConflictRule"/>.</summary>
    internal static string ConflictClause(TableSpec spec, IReadOnlyList<string>? fallbackKey = null)
    {
        if (spec.OnConflict == ConflictRule.Fail)
        {
            return "";
        }

        if (spec.OnConflict == ConflictRule.Skip)
        {
            return " ON CONFLICT DO NOTHING";
        }

        var key = spec.Key ?? fallbackKey;
        if (key is null || key.Count == 0)
        {
            throw new InvalidOperationException($"{spec.Target}: onConflict 'update' needs a key (set \"key\" in the mapping)");
        }

        var updates = spec.Fields.Where(f => !key.Contains(f.Target)).Select(f => $"{Ident.Pg(f.Target)} = EXCLUDED.{Ident.Pg(f.Target)}").ToList();
        return updates.Count == 0
            ? $" ON CONFLICT ({string.Join(", ", key.Select(Ident.Pg))}) DO NOTHING"
            : $" ON CONFLICT ({string.Join(", ", key.Select(Ident.Pg))}) DO UPDATE SET {string.Join(", ", updates)}";
    }

    /// <summary>
    /// The binary import. A failed read leaves the importer uncompleted, which cancels the COPY; a
    /// failed COPY cancels the read. Either way nothing is committed.
    /// </summary>
    private static async Task<long> PumpAsync(
        NpgsqlConnection conn, string into, TableSpec spec, RowShaper shaper, MssqlReader reader, ScopeFilter scope, CancellationToken ct)
    {
        var wire = shaper.WireTypes;
        var lane = Channel.CreateBounded<object?[][]>(new BoundedChannelOptions(BatchesInFlight) { SingleReader = true, SingleWriter = true });
        using var halt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(() => ProduceAsync(lane.Writer, reader, spec, shaper, scope, halt.Token), CancellationToken.None);
        long written = 0;
        try
        {
            await using var importer = await conn.BeginBinaryImportAsync($"COPY {into} ({ColumnList(spec)}) FROM STDIN (FORMAT BINARY)", ct);
            await foreach (var batch in lane.Reader.ReadAllAsync(ct))
            {
                foreach (var row in batch)
                {
                    await importer.StartRowAsync(ct);
                    for (var i = 0; i < row.Length; i++)
                    {
                        if (row[i] is { } value)
                        {
                            await importer.WriteAsync(value, wire[i], ct);
                        }
                        else
                        {
                            await importer.WriteNullAsync(ct);
                        }
                    }
                }

                written += batch.Length;
            }

            await producer; // a read failure surfaces here, before the COPY is completed
            await importer.CompleteAsync(ct);
        }
        catch
        {
            await halt.CancelAsync();
            await producer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        return written;
    }

    private static async Task ProduceAsync(ChannelWriter<object?[][]> lane, MssqlReader reader, TableSpec spec, RowShaper shaper, ScopeFilter scope, CancellationToken ct)
    {
        try
        {
            var batch = new List<object?[]>(RowsPerBatch);
            long ordinal = 0;
            await foreach (var sourced in reader.ReadAsync(spec, scope, ct))
            {
                batch.Add(shaper.Shape(sourced, ++ordinal));
                if (batch.Count == RowsPerBatch)
                {
                    await lane.WriteAsync([.. batch], ct);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await lane.WriteAsync([.. batch], ct);
            }
        }
        finally
        {
            // Completed without the error: the consumer learns of a failure by awaiting this task.
            lane.TryComplete();
        }
    }

    private async Task BindScopeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        if (options.ScopeSetting is null || options.ScopeValue is null)
        {
            return;
        }

        await using var cmd = new NpgsqlCommand("SELECT set_config($1, $2, true)", conn, tx);
        cmd.Parameters.AddWithValue(options.ScopeSetting);
        cmd.Parameters.AddWithValue(options.ScopeValue);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// <c>"schema"."hold_&lt;scope8&gt;_&lt;table&gt;"</c>: the scope is part of the name so two scopes
    /// loading at once never share (or drop) each other's holding table.
    /// </summary>
    internal string HoldingName(QualifiedName table, string? scopeValue) =>
        Ident.Pg(options.HoldingSchema) + "." + Ident.Pg(Ident.Bounded(HoldingPrefix(scopeValue), table.Schema + "_" + table.Name));

    internal static string HoldingPrefix(string? scopeValue)
    {
        var tag = scopeValue is null ? "all" : new string([.. scopeValue.Where(char.IsLetterOrDigit).Take(8)]).ToLowerInvariant();
        return "hold_" + (tag.Length == 0 ? "x" : tag) + "_";
    }

    internal static string ColumnList(TableSpec spec) => string.Join(", ", spec.Fields.Select(f => Ident.Pg(f.Target)));

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Drops holding tables a killed run left behind for this scope.</summary>
    public static async Task<IReadOnlyList<string>> SweepHoldingAsync(NpgsqlDataSource target, string holdingSchema, string? scopeValue, CancellationToken ct)
    {
        await using var conn = await target.OpenConnectionAsync(ct);
        var names = new List<string>();
        await using (var find = new NpgsqlCommand(
            "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = $1 AND c.relkind = 'r' AND starts_with(c.relname, $2)", conn))
        {
            find.Parameters.AddWithValue(holdingSchema);
            find.Parameters.AddWithValue(HoldingPrefix(scopeValue));
            await using var r = await find.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                names.Add(r.GetString(0));
            }
        }

        foreach (var name in names)
        {
            await Exec(conn, null, $"DROP TABLE IF EXISTS {Ident.Pg(holdingSchema)}.{Ident.Pg(name)}", ct);
        }

        return names;
    }
}

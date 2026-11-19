using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using SqlToPg.Journal;
using SqlToPg.Mapping;
using SqlToPg.Safety;
using SqlToPg.Schema;

namespace SqlToPg.Transfer;

public enum LoadMode
{
    /// <summary>Clear the mapped rows (this scope's, or whole tables), then load. Repeatable.</summary>
    Replace,

    /// <summary>Load on top of what is there; pair with <c>onConflict</c> for re-runs.</summary>
    Append,
}

/// <summary>Everything the <c>transfer</c> command was asked to do.</summary>
public sealed record TransferSettings(
    string MapFile,
    IReadOnlyList<string> OnlyTables,
    ScopeFilter Scope,
    bool StrictScope,
    LoadMode Mode,
    WriteStrategy Strategy,
    int Lanes,
    bool DryRun,
    bool FreezeSource,
    TimeSpan? TimeBudget,
    double? RowsPerSecond,
    string? ScopeSetting,
    string JournalSchema,
    string? ConfirmTargetDb,
    bool SkipVerify);

/// <summary>
/// The whole run, in order:
/// <list type="number">
/// <item><b>Audit</b> the mapping against both catalogues and the source rows. Any BLOCK stops here.</item>
/// <item><b>Order</b> the tables by the target's foreign keys and <b>forecast</b> the duration; a forecast over the budget stops here.</item>
/// <item><b>Guard</b>: confirm the target database by name, take the run locks, open the journal, sweep dead holding tables.</item>
/// <item><b>Freeze</b> the source (optional): <c>READ_ONLY</c> until the run ends, whichever way it ends.</item>
/// <item><b>Clear</b> what a replace run reloads, children first.</item>
/// <item><b>Load</b>, <c>--lanes</c> tables at a time, each one transaction.</item>
/// <item><b>Realign</b> sequences past the loaded ids, and <c>ANALYZE</c>.</item>
/// <item><b>Reconcile</b> row counts against the source.</item>
/// </list>
/// Nothing before step 3 writes anything, which is what <c>--dry-run</c> stops after.
/// </summary>
public sealed class TransferRun(Endpoints endpoints, TransferSettings settings, TextWriter log)
{
    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var doc = MapDocument.Load(settings.MapFile);
        var plan = MapResolver.Resolve(doc, settings.OnlyTables);
        var scopeValue = settings.Scope.Value;
        if (plan.Tables.Any(t => t.IsScoped) && scopeValue is null)
        {
            throw new Cli.UsageException("the mapping declares a scope column; pass --scope <value> (and see --claim-blank)");
        }

        log.WriteLine($"source  {MssqlReader.Describe(endpoints.Source)}");
        log.WriteLine($"target  {endpoints.DescribeTarget()}");
        log.WriteLine($"mapping {settings.MapFile} — {plan.Tables.Count} table(s){(scopeValue is null ? "" : $", scope '{scopeValue}'")}, mode {settings.Mode.ToString().ToLowerInvariant()}, strategy {settings.Strategy.ToString().ToLowerInvariant()}");

        await using var pg = endpoints.OpenTarget(settings.Lanes);
        var sourceCs = endpoints.SourceForReaders();
        var msSchema = await MssqlSchemaSnapshot.CaptureAsync(sourceCs, ct);
        var pgSchema = await PgSchemaSnapshot.CaptureAsync(pg, ct);

        // 1. Audit.
        await using var probe = new MssqlReader(sourceCs);
        await probe.OpenAsync(ct);
        var findings = MapAudit.Structural(plan, msSchema, pgSchema);
        if (!findings.Any(f => f.Gravity == Gravity.Block))
        {
            findings.AddRange(await MapAudit.WithDataAsync(plan, msSchema, pgSchema, probe, settings.Scope, settings.StrictScope, ct));
        }

        Report(findings);
        if (findings.Any(f => f.Gravity == Gravity.Block))
        {
            log.WriteLine("refused: the mapping has blocking findings (above). Nothing was written.");
            return ExitCodes.Refused;
        }

        // 2. Order and forecast.
        var order = pgSchema.Links.Arrange(plan.Tables.Select(t => t.Target));
        if (order.Knotted.Count > 0)
        {
            log.WriteLine($"  foreign-key cycle: {string.Join(", ", order.Knotted)} — loaded last, one at a time");
        }

        var counts = new Dictionary<QualifiedName, long>();
        foreach (var spec in plan.Tables)
        {
            var approx = msSchema.Table(spec.Source)?.ApproxRows ?? 0;
            counts[spec.Target] = spec.Where is null && (!spec.IsScoped || spec.ScopeSourceColumn is null)
                ? approx
                : await probe.CountAsync(spec, settings.Scope, ct);
        }

        var journal = new RunJournal(pg, settings.JournalSchema);
        var (rate, rateFrom) = settings.RowsPerSecond is { } r
            ? (r, "--rows-per-second")
            : await journal.MeasuredRateAsync(ct) is { } measured
                ? (measured, "median of earlier runs")
                : (DurationForecast.FallbackRowsPerSecond, "default");
        var forecast = DurationForecast.Make(counts.Values, rate, rateFrom, settings.Lanes);
        log.WriteLine($"forecast {forecast}");

        using var budget = new TimeBudget(settings.TimeBudget);
        if (budget.Forbids(forecast))
        {
            log.WriteLine($"refused: the forecast is over --time-budget-minutes {budget.Limit!.Value.TotalMinutes:0}. Nothing was written.");
            return ExitCodes.Refused;
        }

        if (settings.DryRun)
        {
            log.WriteLine("load order:");
            foreach (var (t, i) in order.Fill.Select((t, i) => (t, i)))
            {
                log.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {i + 1,3}. {t,-50} {counts[t],14:N0} rows"));
            }

            log.WriteLine("dry run: nothing was written.");
            return ExitCodes.Ok;
        }

        // 3. Guard.
        await TargetConfirmation.RequireAsync(pg, settings.ConfirmTargetDb, ct);
        await using var guard = await ExclusiveRunGuard.TryTakeAsync(pg, scopeValue, ct);
        if (guard is null)
        {
            log.WriteLine($"refused: another run {(scopeValue is null ? "against this database" : $"for scope '{scopeValue}' or against the whole database")} is in progress.");
            return ExitCodes.Refused;
        }

        await journal.EnsureAsync(ct);
        var runId = await journal.OpenAsync(Endpoints.Operator, "transfer", scopeValue, Path.GetFileName(settings.MapFile),
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(settings.MapFile, ct))), settings.Mode.ToString().ToLowerInvariant(), ct);
        log.WriteLine($"journal run {runId} ({settings.JournalSchema})");
        foreach (var dead in await PgTableWriter.SweepHoldingAsync(pg, settings.JournalSchema, scopeValue, ct))
        {
            log.WriteLine($"  removed leftover holding table {dead}");
        }

        var frozen = false;
        var status = "failed";
        string? note = null;
        try
        {
            // 4. Freeze.
            if (settings.FreezeSource)
            {
                log.WriteLine($"freezing source database {probe.Database} (READ_ONLY)");
                await probe.SetReadOnlyAsync(true, ct);
                frozen = true;
            }

            budget.Start();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            var token = linked.Token;
            var clock = Stopwatch.StartNew();

            // 5. Clear.
            if (settings.Mode == LoadMode.Replace)
            {
                log.WriteLine("clearing");
                await new TargetClearer(pg, pgSchema, settings.ScopeSetting, log).ClearAsync(plan.Tables, order, scopeValue, token);
            }

            // 6. Load.
            log.WriteLine($"loading ({settings.Lanes} lane(s))");
            var writer = new PgTableWriter(pg, new WriterOptions(settings.Strategy, settings.JournalSchema, settings.ScopeSetting, scopeValue));
            var outcomes = new ConcurrentDictionary<QualifiedName, TableOutcome>();
            var scheduler = new WaveScheduler(settings.Lanes, async c =>
            {
                var reader = new MssqlReader(sourceCs);
                await reader.OpenAsync(c);
                return reader;
            }, log);

            await scheduler.RunAsync(order, pgSchema.Links.ParentsOf, async (reader, table, c) =>
            {
                var spec = plan.ByTarget(table)!;
                var pgTable = pgSchema.Table(table)!;
                await journal.TableStartedAsync(runId, table, spec.Source, counts[table], c);
                try
                {
                    var shaper = new RowShaper(spec, pgTable, plan.DefaultTimeZone, scopeValue);
                    var outcome = await writer.WriteAsync(reader, spec, shaper, settings.Scope, pgTable.PrimaryKey, c);
                    outcomes[table] = outcome;
                    await journal.TableFinishedAsync(runId, table, outcome.Note?.StartsWith("shared table already", StringComparison.Ordinal) == true ? "skipped" : "done", outcome.Rows, outcome.Note);
                    log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  ok {outcome.Rows,14:N0}  {table,-50} {outcome.Elapsed.TotalSeconds,8:0.0}s {outcome.RowsPerSecond,10:N0}/s{(outcome.Note is null ? "" : "  (" + outcome.Note + ")")}"));
                }
                catch (Exception ex)
                {
                    await journal.TableFinishedAsync(runId, table, "failed", null, ex.Message);
                    throw;
                }
            }, token);

            // 7. Realign and analyze.
            var touched = outcomes.Values.Where(o => o.Rows > 0).Select(o => o.Target).ToList();
            foreach (var m in await new IdentityRealigner(pg).RealignAsync(touched, token))
            {
                log.WriteLine($"  sequence {m.Sequence}: {m.From:N0} -> {m.To:N0}");
            }

            await new IdentityRealigner(pg).AnalyzeAsync(touched, settings.Lanes, token);
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"loaded {outcomes.Values.Sum(o => o.Rows):N0} row(s) into {outcomes.Count} table(s) in {clock.Elapsed:hh\\:mm\\:ss}"));

            // 8. Reconcile.
            if (!settings.SkipVerify)
            {
                var matches = await new CountReconciler(pg, pgSchema).CompareAsync(probe, plan.Tables, settings.Scope, settings.Mode == LoadMode.Append, token);
                var bad = PrintCounts(matches);
                if (bad > 0)
                {
                    note = $"{bad} table(s) disagree on row count";
                    log.WriteLine($"FAILED: {note}");
                    return ExitCodes.Failed;
                }
            }

            status = "succeeded";
            return ExitCodes.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || budget.Exhausted)
        {
            var reason = budget.Explain(ex);
            note = reason.Message;
            if (!ReferenceEquals(reason, ex))
            {
                throw reason;
            }

            throw;
        }
        finally
        {
            if (frozen)
            {
                try
                {
                    await probe.SetReadOnlyAsync(false, CancellationToken.None);
                    log.WriteLine($"source database {probe.Database} is READ_WRITE again");
                }
                catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
                {
                    log.WriteLine($"!!! SOURCE IS STILL READ-ONLY: {ex.Message}");
                    log.WriteLine("!!! run: sql_to_pg_migration unfreeze-source");
                    status = "failed";
                    note = (note is null ? "" : note + "; ") + "source left READ_ONLY";
                }
            }

            await journal.CloseAsync(runId, status, note, log);
        }
    }

    private void Report(IReadOnlyList<Finding> findings)
    {
        if (findings.Count == 0)
        {
            log.WriteLine("audit: mapping fits both databases");
            return;
        }

        log.WriteLine($"audit: {findings.Count(f => f.Gravity == Gravity.Block)} block, {findings.Count(f => f.Gravity == Gravity.Warn)} warn, {findings.Count(f => f.Gravity == Gravity.Note)} note");
        foreach (var f in findings.OrderByDescending(f => f.Gravity).ThenBy(f => f.Where, StringComparer.Ordinal))
        {
            log.WriteLine("  " + f);
        }
    }

    internal int PrintCounts(IReadOnlyList<CountMatch> matches)
    {
        log.WriteLine("reconcile (row counts):");
        foreach (var m in matches)
        {
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {(m.Agrees ? "ok  " : "DIFF")} {m.Target,-50} source {m.Source,14:N0}  target {m.Destination,14:N0}{(m.Note is null ? "" : "  " + m.Note)}"));
        }

        return matches.Count(m => !m.Agrees);
    }
}

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Refused = 2;
    public const int Usage = 64;
}

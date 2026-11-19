using System.Globalization;
using SqlToPg.Journal;
using SqlToPg.Mapping;
using SqlToPg.Safety;
using SqlToPg.Schema;
using SqlToPg.Transfer;

namespace SqlToPg.Cli;

/// <summary>One method per sub-command. Each reads its options, refuses unknown ones, and returns an exit code.</summary>
internal sealed class Commands(Endpoints endpoints, TextWriter log)
{
    public const string DefaultJournalSchema = "sql_to_pg_journal";

    /// <summary><c>scaffold</c>: drafts a mapping file by pairing the two catalogues by name.</summary>
    public async Task<int> ScaffoldAsync(ArgBag args, CancellationToken ct)
    {
        var output = args.Required("out");
        var options = new MapScaffolder.Options(
            args.Text("source-schema") ?? "dbo",
            args.Text("target-schema") ?? "public",
            args.Text("strip-source-prefix"),
            args.Text("add-target-prefix"));
        var force = args.Flag("force");
        args.RejectUnknown();

        if (File.Exists(output) && !force)
        {
            throw new UsageException($"{output} exists; pass --force to overwrite it");
        }

        await using var pg = endpoints.OpenTarget(1);
        var ms = await MssqlSchemaSnapshot.CaptureAsync(endpoints.SourceForReaders(), ct);
        var target = await PgSchemaSnapshot.CaptureAsync(pg, ct);
        var doc = MapScaffolder.Draft(ms, target, options);
        await File.WriteAllTextAsync(output, doc.ToJson(), ct);

        var paired = doc.Tables.Count(t => t.Enabled);
        log.WriteLine($"wrote {output}: {doc.Tables.Count} source table(s), {paired} paired with a target, {doc.Tables.Count - paired} disabled (no match)");
        foreach (var t in doc.Tables.Where(t => t.Enabled && (t.UnmappedSource is not null || t.UnmappedTarget is not null)))
        {
            log.WriteLine($"  review {t.Source} -> {t.Target}: {t.UnmappedSource?.Count ?? 0} source column(s) unpaired, {t.UnmappedTarget?.Count ?? 0} target column(s) unfed");
        }

        return ExitCodes.Ok;
    }

    /// <summary><c>check</c>: the audit alone; exit 2 when anything blocks.</summary>
    public async Task<int> CheckAsync(ArgBag args, CancellationToken ct)
    {
        var mapFile = args.Required("map");
        var tables = args.List("tables");
        var scope = ReadScope(args);
        var strict = args.Flag("strict-scope");
        var structuralOnly = args.Flag("no-data");
        args.RejectUnknown();

        var plan = MapResolver.Resolve(MapDocument.Load(mapFile), tables);
        await using var pg = endpoints.OpenTarget(1);
        var sourceCs = endpoints.SourceForReaders();
        var ms = await MssqlSchemaSnapshot.CaptureAsync(sourceCs, ct);
        var target = await PgSchemaSnapshot.CaptureAsync(pg, ct);
        var findings = MapAudit.Structural(plan, ms, target);
        if (!structuralOnly && !findings.Any(f => f.Gravity == Gravity.Block))
        {
            await using var reader = new MssqlReader(sourceCs);
            await reader.OpenAsync(ct);
            findings.AddRange(await MapAudit.WithDataAsync(plan, ms, target, reader, scope, strict, ct));
        }

        foreach (var f in findings.OrderByDescending(f => f.Gravity).ThenBy(f => f.Where, StringComparer.Ordinal))
        {
            log.WriteLine(f.ToString());
        }

        var order = target.Links.Arrange(plan.Tables.Select(t => t.Target));
        log.WriteLine($"{plan.Tables.Count} table(s); load order: {string.Join(" -> ", order.Fill.Select(t => t.Name))}");
        var blocks = findings.Count(f => f.Gravity == Gravity.Block);
        log.WriteLine(blocks == 0 ? "check passed" : $"check FAILED: {blocks} blocking finding(s)");
        return blocks == 0 ? ExitCodes.Ok : ExitCodes.Refused;
    }

    /// <summary><c>transfer</c>: the load.</summary>
    public async Task<int> TransferAsync(ArgBag args, CancellationToken ct)
    {
        var budget = args.Number("time-budget-minutes");
        var settings = new TransferSettings(
            MapFile: args.Required("map"),
            OnlyTables: args.List("tables"),
            Scope: ReadScope(args),
            StrictScope: args.Flag("strict-scope"),
            Mode: args.Choice("mode", LoadMode.Replace),
            Strategy: args.Choice("strategy", WriteStrategy.Direct),
            Lanes: args.Int("lanes", 4, 1, 64),
            DryRun: args.Flag("dry-run"),
            FreezeSource: args.Flag("freeze-source"),
            TimeBudget: budget is { } m ? TimeSpan.FromMinutes(m) : null,
            RowsPerSecond: args.Number("rows-per-second"),
            ScopeSetting: args.Text("scope-setting"),
            JournalSchema: Ident.Check(args.Text("journal-schema") ?? DefaultJournalSchema),
            ConfirmTargetDb: args.Text("confirm-target-db"),
            SkipVerify: args.Flag("skip-verify"));
        args.RejectUnknown();
        return await new TransferRun(endpoints, settings, log).ExecuteAsync(ct);
    }

    /// <summary><c>verify</c>: row counts, source against target, for the mapped tables.</summary>
    public async Task<int> VerifyAsync(ArgBag args, CancellationToken ct)
    {
        var mapFile = args.Required("map");
        var tables = args.List("tables");
        var scope = ReadScope(args);
        var append = args.Choice("mode", LoadMode.Replace) == LoadMode.Append;
        args.RejectUnknown();

        var plan = MapResolver.Resolve(MapDocument.Load(mapFile), tables);
        await using var pg = endpoints.OpenTarget(1);
        var target = await PgSchemaSnapshot.CaptureAsync(pg, ct);
        await using var reader = new MssqlReader(endpoints.SourceForReaders());
        await reader.OpenAsync(ct);
        var matches = await new CountReconciler(pg, target).CompareAsync(reader, plan.Tables, scope, append, ct);
        var bad = 0;
        foreach (var m in matches)
        {
            bad += m.Agrees ? 0 : 1;
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(m.Agrees ? "ok  " : "DIFF")} {m.Target,-50} source {m.Source,14:N0}  target {m.Destination,14:N0}{(m.Note is null ? "" : "  " + m.Note)}"));
        }

        log.WriteLine(bad == 0 ? "verify passed" : $"verify FAILED: {bad} table(s) differ");
        return bad == 0 ? ExitCodes.Ok : ExitCodes.Failed;
    }

    /// <summary><c>clear</c>: removes what a replace run would reload, without loading.</summary>
    public async Task<int> ClearAsync(ArgBag args, CancellationToken ct)
    {
        var mapFile = args.Required("map");
        var tables = args.List("tables");
        var scope = ReadScope(args);
        var scopeSetting = args.Text("scope-setting");
        var confirm = args.Text("confirm-target-db");
        var journalSchema = Ident.Check(args.Text("journal-schema") ?? DefaultJournalSchema);
        args.RejectUnknown();

        var plan = MapResolver.Resolve(MapDocument.Load(mapFile), tables);
        await using var pg = endpoints.OpenTarget(1);
        await TargetConfirmation.RequireAsync(pg, confirm, ct);
        var target = await PgSchemaSnapshot.CaptureAsync(pg, ct);
        await using var guard = await ExclusiveRunGuard.TryTakeAsync(pg, scope.Value, ct);
        if (guard is null)
        {
            log.WriteLine("refused: another run holds the lock");
            return ExitCodes.Refused;
        }

        var journal = new RunJournal(pg, journalSchema);
        await journal.EnsureAsync(ct);
        var runId = await journal.OpenAsync(Endpoints.Operator, "clear", scope.Value, Path.GetFileName(mapFile), null, null, ct);
        var status = "failed";
        try
        {
            var order = target.Links.Arrange(plan.Tables.Select(t => t.Target));
            await new TargetClearer(pg, target, scopeSetting, log).ClearAsync(plan.Tables, order, scope.Value, ct);
            status = "succeeded";
            log.WriteLine("cleared");
            return ExitCodes.Ok;
        }
        finally
        {
            await journal.CloseAsync(runId, status, null, log);
        }
    }

    /// <summary><c>unfreeze-source</c>: returns the source database to READ_WRITE after a run died while it was frozen.</summary>
    public async Task<int> UnfreezeAsync(ArgBag args, CancellationToken ct)
    {
        args.RejectUnknown();
        await using var reader = new MssqlReader(endpoints.SourceForReaders());
        await reader.SetReadOnlyAsync(false, ct);
        log.WriteLine($"source database {reader.Database} is READ_WRITE");
        return ExitCodes.Ok;
    }

    /// <summary><c>history</c>: recent runs from the journal, or one run's tables.</summary>
    public async Task<int> HistoryAsync(ArgBag args, CancellationToken ct)
    {
        var limit = args.Int("limit", 20, 1, 1000);
        var run = args.Text("run");
        var journalSchema = Ident.Check(args.Text("journal-schema") ?? DefaultJournalSchema);
        args.RejectUnknown();

        await using var pg = endpoints.OpenTarget(1);
        var journal = new RunJournal(pg, journalSchema);
        if (run is not null)
        {
            if (!long.TryParse(run, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                throw new UsageException("--run takes a run id");
            }

            foreach (var t in await journal.TablesOfAsync(id, ct))
            {
                log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{t.Status,-8} {t.Target,-50} source {t.RowsSource,14:N0}  written {t.RowsWritten,14:N0}  {t.Seconds,8:0.0}s  {t.Detail}"));
            }

            return ExitCodes.Ok;
        }

        foreach (var r in await journal.RecentAsync(limit, ct))
        {
            log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"#{r.RunId,-5} {r.StartedAt:yyyy-MM-dd HH:mm} {r.Status,-9} {r.Command,-8} {r.Mode,-7} scope={r.Scope ?? "-",-38} {r.Tables,4} tables {r.Rows,14:N0} rows  by {r.Operator}{(r.Note is null ? "" : "  — " + r.Note)}"));
        }

        return ExitCodes.Ok;
    }

    private static ScopeFilter ReadScope(ArgBag args)
    {
        var value = args.Text("scope");
        var claim = args.Flag("claim-blank");
        if (claim && value is null)
        {
            throw new UsageException("--claim-blank needs --scope");
        }

        return value is null ? ScopeFilter.None : new ScopeFilter(value, claim);
    }
}

using SqlToPg.Cli;
using SqlToPg.Safety;
using SqlToPg.Transfer;

namespace SqlToPg;

public static class Program
{
    private const string Help = """
        sql_to_pg_migration — map SQL Server tables/columns onto PostgreSQL and bulk-load the data.

        Connections come from the environment only:
          SQLTOPG_MSSQL      SQL Server connection string (source)
          SQLTOPG_PG         PostgreSQL connection string (target)
          SQLTOPG_OPERATOR   name recorded in the journal (default: OS user)

        Commands:
          scaffold         --out <map.json> [--source-schema dbo] [--target-schema public]
                           [--strip-source-prefix tbl] [--add-target-prefix x_] [--force]
          check            --map <map.json> [--tables a,b] [--scope <v> [--claim-blank]] [--strict-scope] [--no-data]
          transfer         --map <map.json> [--tables a,b] [--scope <v> [--claim-blank]] [--strict-scope]
                           [--mode replace|append] [--strategy direct|holding] [--lanes 4]
                           [--dry-run] [--freeze-source] [--time-budget-minutes n] [--rows-per-second n]
                           [--scope-setting app.current_tenant] [--journal-schema sql_to_pg_journal]
                           [--skip-verify] --confirm-target-db <db>
          verify           --map <map.json> [--tables a,b] [--scope <v> [--claim-blank]] [--mode replace|append]
          clear            --map <map.json> [--tables a,b] [--scope <v>] [--scope-setting ...] --confirm-target-db <db>
          unfreeze-source
          history          [--limit 20] [--run <id>] [--journal-schema sql_to_pg_journal]

        Exit codes: 0 ok, 1 failed, 2 refused (audit/lock/budget), 64 usage.
        """;

    public static async Task<int> Main(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine(Help);
            return argv.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C cancels cleanly (journal closed, source thawed); a second one kills.
            if (!cts.IsCancellationRequested)
            {
                e.Cancel = true;
                Console.Error.WriteLine("cancelling… (Ctrl+C again to kill)");
                cts.Cancel();
            }
        };

        var log = Console.Out;
        var commands = new Commands(Endpoints.FromEnvironment(), log);
        try
        {
            var args = new ArgBag(argv.Skip(1));
            return argv[0] switch
            {
                "scaffold" => await commands.ScaffoldAsync(args, cts.Token),
                "check" => await commands.CheckAsync(args, cts.Token),
                "transfer" => await commands.TransferAsync(args, cts.Token),
                "verify" => await commands.VerifyAsync(args, cts.Token),
                "clear" => await commands.ClearAsync(args, cts.Token),
                "unfreeze-source" => await commands.UnfreezeAsync(args, cts.Token),
                "history" => await commands.HistoryAsync(args, cts.Token),
                _ => throw new UsageException($"unknown command '{argv[0]}' (try --help)"),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.Usage;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"mapping error: {ex.Message}");
            return ExitCodes.Usage;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("cancelled; nothing of the table in flight was committed");
            return ExitCodes.Failed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                Console.Error.WriteLine($"  caused by {inner.GetType().Name}: {inner.Message}");
            }

            return ExitCodes.Failed;
        }
    }
}

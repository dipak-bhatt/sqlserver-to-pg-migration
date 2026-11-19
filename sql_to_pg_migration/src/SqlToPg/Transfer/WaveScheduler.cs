using SqlToPg.Safety;
using SqlToPg.Schema;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace SqlToPg.Transfer;

/// <summary>
/// Runs one piece of work per table, up to <c>lanes</c> at a time, never starting a table before every
/// parent it has in the set has <b>committed</b> — so a child's foreign keys always find their rows.
/// Knotted tables (a foreign-key cycle and whatever hangs off it) run after everything else, one by one.
/// <para>
/// A single coordinator loop owns all the bookkeeping: it starts what is ready, waits for any lane to
/// finish, releases that table's children, and repeats. Each lane borrows a SQL Server reader from a
/// small pool and returns it; a reader that failed is discarded and replaced.
/// </para>
/// <para>
/// The first real failure cancels the lanes in flight, starts nothing new, and is what the caller sees.
/// A transient one (dropped connection, timeout, deadlock or serialization victim) is retried in place
/// after <see cref="Backoff"/>; the work must leave nothing behind when it fails, which one transaction
/// per table guarantees.
/// </para>
/// </summary>
public sealed class WaveScheduler(int lanes, Func<CancellationToken, Task<MssqlReader>> openReader, TextWriter log)
{
    public IReadOnlyList<TimeSpan> Backoff { get; init; } = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45)];

    public async Task RunAsync(
        TableOrder order,
        Func<QualifiedName, IReadOnlyCollection<QualifiedName>> parentsOf,
        Func<MssqlReader, QualifiedName, CancellationToken, Task> work,
        CancellationToken ct)
    {
        var knotted = order.Knotted.ToHashSet();
        var flowing = order.Fill.Where(t => !knotted.Contains(t)).ToList();
        var rank = flowing.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);

        var blockers = new Dictionary<QualifiedName, int>();
        var unlocks = flowing.ToDictionary(t => t, _ => new List<QualifiedName>());
        foreach (var table in flowing)
        {
            var parents = parentsOf(table).Where(p => rank.ContainsKey(p) && p != table).Distinct().ToList();
            blockers[table] = parents.Count;
            foreach (var p in parents)
            {
                unlocks[p].Add(table);
            }
        }

        // Ready tables leave in fill order, so lanes=1 reproduces the serial order exactly.
        var ready = new PriorityQueue<QualifiedName, int>(flowing.Where(t => blockers[t] == 0).Select(t => (t, rank[t])));
        var idle = new Stack<MssqlReader>();
        var running = new Dictionary<Task<MssqlReader>, QualifiedName>();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var width = Math.Max(1, lanes);

        try
        {
            Exception? failure = null;
            while (failure is null && (ready.Count > 0 || running.Count > 0))
            {
                while (ready.Count > 0 && running.Count < width)
                {
                    var table = ready.Dequeue();
                    var reader = idle.Count > 0 ? idle.Pop() : null;
                    running.Add(RunOneAsync(reader, table, work, abort.Token), table);
                }

                var finished = await Task.WhenAny(running.Keys);
                var done = running[finished];
                running.Remove(finished);
                if (finished.IsCompletedSuccessfully)
                {
                    idle.Push(finished.Result);
                    foreach (var child in unlocks[done])
                    {
                        if (--blockers[child] == 0)
                        {
                            ready.Enqueue(child, rank[child]);
                        }
                    }
                }
                else
                {
                    failure = finished.Exception?.InnerException ?? new OperationCanceledException(ct);
                }
            }

            if (failure is not null)
            {
                await abort.CancelAsync();
                foreach (var (task, _) in running)
                {
                    await ((Task)task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    if (task.IsCompletedSuccessfully)
                    {
                        idle.Push(task.Result);
                    }
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
            }

            foreach (var table in order.Knotted)
            {
                var reader = idle.Count > 0 ? idle.Pop() : null;
                idle.Push(await RunOneAsync(reader, table, work, ct));
            }
        }
        finally
        {
            while (idle.Count > 0)
            {
                await idle.Pop().DisposeAsync();
            }
        }
    }

    /// <summary>One table on a borrowed (or fresh) reader, retried in place while the failure is transient.</summary>
    private async Task<MssqlReader> RunOneAsync(MssqlReader? reader, QualifiedName table, Func<MssqlReader, QualifiedName, CancellationToken, Task> work, CancellationToken ct)
    {
        await Task.Yield();
        reader ??= await openReader(ct);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await work(reader, table, ct);
                return reader;
            }
            catch (Exception ex) when (attempt < Backoff.Count && !ct.IsCancellationRequested && IsTransient(ex))
            {
                var wait = Backoff[attempt];
                log.WriteLine($"  ~ {table}: {ex.GetType().Name}: {ex.Message} — retry {attempt + 1}/{Backoff.Count} in {wait.TotalSeconds:0}s");
                await reader.DisposeAsync();
                await Task.Delay(wait, ct);
                reader = await openReader(ct);
            }
            catch
            {
                await reader.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>
    /// Worth retrying unchanged: transport trouble or the engine picking this transaction as a victim.
    /// Never a constraint, a policy refusal or a value that will not convert — those fail the same way
    /// every time.
    /// </summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        PostgresException pg => pg.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CannotConnectNow,
        NpgsqlException npg => npg.IsTransient,
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e => MssqlTransient.Contains(e.Number)),
        TimeoutException => true,
        _ => false,
    };

    /// <summary>SQL Server: 1205 deadlock victim, -2 timeout, and the usual transport/availability numbers.</summary>
    private static readonly HashSet<int> MssqlTransient = [-2, 20, 64, 121, 233, 1205, 4060, 10053, 10054, 10060, 10928, 10929, 40143, 40197, 40501, 40613, 49918, 49919, 49920];
}

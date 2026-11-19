using System.Globalization;

namespace SqlToPg.Transfer;

/// <summary>
/// How long the load should take, worked out before anything is written: total rows over the rate,
/// sped up by the extra lanes — but never shorter than the largest table alone, since one table is
/// one stream however many lanes run.
/// </summary>
public sealed record DurationForecast(long TotalRows, long LargestTable, double RowsPerSecond, string RateSource, int Lanes, TimeSpan Expected)
{
    public const double FallbackRowsPerSecond = 40_000;

    /// <summary>
    /// What each lane after the first adds. Lanes share disks, CPUs and the network on both ends, so
    /// four are not four times one; a quarter per extra lane is deliberately pessimistic — a forecast
    /// that runs long refuses a window that would have fit, one that runs short starts a run the
    /// budget then has to abort.
    /// </summary>
    public const double ExtraLaneGain = 0.25;

    public static DurationForecast Make(IEnumerable<long> tableRows, double rowsPerSecond, string rateSource, int lanes)
    {
        if (rowsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowsPerSecond), "rows per second must be positive");
        }

        var rows = tableRows.ToList();
        var total = rows.Sum();
        var largest = rows.Count == 0 ? 0 : rows.Max();
        var speedup = 1 + (Math.Max(1, lanes) - 1) * ExtraLaneGain;
        var seconds = Math.Max(total / (rowsPerSecond * speedup), largest / rowsPerSecond);
        return new DurationForecast(total, largest, rowsPerSecond, rateSource, lanes, TimeSpan.FromSeconds(Math.Ceiling(seconds)));
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"~{Expected.TotalMinutes:0.#} min for {TotalRows:N0} rows (largest table {LargestTable:N0}) at {RowsPerSecond:N0} rows/s [{RateSource}] over {Lanes} lane(s)");
}

/// <summary>
/// An optional ceiling on a run's duration. A run whose forecast is already over it does not start;
/// a run that reaches it is cancelled wherever it is, and fails through the normal failure path.
/// </summary>
public sealed class TimeBudget(TimeSpan? limit) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public TimeSpan? Limit { get; } = limit;

    public CancellationToken Token => _cts.Token;

    public bool Exhausted => _cts.IsCancellationRequested;

    public bool Forbids(DurationForecast forecast) => Limit is { } l && forecast.Expected > l;

    public void Start()
    {
        if (Limit is { } l)
        {
            _cts.CancelAfter(l);
        }
    }

    /// <summary>Once the budget has fired, whatever failure it caused is reported as what it is.</summary>
    public Exception Explain(Exception caught) =>
        Exhausted ? new TimeoutException($"the time budget of {Limit!.Value.TotalMinutes:0.#} min ran out before the load finished", caught) : caught;

    public void Dispose() => _cts.Dispose();
}

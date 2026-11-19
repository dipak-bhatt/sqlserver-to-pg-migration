using SqlToPg.Cli;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace SqlToPg.Safety;

/// <summary>
/// Where the tool reads and writes. Connection strings come only from the environment — never the
/// command line — so no password lands in a shell history or a process listing.
/// </summary>
public sealed class Endpoints
{
    public const string SourceVariable = "SQLTOPG_MSSQL";
    public const string TargetVariable = "SQLTOPG_PG";
    public const string OperatorVariable = "SQLTOPG_OPERATOR";

    private Endpoints(string? source, string? target)
    {
        SourceConnection = source;
        TargetConnection = target;
    }

    public string? SourceConnection { get; }

    public string? TargetConnection { get; }

    public static Endpoints FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(SourceVariable), Environment.GetEnvironmentVariable(TargetVariable));

    public string Source => SourceConnection ?? throw new UsageException($"set {SourceVariable} to the SQL Server connection string");

    public string Target => TargetConnection ?? throw new UsageException($"set {TargetVariable} to the PostgreSQL connection string");

    public static string Operator =>
        Environment.GetEnvironmentVariable(OperatorVariable) is { Length: > 0 } op ? op : Environment.UserName;

    public NpgsqlDataSource OpenTarget(int lanes)
    {
        var builder = new NpgsqlConnectionStringBuilder(Target)
        {
            ApplicationName = "sql_to_pg_migration",
            MaxPoolSize = Math.Max(new NpgsqlConnectionStringBuilder(Target).MaxPoolSize, (lanes * 3) + 4),
        };
        return NpgsqlDataSource.Create(builder);
    }

    public string SourceForReaders()
    {
        var builder = new SqlConnectionStringBuilder(Source) { ApplicationName = "sql_to_pg_migration" };
        return builder.ConnectionString;
    }

    public string DescribeTarget()
    {
        var b = new NpgsqlConnectionStringBuilder(Target);
        return $"{b.Host}:{b.Port}/{b.Database} as {b.Username}";
    }
}

/// <summary>
/// A destructive command must name the target database a second time (<c>--confirm-target-db</c>),
/// and the tool asks the server which database it is really connected to. The claim is checked, not
/// trusted: a connection string pointed at the wrong server is refused before anything is written.
/// </summary>
public static class TargetConfirmation
{
    public static async Task<string> ActualDatabaseAsync(NpgsqlDataSource target, CancellationToken ct)
    {
        await using var conn = await target.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT current_database()", conn);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task RequireAsync(NpgsqlDataSource target, string? claimed, CancellationToken ct)
    {
        var actual = await ActualDatabaseAsync(target, ct);
        if (claimed is null)
        {
            throw new UsageException($"this command writes to PostgreSQL database '{actual}'; repeat its name with --confirm-target-db {actual}");
        }

        if (!string.Equals(claimed, actual, StringComparison.Ordinal))
        {
            throw new UsageException($"--confirm-target-db says '{claimed}', but the connection is to '{actual}'; refusing");
        }
    }
}

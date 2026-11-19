using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace SqlToPg.Journal;

/// <summary>
/// Keeps two writing runs from stepping on each other — one clearing while another loads. Two
/// session-level advisory locks, held on a dedicated connection for the whole command:
/// <list type="bullet">
/// <item>the <b>database</b> key: an unscoped run takes it exclusively; a scoped run takes it shared,
/// so scoped runs can proceed side by side but never alongside a whole-database run;</item>
/// <item>the <b>scope</b> key: taken exclusively by a scoped run, so the same scope never runs twice.</item>
/// </list>
/// Both are <c>try</c> locks: a second run is refused immediately with a message, never queued. The
/// server releases them if the process dies.
/// </summary>
public sealed class ExclusiveRunGuard : IAsyncDisposable
{
    private readonly NpgsqlConnection _conn;

    private ExclusiveRunGuard(NpgsqlConnection conn) => _conn = conn;

    public static async Task<ExclusiveRunGuard?> TryTakeAsync(NpgsqlDataSource target, string? scopeValue, CancellationToken ct)
    {
        var conn = await target.OpenConnectionAsync(ct);
        try
        {
            var whole = KeyFor("*");
            if (scopeValue is null)
            {
                if (await TryAsync(conn, "pg_try_advisory_lock", whole, ct))
                {
                    return new ExclusiveRunGuard(conn);
                }
            }
            else if (await TryAsync(conn, "pg_try_advisory_lock_shared", whole, ct))
            {
                if (await TryAsync(conn, "pg_try_advisory_lock", KeyFor(scopeValue), ct))
                {
                    return new ExclusiveRunGuard(conn);
                }
            }
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }

        // Closing the session releases whatever was taken.
        await conn.DisposeAsync();
        return null;
    }

    private static async Task<bool> TryAsync(NpgsqlConnection conn, string function, long key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT {function}($1)", conn);
        cmd.Parameters.AddWithValue(key);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>A stable 64-bit key in this tool's own namespace, so no other advisory-lock user collides with it.</summary>
    internal static long KeyFor(string scope) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("sql2pg/" + scope.Trim().ToLowerInvariant())), 0);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", _conn);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (NpgsqlException)
        {
            // The session is gone, and the locks went with it.
        }
        finally
        {
            await _conn.DisposeAsync();
        }
    }
}

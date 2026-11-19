using System.Globalization;
using NpgsqlTypes;

namespace SqlToPg.Transfer;

/// <summary>
/// How a value read from SQL Server becomes the CLR value Npgsql's binary importer expects for a
/// PostgreSQL type. Everything about a column that does not depend on the value — the wire type, the
/// coercion, the time zone — is decided once, in <see cref="For"/>; per value only the delegate runs.
/// </summary>
public static class CellCoercion
{
    /// <summary>
    /// The importer's type for a PostgreSQL type as <c>format_type</c> spells it. The type modifier is
    /// removed from the middle, not cut at the first parenthesis: <c>timestamp(3) with time zone</c>
    /// truncated at "(" would read as a naive timestamp and silently shift every value.
    /// </summary>
    public static NpgsqlDbType WireTypeOf(string pgType)
    {
        var bare = Bare(pgType);
        if (bare.EndsWith("[]", StringComparison.Ordinal))
        {
            return NpgsqlDbType.Unknown;
        }

        return bare switch
        {
            "smallint" or "int2" => NpgsqlDbType.Smallint,
            "integer" or "int" or "int4" => NpgsqlDbType.Integer,
            "bigint" or "int8" => NpgsqlDbType.Bigint,
            "boolean" or "bool" => NpgsqlDbType.Boolean,
            "numeric" or "decimal" => NpgsqlDbType.Numeric,
            "money" => NpgsqlDbType.Money,
            "real" or "float4" => NpgsqlDbType.Real,
            "double precision" or "float8" => NpgsqlDbType.Double,
            "date" => NpgsqlDbType.Date,
            "time" or "time without time zone" => NpgsqlDbType.Time,
            "time with time zone" or "timetz" => NpgsqlDbType.TimeTz,
            "timestamp" or "timestamp without time zone" => NpgsqlDbType.Timestamp,
            "timestamp with time zone" or "timestamptz" => NpgsqlDbType.TimestampTz,
            "interval" => NpgsqlDbType.Interval,
            "uuid" => NpgsqlDbType.Uuid,
            "bytea" => NpgsqlDbType.Bytea,
            "json" => NpgsqlDbType.Json,
            "jsonb" => NpgsqlDbType.Jsonb,
            "xml" => NpgsqlDbType.Xml,
            "text" or "character varying" or "varchar" or "character" or "char" or "bpchar" or "citext" or "name" => NpgsqlDbType.Text,
            _ => NpgsqlDbType.Unknown,
        };
    }

    /// <summary>Whether this tool can write the type at all; the audit refuses a mapped column that it cannot.</summary>
    public static bool IsSupported(string pgType) => WireTypeOf(pgType) != NpgsqlDbType.Unknown;

    internal static string Bare(string pgType)
    {
        var t = pgType.Trim().ToLowerInvariant();
        while (t.IndexOf('(', StringComparison.Ordinal) is var open and >= 0)
        {
            var close = t.IndexOf(')', open);
            t = close > open ? t[..open] + t[(close + 1)..] : t[..open];
        }

        return string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>One column's resolved conversion.</summary>
    /// <param name="timeZone">What a naive source datetime meant, for a <c>timestamptz</c> target. UTC when null.</param>
    public static CellCoercer For(string pgType, string? timeZone = null, bool trimEnd = false, bool stripNul = false)
    {
        var wire = WireTypeOf(pgType);
        if (wire == NpgsqlDbType.Unknown)
        {
            throw new NotSupportedException($"target type '{pgType}' is not supported by the loader");
        }

        // Looked up on first use: a column whose values all carry their own offset never needs it.
        var zone = timeZone is null || timeZone.Equals("UTC", StringComparison.OrdinalIgnoreCase)
            ? null
            : new Lazy<TimeZoneInfo>(() => TimeZoneInfo.FindSystemTimeZoneById(timeZone));

        Func<object, object> convert = wire switch
        {
            NpgsqlDbType.Smallint => raw => AsInt16(raw),
            NpgsqlDbType.Integer => raw => AsInt32(raw),
            NpgsqlDbType.Bigint => raw => AsInt64(raw),
            NpgsqlDbType.Boolean => raw => AsBool(raw),
            NpgsqlDbType.Numeric or NpgsqlDbType.Money => raw => AsDecimal(raw),
            NpgsqlDbType.Real => raw => AsSingle(raw),
            NpgsqlDbType.Double => raw => AsDouble(raw),
            NpgsqlDbType.Date => raw => AsDate(raw),
            NpgsqlDbType.Time => raw => AsTime(raw),
            NpgsqlDbType.TimeTz => raw => AsTimeTz(raw),
            NpgsqlDbType.Timestamp => raw => AsWallClock(raw),
            NpgsqlDbType.TimestampTz => raw => AsInstant(raw, zone),
            NpgsqlDbType.Interval => raw => AsInterval(raw),
            NpgsqlDbType.Uuid => raw => AsGuid(raw),
            NpgsqlDbType.Bytea => AsBytes,
            _ => AsText,
        };

        if (wire is NpgsqlDbType.Text && (trimEnd || stripNul))
        {
            var inner = convert;
            convert = raw =>
            {
                var s = (string)inner(raw);
                if (stripNul)
                {
                    s = s.Replace("\0", string.Empty, StringComparison.Ordinal);
                }

                return trimEnd ? s.TrimEnd(' ') : s;
            };
        }

        return new CellCoercer(wire, convert);
    }

    private static short AsInt16(object v) => v switch
    {
        short s => s,
        byte b => b,
        int i => checked((short)i),
        long l => checked((short)l),
        bool b => (short)(b ? 1 : 0),
        string s => short.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => Convert.ToInt16(v, CultureInfo.InvariantCulture),
    };

    private static int AsInt32(object v) => v switch
    {
        int i => i,
        short s => s,
        byte b => b,
        long l => checked((int)l),
        bool b => b ? 1 : 0,
        string s => int.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => Convert.ToInt32(v, CultureInfo.InvariantCulture),
    };

    private static long AsInt64(object v) => v switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        bool b => b ? 1L : 0L,
        string s => long.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture),
    };

    private static bool AsBool(object v) => v switch
    {
        bool b => b,
        byte b => b != 0,
        short s => s != 0,
        int i => i != 0,
        long l => l != 0,
        string s => s.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "t" or "y" or "yes" => true,
            "0" or "false" or "f" or "n" or "no" => false,
            _ => throw new FormatException("not a boolean"),
        },
        _ => Convert.ToBoolean(v, CultureInfo.InvariantCulture),
    };

    private static decimal AsDecimal(object v) => v switch
    {
        decimal d => d,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        double d => (decimal)d,
        float f => (decimal)f,
        string s => decimal.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
    };

    private static float AsSingle(object v) => v switch
    {
        float f => f,
        double d => (float)d,
        decimal m => (float)m,
        string s => float.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => Convert.ToSingle(v, CultureInfo.InvariantCulture),
    };

    private static double AsDouble(object v) => v switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        string s => double.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };

    private static DateOnly AsDate(object v) => v switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        DateTimeOffset dto => DateOnly.FromDateTime(dto.DateTime),
        string s => DateOnly.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException(),
    };

    private static TimeOnly AsTime(object v) => v switch
    {
        TimeOnly t => t,
        TimeSpan ts => TimeOnly.FromTimeSpan(ts),
        DateTime dt => TimeOnly.FromDateTime(dt),
        string s => TimeOnly.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException(),
    };

    private static DateTimeOffset AsTimeTz(object v) => v switch
    {
        DateTimeOffset dto => new DateTimeOffset(DateTime.MinValue.Add(dto.TimeOfDay), dto.Offset),
        TimeSpan ts => new DateTimeOffset(DateTime.MinValue.Add(ts), TimeSpan.Zero),
        _ => throw new InvalidCastException(),
    };

    /// <summary>A naive <c>timestamp</c>: the value as it was stored, never shifted. Npgsql refuses a UTC-kind value here.</summary>
    private static DateTime AsWallClock(object v) => v switch
    {
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified),
        DateTimeOffset dto => DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified),
        string s => DateTime.SpecifyKind(DateTime.Parse(s.Trim(), CultureInfo.InvariantCulture), DateTimeKind.Unspecified),
        _ => throw new InvalidCastException(),
    };

    /// <summary>
    /// A <c>timestamptz</c> is an instant. A <c>datetimeoffset</c> carries its own offset; a naive
    /// <c>datetime</c> is read in the mapping's time zone, or as UTC when none is given.
    /// </summary>
    internal static DateTime AsInstant(object v, Lazy<TimeZoneInfo>? zone) => v switch
    {
        DateTimeOffset dto => dto.UtcDateTime,
        DateTime { Kind: DateTimeKind.Utc } dt => dt,
        DateTime dt when zone is not null => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), zone.Value),
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        string s => DateTimeOffset.Parse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime,
        _ => throw new InvalidCastException(),
    };

    private static TimeSpan AsInterval(object v) => v switch
    {
        TimeSpan ts => ts,
        string s => TimeSpan.Parse(s.Trim(), CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException(),
    };

    private static Guid AsGuid(object v) => v switch
    {
        Guid g => g,
        string s => Guid.Parse(s.Trim()),
        byte[] { Length: 16 } b => new Guid(b),
        _ => throw new InvalidCastException(),
    };

    /// <summary>Only bytes are bytes; anything else means the column is paired with the wrong source.</summary>
    private static byte[] AsBytes(object v) => v as byte[]
        ?? throw new InvalidOperationException($"a bytea column was given a {v.GetType().Name}; the mapping pairs it with the wrong source column");

    private static string AsText(object v) => v switch
    {
        string s => s,
        char[] c => new string(c),
        Guid g => g.ToString("D"),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        byte[] => throw new InvalidOperationException("a text column was given binary data; map it to bytea or convert it with an expression"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty,
    };
}

/// <summary>A column's conversion, resolved once (<see cref="CellCoercion.For"/>).</summary>
public sealed class CellCoercer(NpgsqlDbType wireType, Func<object, object> convert)
{
    public NpgsqlDbType WireType { get; } = wireType;

    /// <summary>Text-like on the wire, where PostgreSQL cannot store U+0000.</summary>
    public bool IsTextual => WireType is NpgsqlDbType.Text or NpgsqlDbType.Json or NpgsqlDbType.Jsonb or NpgsqlDbType.Xml;

    public object? Apply(object? raw) => raw is null or DBNull ? null : convert(raw);
}

using System.Globalization;
using SqlToPg.Mapping;
using SqlToPg.Schema;
using NpgsqlTypes;

namespace SqlToPg.Transfer;

/// <summary>
/// Raised for a value that cannot be written. It names the table, the column and the row by its key
/// — never the value itself, which may be a name, an e-mail or worse in a column the mapping has wrong.
/// </summary>
public sealed class CellRejectedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// One table's row conversion: for each <see cref="FieldSpec"/> the coercer the target column's
/// declared type asks for, built once when the table starts.
/// </summary>
public sealed class RowShaper
{
    private readonly TableSpec _spec;
    private readonly CellCoercer[] _coercers;
    private readonly object?[] _constants;
    private readonly int[] _sourceOrdinal;
    private readonly int[] _keyFields;

    /// <param name="scopeValue">The run's <c>--scope</c> value, stamped into the scope column; null for an unscoped run.</param>
    public RowShaper(TableSpec spec, PgTable target, string defaultTimeZone, string? scopeValue)
    {
        _spec = spec;
        var count = spec.Fields.Count;
        _coercers = new CellCoercer[count];
        _constants = new object?[count];
        _sourceOrdinal = new int[count];
        var nextSourced = 0;
        for (var i = 0; i < count; i++)
        {
            var field = spec.Fields[i];
            var column = target.Column(field.Target)
                ?? throw new InvalidOperationException($"{spec.Target}.{field.Target} does not exist on the target (run 'check' first)");
            _coercers[i] = CellCoercion.For(column.Type, field.TimeZone ?? defaultTimeZone, field.TrimEnd, field.StripNul);
            _sourceOrdinal[i] = field.SelectItem is null ? -1 : nextSourced++;
            if (field.Feed is FeedKind.Literal or FeedKind.Scope)
            {
                var text = field.Feed == FeedKind.Literal ? field.Literal : scopeValue
                    ?? throw new InvalidOperationException($"{spec.Target} is scoped by {field.Target}; pass --scope <value>");
                _constants[i] = Coerce(i, text, "the literal value");
            }
        }

        SourceWidth = nextSourced;
        WireTypes = [.. _coercers.Select(c => c.WireType)];
        var key = spec.Key ?? target.PrimaryKey;
        _keyFields = [.. key
            .Select(k => spec.Fields.ToList().FindIndex(f => f.Target == k))
            .Where(i => i >= 0 && _sourceOrdinal[i] >= 0)];
    }

    /// <summary>How many values a source row carries (the select list's width).</summary>
    public int SourceWidth { get; }

    public NpgsqlDbType[] WireTypes { get; }

    /// <summary>
    /// The target row for one source row. <paramref name="ordinal"/> (1-based position in the read)
    /// names the row when the table has no key that the mapping reads.
    /// </summary>
    public object?[] Shape(object?[] sourced, long ordinal)
    {
        var row = new object?[_coercers.Length];
        for (var i = 0; i < row.Length; i++)
        {
            if (_sourceOrdinal[i] < 0)
            {
                row[i] = _constants[i];
                continue;
            }

            var value = Coerce(i, sourced[_sourceOrdinal[i]], () => RowName(sourced, ordinal));
            if (value is string text && _coercers[i].IsTextual && text.Contains('\0', StringComparison.Ordinal))
            {
                throw new CellRejectedException(
                    $"{_spec.Target}.{_spec.Fields[i].Target}: row {RowName(sourced, ordinal)} holds a NUL character, which PostgreSQL text cannot store. " +
                    "Nothing of this table was committed. Clean the source, or set \"stripNul\": true on the column.");
            }

            row[i] = value;
        }

        return row;
    }

    private object? Coerce(int field, object? raw, string where) => Coerce(field, raw, () => where);

    private object? Coerce(int field, object? raw, Func<string> where)
    {
        try
        {
            return _coercers[field].Apply(raw);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException
            or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            var why = ex is InvalidOperationException ? ex.Message : ex.GetType().Name;
            throw new CellRejectedException(
                $"{_spec.Target}.{_spec.Fields[field].Target}: {where()} cannot be written as {_coercers[field].WireType} ({why}). Nothing of this table was committed.", ex);
        }
    }

    private string RowName(object?[] sourced, long ordinal)
    {
        if (_keyFields.Length == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"#{ordinal} of the read (no key is mapped)");
        }

        var parts = _keyFields.Select(i =>
            $"{_spec.Fields[i].Target}={Convert.ToString(sourced[_sourceOrdinal[i]], CultureInfo.InvariantCulture) ?? "NULL"}");
        return "(" + string.Join(", ", parts) + ")";
    }
}

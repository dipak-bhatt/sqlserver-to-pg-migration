using SqlToPg.Cli;
using SqlToPg.Journal;
using SqlToPg.Mapping;
using SqlToPg.Safety;
using SqlToPg.Schema;
using SqlToPg.Transfer;
using NpgsqlTypes;

namespace SqlToPg.Tests;

public class TransferTests
{
    private static QualifiedName N(string name) => new("public", name);

    [Theory]
    [InlineData("timestamp(3) with time zone", NpgsqlDbType.TimestampTz)]
    [InlineData("timestamp(6) without time zone", NpgsqlDbType.Timestamp)]
    [InlineData("numeric(18,2)", NpgsqlDbType.Numeric)]
    [InlineData("character varying(40)", NpgsqlDbType.Text)]
    [InlineData("integer[]", NpgsqlDbType.Unknown)]
    [InlineData("geometry", NpgsqlDbType.Unknown)]
    public void Wire_type_keeps_the_time_zone_when_a_modifier_is_present(string pg, NpgsqlDbType expected) =>
        Assert.Equal(expected, CellCoercion.WireTypeOf(pg));

    [Fact]
    public void Naive_datetime_into_timestamptz_is_read_in_the_given_zone()
    {
        var local = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Unspecified);
        var utc = (DateTime)CellCoercion.For("timestamptz").Apply(local)!;
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(local.Ticks, utc.Ticks);

        var tokyo = (DateTime)CellCoercion.For("timestamptz", "Asia/Tokyo").Apply(local)!;
        Assert.Equal(new DateTime(2026, 1, 15, 1, 0, 0, DateTimeKind.Utc), tokyo);

        var offset = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.FromHours(5.75));
        Assert.Equal(offset.UtcDateTime, CellCoercion.For("timestamptz", "Asia/Tokyo").Apply(offset));
    }

    [Fact]
    public void Coercions_cover_sql_server_shapes()
    {
        Assert.Equal(true, CellCoercion.For("boolean").Apply((byte)1));
        Assert.Equal(false, CellCoercion.For("boolean").Apply("N"));
        Assert.Equal(new DateOnly(2026, 3, 4), CellCoercion.For("date").Apply(new DateTime(2026, 3, 4)));
        Assert.Equal(12.5m, CellCoercion.For("numeric(9,2)").Apply("12.5"));
        Assert.Equal("ab", CellCoercion.For("text", trimEnd: true).Apply("ab   "));
        Assert.Equal("ab", CellCoercion.For("text", stripNul: true).Apply("a\0b"));
        Assert.Null(CellCoercion.For("integer").Apply(DBNull.Value));
        Assert.Throws<OverflowException>(() => CellCoercion.For("smallint").Apply(100_000));
        Assert.Throws<InvalidOperationException>(() => CellCoercion.For("bytea").Apply("not bytes"));
        Assert.Throws<InvalidOperationException>(() => CellCoercion.For("text").Apply(new byte[] { 1 }));
        Assert.Throws<NotSupportedException>(() => CellCoercion.For("integer[]"));
    }

    private static (TableSpec Spec, PgTable Table) EmployeeTable()
    {
        var plan = MapResolver.Resolve(MapDocument.Parse("""
            { "scope": { "target": "tenant_id", "source": "TenantId" },
              "tables": [ { "source": "Employee", "target": "employee", "columns": [
                { "source": "Id", "target": "id" },
                { "source": "Name", "target": "name" },
                { "literal": "7", "target": "batch" } ] } ] }
            """));
        var table = new PgTable(N("employee"), [
            new PgColumn("tenant_id", "uuid", true, false, false),
            new PgColumn("id", "bigint", true, false, false),
            new PgColumn("name", "text", false, false, false),
            new PgColumn("batch", "smallint", false, false, false)], ["tenant_id", "id"]);
        return (plan.Tables[0], table);
    }

    [Fact]
    public void Shaper_converts_sourced_values_and_stamps_literal_and_scope()
    {
        var (spec, table) = EmployeeTable();
        var tenant = Guid.NewGuid();
        var shaper = new RowShaper(spec, table, "UTC", tenant.ToString());

        Assert.Equal(2, shaper.SourceWidth);
        Assert.Equal([NpgsqlDbType.Bigint, NpgsqlDbType.Text, NpgsqlDbType.Smallint, NpgsqlDbType.Uuid], shaper.WireTypes);
        Assert.Equal([42L, "Ann", (short)7, tenant], shaper.Shape([42, "Ann"], 1));
    }

    [Fact]
    public void Shaper_names_the_row_by_key_and_never_the_value()
    {
        var (spec, table) = EmployeeTable();
        var shaper = new RowShaper(spec, table, "UTC", Guid.NewGuid().ToString());

        var nul = Assert.Throws<CellRejectedException>(() => shaper.Shape([42, "secret\0name"], 9));
        Assert.Contains("public.employee.name", nul.Message);
        Assert.Contains("(id=42)", nul.Message);
        Assert.DoesNotContain("secret", nul.Message);

        var bad = Assert.Throws<CellRejectedException>(() => shaper.Shape(["x", "Ann"], 3));
        Assert.Contains("public.employee.id", bad.Message);
    }

    [Fact]
    public void Shaper_refuses_a_scoped_table_without_a_scope_value()
    {
        var (spec, table) = EmployeeTable();
        Assert.Throws<InvalidOperationException>(() => new RowShaper(spec, table, "UTC", null));
    }

    [Fact]
    public void Graph_orders_parents_first_and_puts_cycles_last()
    {
        var graph = new ForeignKeyGraph([
            (N("employee"), N("department")),
            (N("department"), N("company")),
            (N("payslip"), N("employee")),
            (N("a"), N("b")), (N("b"), N("a")),
            (N("employee"), N("employee")),
            (N("department"), N("not_loaded")),
        ]);

        var order = graph.Arrange([N("payslip"), N("employee"), N("department"), N("company"), N("a"), N("b")]);

        Assert.Equal(["company", "department", "employee", "payslip", "a", "b"], order.Fill.Select(t => t.Name));
        Assert.Equal(["a", "b"], order.Knotted.Select(t => t.Name));
        Assert.Equal("payslip", order.Clear[2].Name);
    }

    [Fact]
    public async Task Scheduler_never_starts_a_child_before_its_parent_finishes()
    {
        var graph = new ForeignKeyGraph([(N("c1"), N("p")), (N("c2"), N("p")), (N("g"), N("c1")), (N("x"), N("y")), (N("y"), N("x"))]);
        var order = graph.Arrange([N("p"), N("c1"), N("c2"), N("g"), N("solo"), N("x"), N("y")]);
        var finished = new System.Collections.Concurrent.ConcurrentDictionary<QualifiedName, bool>();
        var violations = 0;
        var scheduler = new WaveScheduler(3, _ => Task.FromResult(new MssqlReader("Server=none")), TextWriter.Null);

        await scheduler.RunAsync(order, graph.ParentsOf, async (_, table, ct) =>
        {
            if (graph.ParentsOf(table).Any(p => order.Fill.Contains(p) && !order.Knotted.Contains(table) && !finished.ContainsKey(p)))
            {
                Interlocked.Increment(ref violations);
            }

            await Task.Delay(20, ct);
            finished[table] = true;
        }, CancellationToken.None);

        Assert.Equal(0, violations);
        Assert.Equal(7, finished.Count);
    }

    [Fact]
    public async Task Scheduler_surfaces_the_first_failure_and_retries_transient_ones()
    {
        var graph = new ForeignKeyGraph([(N("child"), N("parent"))]);
        var order = graph.Arrange([N("parent"), N("child"), N("other")]);
        var attempts = 0;
        var started = new System.Collections.Concurrent.ConcurrentBag<string>();
        var scheduler = new WaveScheduler(2, _ => Task.FromResult(new MssqlReader("Server=none")), TextWriter.Null) { Backoff = [TimeSpan.Zero] };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunAsync(order, graph.ParentsOf, (_, table, _) =>
        {
            started.Add(table.Name);
            if (table.Name == "other" && Interlocked.Increment(ref attempts) == 1)
            {
                throw new TimeoutException("blip");
            }

            return table.Name == "parent" ? throw new InvalidOperationException("parent broke") : Task.CompletedTask;
        }, CancellationToken.None));

        Assert.Equal("parent broke", ex.Message);
        Assert.DoesNotContain("child", started);
    }

    [Fact]
    public void Conflict_clause_follows_the_rule()
    {
        var spec = EmployeeTable().Spec;
        Assert.Equal("", PgTableWriter.ConflictClause(spec));
        Assert.Equal(" ON CONFLICT DO NOTHING", PgTableWriter.ConflictClause(spec with { OnConflict = ConflictRule.Skip }));
        var update = PgTableWriter.ConflictClause(spec with { OnConflict = ConflictRule.Update }, ["tenant_id", "id"]);
        Assert.Equal(" ON CONFLICT (\"tenant_id\", \"id\") DO UPDATE SET \"name\" = EXCLUDED.\"name\", \"batch\" = EXCLUDED.\"batch\"", update);
        Assert.Throws<InvalidOperationException>(() => PgTableWriter.ConflictClause(spec with { OnConflict = ConflictRule.Update }));
    }

    [Fact]
    public void Scratch_names_fit_postgres_and_stay_distinct()
    {
        var a = Ident.Bounded("hold_1234abcd_", "public_" + new string('x', 80) + "_a");
        var b = Ident.Bounded("hold_1234abcd_", "public_" + new string('x', 80) + "_b");
        Assert.True(a.Length <= 63);
        Assert.NotEqual(a, b);
        Assert.Equal("hold_1234abcd_", PgTableWriter.HoldingPrefix("1234ABCD-0000-0000-0000-000000000000"));
        Assert.Equal("hold_all_", PgTableWriter.HoldingPrefix(null));
    }

    [Fact]
    public void Identifiers_are_validated_and_quoted()
    {
        Assert.Equal("\"hr\".\"employee\"", Ident.Pg(QualifiedName.Parse("hr.employee", "public")));
        Assert.Equal("[dbo].[Order Line]", Ident.Ms(QualifiedName.Parse("[Order Line]", "dbo")));
        Assert.Throws<ArgumentException>(() => Ident.Pg("x\"; DROP TABLE y; --"));
    }

    [Fact]
    public void Forecast_is_never_shorter_than_the_largest_table()
    {
        var f = DurationForecast.Make([1_000_000, 10_000, 10_000], 10_000, "test", 8);
        Assert.Equal(TimeSpan.FromSeconds(100), f.Expected);
        using var budget = new TimeBudget(TimeSpan.FromMinutes(1));
        Assert.True(budget.Forbids(f));
    }

    [Fact]
    public void Arguments_refuse_typos()
    {
        var args = new ArgBag(["--map", "m.json", "--dry-rn", "--lanes=8"]);
        Assert.Equal("m.json", args.Required("map"));
        Assert.Equal(8, args.Int("lanes", 4, 1, 64));
        Assert.False(args.Flag("dry-run"));
        var ex = Assert.Throws<UsageException>(args.RejectUnknown);
        Assert.Contains("--dry-rn", ex.Message);
        Assert.Throws<UsageException>(() => new ArgBag(["--mode", "sideways"]).Choice("mode", LoadMode.Replace));
    }

    [Fact]
    public void Lock_keys_are_stable_and_case_insensitive() =>
        Assert.Equal(ExclusiveRunGuard.KeyFor("ABC"), ExclusiveRunGuard.KeyFor(" abc "));
}

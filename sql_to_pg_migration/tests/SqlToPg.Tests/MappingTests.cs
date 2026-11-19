using SqlToPg.Mapping;
using SqlToPg.Safety;
using SqlToPg.Schema;
using SqlToPg.Transfer;

namespace SqlToPg.Tests;

public class MappingTests
{
    private const string Sample = """
        {
          // comments and trailing commas are allowed
          "sourceSchema": "dbo",
          "targetSchema": "hr",
          "scope": { "target": "tenant_id", "source": "TenantId" },
          "tables": [
            { "source": "Employee", "target": "employee", "key": ["id"],
              "columns": [
                { "source": "EmployeeId", "target": "id" },
                { "expression": "UPPER(src.[Code])", "target": "code" },
                { "literal": "true", "target": "is_migrated" },
              ] },
            { "source": "dbo.Country", "target": "public.country", "noScope": true, "role": "shared",
              "columns": [ { "source": "Id", "target": "id" } ] },
            { "source": "Old", "target": "old", "enabled": false, "columns": [] }
          ]
        }
        """;

    [Fact]
    public void Resolves_defaults_scope_and_feeds()
    {
        var plan = MapResolver.Resolve(MapDocument.Parse(Sample));

        Assert.Equal(2, plan.Tables.Count);
        var emp = plan.Tables[0];
        Assert.Equal(new QualifiedName("dbo", "Employee"), emp.Source);
        Assert.Equal(new QualifiedName("hr", "employee"), emp.Target);
        Assert.Equal("tenant_id", emp.ScopeTargetColumn);
        Assert.Equal("TenantId", emp.ScopeSourceColumn);
        Assert.Equal([FeedKind.Column, FeedKind.Expression, FeedKind.Literal, FeedKind.Scope], emp.Fields.Select(f => f.Feed));
        Assert.Equal(["src.[EmployeeId]", "(UPPER(src.[Code]))"], emp.SourcedFields.Select(f => f.SelectItem));

        var country = plan.Tables[1];
        Assert.False(country.IsScoped);
        Assert.Equal(TableRole.Shared, country.Role);
        Assert.Equal("UTC", plan.DefaultTimeZone);
    }

    [Fact]
    public void Collects_every_problem_before_refusing()
    {
        var doc = MapDocument.Parse("""
            { "scope": { "target": "tenant_id" },
              "tables": [
                { "source": "A", "target": "a", "columns": [
                    { "source": "X", "target": "x" },
                    { "source": "Y", "target": "x" },
                    { "target": "z" },
                    { "source": "T", "target": "tenant_id" } ] },
                { "source": "B", "target": "a", "columns": [ { "source": "X", "target": "x" } ] },
                { "source": "C", "target": "c", "columns": [ { "source": "bad;name", "target": "c" } ] }
              ] }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => MapResolver.Resolve(doc));
        Assert.Contains("fed more than once", ex.Message);
        Assert.Contains("exactly one of source, expression or literal", ex.Message);
        Assert.Contains("scope column", ex.Message);
        Assert.Contains("mapped more than once", ex.Message);
        Assert.Contains("not an identifier", ex.Message);
    }

    [Fact]
    public void Table_filter_matches_source_or_target_names()
    {
        var plan = MapResolver.Resolve(MapDocument.Parse(Sample), ["country"]);
        Assert.Single(plan.Tables);
        Assert.Throws<InvalidDataException>(() => MapResolver.Resolve(MapDocument.Parse(Sample), ["nope"]));
    }

    [Theory]
    [InlineData("EmployeeID", "employee_id")]
    [InlineData("HTTPLog", "http_log")]
    [InlineData("Address2Line", "address2_line")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("Emp Code", "emp_code")]
    public void Snake_cases_sql_server_names(string input, string expected) => Assert.Equal(expected, MapScaffolder.Snake(input));

    [Fact]
    public void Scaffold_pairs_by_folded_name_and_reports_leftovers()
    {
        var ms = new MssqlSchemaSnapshot([
            new MsTable(new("dbo", "EmployeeAddress"), [
                new MsColumn("EmployeeID", "int", false, false),
                new MsColumn("City", "nvarchar(50)", true, false),
                new MsColumn("LegacyFlag", "bit", true, false)], ["EmployeeID"], 10),
            new MsTable(new("dbo", "Orphan"), [new MsColumn("Id", "int", false, false)], ["Id"], 0),
        ]);
        var pg = PgSchemaSnapshot.FromTables([
            new PgTable(new("public", "employee_address"), [
                new PgColumn("employee_id", "integer", true, false, false),
                new PgColumn("city", "character varying(50)", false, false, false),
                new PgColumn("created_at", "timestamp with time zone", true, false, false)], ["employee_id"]),
        ]);

        var doc = MapScaffolder.Draft(ms, pg, new MapScaffolder.Options());

        var paired = Assert.Single(doc.Tables, t => t.Enabled);
        Assert.Equal("public.employee_address", paired.Target);
        Assert.Equal(["employee_id", "city"], paired.Columns.Select(c => c.Target));
        Assert.Equal(["LegacyFlag bit"], paired.UnmappedSource!);
        Assert.Equal(["created_at timestamp with time zone NOT NULL"], paired.UnmappedTarget!);
        var orphan = Assert.Single(doc.Tables, t => !t.Enabled);
        Assert.Equal("public.orphan", orphan.Target);

        // The draft round-trips through the file format.
        Assert.Equal(2, MapDocument.Parse(doc.ToJson()).Tables.Count);
    }

    [Fact]
    public void Audit_blocks_what_would_fail_mid_load()
    {
        var plan = MapResolver.Resolve(MapDocument.Parse("""
            { "tables": [ { "source": "E", "target": "e", "columns": [
                { "source": "Id", "target": "id" },
                { "source": "Missing", "target": "name" },
                { "source": "Photo", "target": "photo_text" },
                { "literal": "abc", "target": "age" },
                { "source": "Id", "target": "total" } ] } ] }
            """));
        var ms = new MssqlSchemaSnapshot([new MsTable(new("dbo", "E"), [
            new MsColumn("Id", "int", false, false),
            new MsColumn("Photo", "varbinary(max)", true, false)], ["Id"], 1)]);
        var pg = PgSchemaSnapshot.FromTables([new PgTable(new("public", "e"), [
            new PgColumn("id", "integer", true, false, false),
            new PgColumn("name", "text", false, false, false),
            new PgColumn("photo_text", "text", false, false, false),
            new PgColumn("age", "integer", false, false, false),
            new PgColumn("total", "integer", false, false, true),
            new PgColumn("dept_id", "integer", true, false, false)], ["id"])]);

        var findings = MapAudit.Structural(plan, ms, pg).Where(f => f.Gravity == Gravity.Block).Select(f => f.Where + " " + f.Message).ToList();

        Assert.Contains(findings, f => f.Contains("public.e.name") && f.Contains("does not exist"));
        Assert.Contains(findings, f => f.Contains("photo_text") && f.Contains("cannot become"));
        Assert.Contains(findings, f => f.Contains("age") && f.Contains("literal"));
        Assert.Contains(findings, f => f.Contains("total") && f.Contains("generated"));
        Assert.Contains(findings, f => f.Contains("dept_id") && f.Contains("NOT NULL with no default"));
    }

    [Theory]
    [InlineData("nvarchar(100)", "character varying(50)", Gravity.Warn)]
    [InlineData("nvarchar(50)", "character varying(100)", null)]
    [InlineData("datetime", "timestamp(6) with time zone", Gravity.Note)]
    [InlineData("int", "uuid", Gravity.Block)]
    [InlineData("varchar(10)", "integer", Gravity.Warn)]
    [InlineData("bigint", "integer", Gravity.Warn)]
    public void Type_pairings(string ms, string pg, Gravity? expected) =>
        Assert.Equal(expected, MapAudit.TypeClash(ms, pg)?.Gravity);

    [Fact]
    public void Scope_predicate_is_added_to_the_mapping_filter()
    {
        var plan = MapResolver.Resolve(MapDocument.Parse(Sample));
        var emp = plan.Tables[0] with { Where = "src.[IsDeleted] = 0" };

        Assert.Equal(" WHERE (src.[IsDeleted] = 0) AND (src.[TenantId] = @scope)", MssqlReader.WhereClause(emp, new ScopeFilter("t1", false)));
        Assert.Contains("OR (src.[TenantId] IS NULL OR", MssqlReader.WhereClause(emp, new ScopeFilter("t1", true)));
        Assert.Equal(" WHERE (src.[IsDeleted] = 0)", MssqlReader.WhereClause(emp, ScopeFilter.None));
    }

    [Fact]
    public void The_shipped_demo_mapping_resolves()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "sql_to_pg_migration.slnx")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repository root not found");
        }

        var plan = MapResolver.Resolve(MapDocument.Load(Path.Combine(dir, "samples", "demo", "demo.map.json")));
        Assert.Equal(["country", "department", "employee", "employee_address"], plan.Tables.Select(t => t.Target.Name));
        Assert.Equal("Asia/Kathmandu", plan.DefaultTimeZone);
    }
}

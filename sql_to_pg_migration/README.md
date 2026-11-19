# sql_to_pg_migration

A standalone .NET command-line tool that copies data from **SQL Server** to **PostgreSQL**.

You describe the move in a single JSON mapping file: which source tables feed which target tables,
and which source columns (or expressions, or fixed values) feed which target columns. The tool then:

- **drafts** that mapping for you by matching the two catalogues by name,
- **checks** it against both databases (and the source rows) before anything is written,
- **loads** tables in foreign-key order over several parallel lanes using binary `COPY`,
- **realigns** identity/serial sequences and reconciles row counts afterwards,
- **records** every run in a journal inside the target database.

It supports multi-tenant ("scoped") loads, where one tenant is moved at a time without touching the
others' rows.

---

## Contents

- [Requirements](#requirements)
- [Build and test](#build-and-test)
- [Project layout](#project-layout)
- [Connections](#connections)
- [Quick start](#quick-start)
- [Command reference](#command-reference)
- [The mapping file](#the-mapping-file)
- [What a transfer run does](#what-a-transfer-run-does)
- [Value conversion](#value-conversion)
- [Write strategies](#write-strategies)
- [Scoped (multi-tenant) loads](#scoped-multi-tenant-loads)
- [The run journal](#the-run-journal)
- [Demo](#demo)
- [Limitations](#limitations)

---

## Requirements

- .NET SDK 10 (`global.json` pins `10.0.100`, rolling forward to the latest feature band)
- Network access to a SQL Server source and a PostgreSQL target
- NuGet packages (restored automatically): `Npgsql`, `Microsoft.Data.SqlClient`

## Build and test

```bash
dotnet build sql_to_pg_migration.slnx
dotnet test  sql_to_pg_migration.slnx           # unit tests, no database needed
dotnet run --project src/SqlToPg -- --help
```

The executable is named `sql_to_pg_migration`. The examples below call it by that name; during
development substitute `dotnet run --project src/SqlToPg --`.

## Project layout

```
src/SqlToPg/                 the tool (assembly: sql_to_pg_migration)
  Cli/                       argument parsing and the sub-commands
  Mapping/                   mapping file model, resolver, audit (preflight), scaffolder
  Schema/                    catalogue snapshots of both databases, foreign-key graph
  Transfer/                  reader, value coercion, COPY writer, scheduler, clear, sequences, reconcile
  Journal/                   run journal and the advisory-lock guard
  Safety/                    identifier quoting, connection settings, target confirmation
tests/SqlToPg.Tests/         unit tests
samples/demo/                small end-to-end demo: source DDL, target DDL, mapping
```

## Connections

Connection strings are read **only from environment variables**, never from the command line, so
passwords don't end up in shell history or process listings.

| Variable           | Purpose |
| ------------------ | ------- |
| `SQLTOPG_MSSQL`    | SQL Server source, e.g. `Server=.\SQLEXPRESS;Database=src;Integrated Security=true;Encrypt=true;TrustServerCertificate=true` |
| `SQLTOPG_PG`       | PostgreSQL target, e.g. `Host=localhost;Database=dst;Username=loader` (Npgsql also reads `pgpass`) |
| `SQLTOPG_OPERATOR` | Name recorded in the journal (default: the OS user) |

## Quick start

```bash
# 1. Draft a mapping from the two catalogues
#    (names are matched ignoring case and underscores)
sql_to_pg_migration scaffold --out map.json --source-schema dbo --target-schema public

# 2. Review map.json: enable/disable tables and resolve the columns listed
#    under unmappedSource / unmappedTarget

# 3. Validate the mapping against both databases and the source data
#    (exits 2 if anything blocks)
sql_to_pg_migration check --map map.json

# 4. Rehearse: audit, load order and duration forecast; nothing is written
sql_to_pg_migration transfer --map map.json --dry-run

# 5. Load
sql_to_pg_migration transfer --map map.json --lanes 4 --confirm-target-db my_target_db

# 6. Re-check row counts at any time, and inspect the journal
sql_to_pg_migration verify  --map map.json
sql_to_pg_migration history
sql_to_pg_migration history --run 12
```

For a multi-tenant load, add `--scope <value>` (and optionally `--claim-blank`) to `check`,
`transfer` and `verify`. See [Scoped loads](#scoped-multi-tenant-loads).

## Command reference

| Command | What it does |
| ------- | ------------ |
| `scaffold`        | Writes a draft mapping file by pairing source and target tables/columns by name. |
| `check`           | Runs the audit only. Exit `2` if any finding blocks. |
| `transfer`        | Audits, clears (in `replace` mode), loads, realigns sequences, analyzes and reconciles. |
| `verify`          | Compares source and target row counts for the mapped tables. |
| `clear`           | Removes exactly what a `replace` run would reload, without loading. |
| `unfreeze-source` | Sets the source database back to `READ_WRITE` after a run died while frozen. |
| `history`         | Lists recent runs from the journal, or one run's tables with `--run <id>`. |

### Options

```
scaffold   --out <map.json> [--source-schema dbo] [--target-schema public]
           [--strip-source-prefix tbl] [--add-target-prefix x_] [--force]

check      --map <map.json> [--tables a,b] [--scope <v> [--claim-blank]] [--strict-scope] [--no-data]

transfer   --map <map.json> [--tables a,b] [--scope <v> [--claim-blank]] [--strict-scope]
           [--mode replace|append] [--strategy direct|holding] [--lanes 4]
           [--dry-run] [--freeze-source] [--time-budget-minutes n] [--rows-per-second n]
           [--scope-setting app.current_tenant] [--journal-schema sql_to_pg_journal]
           [--skip-verify] --confirm-target-db <db>

verify     --map <map.json> [--tables a,b] [--scope <v> [--claim-blank]] [--mode replace|append]

clear      --map <map.json> [--tables a,b] [--scope <v>] [--scope-setting ...] --confirm-target-db <db>

unfreeze-source

history    [--limit 20] [--run <id>] [--journal-schema sql_to_pg_journal]
```

Notable options:

| Option | Meaning |
| ------ | ------- |
| `--tables a,b`            | Restrict the command to these mapped tables. |
| `--mode replace\|append`  | `replace` (default) clears this run's rows first; `append` adds to what is there. |
| `--lanes n`               | Number of tables loaded in parallel (1–64, default 4). |
| `--dry-run`               | Audit, order and forecast only; nothing is written. |
| `--freeze-source`         | Puts the source database in `READ_ONLY` for the duration of the run. |
| `--time-budget-minutes n` | Refuse to start if the forecast exceeds `n`; cancel if the run reaches it. |
| `--rows-per-second n`     | Override the throughput used for the forecast. |
| `--skip-verify`           | Skip the row-count reconciliation at the end of `transfer`. |
| `--confirm-target-db db`  | Required for destructive commands; must equal the target's `current_database()`. |

Options are strict: an unknown or mistyped option (for example `--dry-rn`) is a usage error, never
silently ignored.

### Exit codes

| Code | Meaning |
| ---- | ------- |
| `0`  | OK |
| `1`  | Failed (including a row-count mismatch or cancellation) |
| `2`  | Refused: blocking audit finding, lock held by another run, or over the time budget |
| `64` | Usage or mapping-file error |

Pressing **Ctrl+C** once cancels cleanly: the table in flight is rolled back, the journal is closed
and a frozen source is set back to `READ_WRITE`. Pressing it a second time kills the process.

## The mapping file

JSON with comments and trailing commas allowed.

```jsonc
{
  "sourceSchema": "dbo",                 // default schema for source names without one
  "targetSchema": "public",              // default schema for target names without one
  "sourceTimeZone": "Asia/Kathmandu",    // meaning of a naive datetime written to timestamptz (default UTC)
  "scope": { "target": "tenant_id", "source": "TenantId" },   // optional, see "Scoped loads"

  "tables": [
    {
      "source": "Employee",              // dbo.Employee
      "target": "employee",              // public.employee
      "enabled": true,
      "role": "owned",                   // owned (cleared and reloaded) | shared (filled only while empty)
      "where": "src.[IsDeleted] = 0",    // optional SQL Server filter; the source table is aliased src
      "noScope": false,                  // true: this table has no scope column
      "key": ["tenant_id", "id"],        // optional; defaults to the target primary key
      "onConflict": "fail",              // fail | skip | update (skip/update go through a holding table)
      "columns": [
        { "source": "EmployeeID", "target": "id" },
        { "expression": "LOWER(src.[Email])", "target": "email" },
        { "literal": "sqlserver", "target": "source_system" },
        { "source": "JoinedOn", "target": "joined_on", "timeZone": "UTC" },
        { "source": "Code", "target": "code", "trimEnd": true },    // strip char(n) padding
        { "source": "Notes", "target": "notes", "stripNul": true }  // drop U+0000 instead of failing
      ]
    }
  ]
}
```

Each target column is fed by **exactly one** of:

- `source` — a source column,
- `expression` — any SQL Server expression over `src`,
- `literal` — a fixed value, converted to the target column's type.

`unmappedSource` / `unmappedTarget` are review notes written by `scaffold`; the tool ignores them.

> **Security note:** `where` and `expression` are inserted into the SQL Server query as written.
> Treat mapping files like code and only run ones you trust.

## What a transfer run does

1. **Audit.** Every mapped table and column is checked against both catalogues before anything is
   written. These problems block the run:
   - a missing table or column
   - a generated target column being fed
   - a target type the loader can't write
   - a `NOT NULL` target column without a default that nothing feeds
   - a source/target type pairing that cannot work
   - a literal that doesn't parse as its target type
   - `onConflict: update` with no key
   - source rows that are `NULL` where the target is `NOT NULL` (counted, with the first 10 keys printed)

   Warnings (a narrowing, text parsed as a number) and notes are printed but don't stop the run.
2. **Order and forecast.** Tables are ordered parents-first using the **target's own foreign keys**
   (from `pg_constraint`), not the mapping. Tables in a foreign-key cycle are loaded last, one at a
   time. Duration is forecast from row counts and a rate taken from `--rows-per-second`, else the
   median of earlier runs in the journal, else 40k rows/s.
3. **Guard.** Destructive commands require `--confirm-target-db`. Advisory locks prevent
   overlapping runs: a whole-database run excludes everything, while runs for different scopes may
   run side by side. The journal run is opened and holding tables left by killed runs are dropped.
4. **Freeze** (`--freeze-source`). Runs `ALTER DATABASE … SET READ_ONLY WITH ROLLBACK IMMEDIATE`
   and restores `READ_WRITE` on every exit path. If that restore fails the run says so loudly; use
   `unfreeze-source`.
5. **Clear** (`--mode replace`, the default). Unscoped tables are emptied with a single `TRUNCATE`.
   Scoped tables have only this scope's rows deleted, children before parents. Shared tables are
   never cleared. Running the same command twice therefore ends in the same state.
6. **Load.** Up to `--lanes` tables at a time; a table starts only after all its parents have
   committed. Within a table, a reader converts rows from SQL Server while binary `COPY` writes
   earlier batches through a bounded channel, so memory stays flat. Each table is one transaction,
   so a failed table commits nothing. Transient failures (dropped connection, timeout, deadlock or
   serialization victim) are retried up to 3 times on a fresh connection; the first real failure
   cancels the other lanes.
7. **Sequences and statistics.** Every sequence behind a loaded `serial`/identity column is moved
   past the highest loaded value (only ever raised, never lowered), then `ANALYZE` runs.
8. **Reconcile.** Row counts are compared with the source for the same scope and filter. Any
   difference fails the run with exit `1`.

## Value conversion

The wire type for each column comes from the **target's declared type**, never from the mapping.
Binary `COPY` is strict about widths, and a wrong width fails with a protocol error that doesn't
name the column, so the conversion is decided up front, once per column per table.

| Target type | Accepted from SQL Server |
| ----------- | ------------------------ |
| `smallint`, `integer`, `bigint` | integer types, `bit`, numeric text (range-checked) |
| `boolean` | `bit`, integers, `1/0/true/false/y/n/yes/no` |
| `numeric`, `money` | `decimal`, `money`, `float`, integers, numeric text |
| `timestamp` | `datetime*`, stored as-is (never shifted) |
| `timestamptz` | `datetimeoffset` (its own offset); naive `datetime` interpreted in `timeZone` / `sourceTimeZone` |
| `date`, `time`, `interval` | `date` / `datetime`, `time` |
| `uuid` | `uniqueidentifier`, GUID text |
| `bytea` | binary only (anything else means the mapping is wrong) |
| `text`, `varchar`, `char`, `citext`, `json`, `jsonb`, `xml` | any textual value; dates are written as ISO, binary is refused |

When a value can't be converted, the table fails with a message naming the table, the column and the
row's **key**. The value itself is never printed, since it may be personal data. A NUL character
(`\0`) in text fails the same way because PostgreSQL can't store it; set `stripNul` to drop it.

`Npgsql.DisableDateTimeInfinityConversions` is enabled, so SQL Server's `0001-01-01` and
`9999-12-31` stay real dates rather than becoming `-infinity` / `infinity`.

## Write strategies

- **`--strategy direct`** (default): binary `COPY` straight into the target table inside one
  transaction. Fastest, and `COPY` keeps explicit ids even in `GENERATED ALWAYS` identity columns.
- **`--strategy holding`**: binary `COPY` into an unlogged holding table in the journal schema, then
  `INSERT … OVERRIDING SYSTEM VALUE SELECT …` into the target. Use this when the target has
  **row-level security**, because PostgreSQL refuses `COPY` into an RLS table for roles that don't
  bypass it. With `--scope-setting app.current_tenant`, the scope value is bound with
  `set_config(…, true)` before the insert so the policy checks every row.

Tables with `onConflict` set always go through a holding table. Holding tables are dropped in a
`finally` block, and any left by a killed run are swept at the start of the next one.

## Scoped (multi-tenant) loads

Set `"scope"` in the mapping and pass `--scope <value>` to move one tenant (or any partition) at a
time. Other tenants' rows in the target are never touched.

- **Shared source database** (`scope.source` set): only rows where `src.<scope.source> = @scope` are
  read. The comparison uses the column's own type, so an index on it can be used.
  - `--claim-blank` also takes rows whose scope is `NULL`, blank or the all-zero GUID, and writes
    the scope value into them.
  - `check` reports how many rows belong to other scopes; with `--strict-scope` any such row blocks
    the run (the "one database per tenant" assertion).
- **One database per tenant** (`scope.source` omitted): every row is read and the scope value is
  written into it.
- The target scope column is never copied from the source; it is always filled from `--scope`.

## The run journal

`transfer` and `clear` create the schema `sql_to_pg_journal` in the target if it doesn't exist
(override with `--journal-schema`):

- **`transfer_run`** — one row per run: operator, command, scope, mapping file name and SHA-256,
  mode, status (`running` / `succeeded` / `failed` / `refused`) and a note.
- **`transfer_table`** — one row per table per run: source and written row counts, timings, status
  and error.

A run row is closed on every exit path. A row still `running` with no live process means the
process died; its table rows show how far it got.

## Demo

`samples/demo/` contains a small source database, a matching target schema and a mapping.

```bash
sqlcmd -S .\SQLEXPRESS -E -i samples/demo/01_source_sqlserver.sql
createdb demo_dst && psql -d demo_dst -f samples/demo/02_target_postgres.sql

export SQLTOPG_MSSQL='Server=.\SQLEXPRESS;Database=demo_src;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
export SQLTOPG_PG='Host=localhost;Database=demo_dst;Username=<you>'

dotnet run --project src/SqlToPg -- check    --map samples/demo/demo.map.json --scope 11111111-1111-1111-1111-111111111111 --claim-blank
dotnet run --project src/SqlToPg -- transfer --map samples/demo/demo.map.json --scope 11111111-1111-1111-1111-111111111111 --claim-blank --confirm-target-db demo_dst
dotnet run --project src/SqlToPg -- transfer --map samples/demo/demo.map.json --scope 22222222-2222-2222-2222-222222222222 --confirm-target-db demo_dst
dotnet run --project src/SqlToPg -- history
```

The demo exercises:

- a shared reference table
- a `GENERATED ALWAYS` identity column whose ids are preserved
- a generated column that is left unmapped
- an expression, a literal and a `where` filter
- `money → numeric`, naive `datetime → timestamptz` (Asia/Kathmandu), `datetimeoffset`, `varbinary → bytea`
- a blank-tenant row claimed by the first tenant
- a second tenant loaded alongside the first without touching it

## Limitations

- PostgreSQL array, range and geometric types are not supported; the audit blocks them.
- SQL Server `decimal(38)` values beyond the range of .NET `decimal` can't be read directly; cast
  them in an `expression`.
- `where` and `expression` are raw SQL Server text; only use trusted mapping files.

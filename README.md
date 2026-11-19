# sqlserver-to-pg-migration

Two independent approaches to moving data from SQL Server to PostgreSQL.

| Folder | What it is |
|---|---|
| [`pgloader_migration/`](pgloader_migration/README.md) | Python + pgloader workflow: generates per-tenant pgloader configs from YAML and runs auditable iterations. |
| [`sql_to_pg_migration/`](sql_to_pg_migration/README.md) | .NET 10 CLI: maps SQL Server tables/columns onto an existing PostgreSQL schema and bulk-loads the data, with a run journal in the target DB. |

Each folder is self-contained; see its README for setup and usage.

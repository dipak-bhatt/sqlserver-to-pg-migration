# sqlserver-to-pg-migration

Two independent approaches to moving data from SQL Server to PostgreSQL.

| Folder | What it is |
|---|---|
| [`pgloader_migration/`](pgloader_migration/README.md) | Python + pgloader workflow: generates per-tenant pgloader configs from YAML and runs auditable iterations. |
| [`sql_to_pg_migration/`](sql_to_pg_migration/README.md) | .NET 10 CLI: maps SQL Server tables/columns onto an existing PostgreSQL schema and bulk-loads the data, with a run journal in the target DB. |

Each folder is self-contained; see its README for setup and usage.

## Combining tenant databases into one

If each tenant (customer) has its own database, you can use these tools to copy all of their data into one shared target database. You tell the tool which source tables and columns go to which target tables, and it loads each tenant's data into the shared database, one tenant at a time.

### Why do this?

- **One place for reports:** Build reports and dashboards for all tenants from a single database.
- **Simpler data warehouse:** Keep one database for business intelligence and history instead of many.
- **Compare tenants:** See trends, usage, and performance side by side across tenants.
- **Less to manage:** No need to query many databases to get the full picture.
- **Easier tool setup:** Connect reporting, BI, and data tools to one database instead of one per tenant.
- **Consistent data:** Apply the same schema and cleanup rules to every tenant's data as it is loaded.
- **Protects live systems:** Heavy reporting queries run on the shared database, not on the tenants' production databases.
- **Easier backups and archiving:** Keep and manage all tenants' historical data in one place.

# pg-migration – SQL Server to PostgreSQL Migration

This repository orchestrates iterative, multi-tenant migrations from SQL Server to PostgreSQL with pgloader and Jinja2 templating.

## **Prerequisites**

- **Python 3.9+**
- **pgloader** - Database migration tool
  - Ubuntu/Debian: `apt-get install pgloader`
- **Python packages** (install with uv or pip):
  ```bash
  uv sync
  # Or: pip3 install pyyaml jinja2 psycopg2-binary
  ```

## **Configuration**

### 1. **Credentials**

Copy `credentials_dev.ini.example` to `credentials_dev.ini` (git-ignored) and fill in your database connection details:

```ini
[mssql]
sql_host = <sql-server-host>
sql_user = <sql-user>
sql_pass = <sql-password>

[postgres]
pg_host = <pg-host>
pg_port = 5432
pg_user = <pg-user>
pg_pass = <pg-password>
target_db = <target-database>
```

### 2. **Tenants**

Edit `tenants/tenants_<env>.yaml` to define tenants and connection details:

```yaml
pg_host: "localhost"
pg_port: "5433"
pg_database: "828"
sql_host: "10.0.0.1"
sql_port: "1433"

tenants:
  - tenant_id: "uuid-here"
    slug: "dev"
```

### 3. **Tables**

Define tables to migrate in `tables/<iteration>_tables.yaml`:

```yaml
tables:
  - name: table1
  - name: table2
    rename: table2_renamed
```

## **Pre-Migration Setup**

Before starting the migration, perform the following tasks in the target PostgreSQL database:

### 1. Create Target Table Schemas

Create table schemas in the target PostgreSQL database for each table you want to migrate. Table names can differ from source table names, but **column names must be in lowercase** (not in snake case).

**SQL Server:**
```sql
create table dbo.source_employee
(
    EmployeeId   int identity
        primary key,
    TenantId     uniqueidentifier not null,
    EmployeeCode varchar(20)      not null
        unique,
    FirstName    varchar(50)      not null,
    LastName     varchar(50)      not null,
    Email        varchar(100),
    Phone        varchar(20),
    Designation  varchar(100),
    Salary       decimal(18, 2),
    BranchId     int              not null
        constraint FK_Employee_Branch
            references dbo.source_branch,
    HireDate     date             not null
)
go
```

**PostgreSQL:**
```sql

create table employee
(
    tenantid     uuid        not null,
    employeeid   bigint      not null,
    employeecode varchar(20) not null,
    firstname    varchar(50) not null,
    lastname     varchar(50) not null,
    email        varchar(100),
    phone        varchar(20),
    designation  varchar(100),
    salary       numeric(18, 2),
    branchid     bigint      not null,
    hiredate     date        not null,
    constraint pkemployee
        primary key (tenantid, employeeid),
    constraint uqemployeecode
        unique (tenantid, employeecode),
    constraint fkemployeebranch
        foreign key (tenantid, branchid) references branch
);

alter table employee
    owner to postgres;
```

### 2. Enable UUID Extension

Execute the following in the PostgreSQL database:

```sql
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
```

### 3. Create Migration Log Table and Function

Create the migration log table and the `merge_and_log_migration` function for post-processing migrated data:

```sql
create table migration_log
(
    id                      serial
        primary key,
    tenant_id               varchar(255) not null,
    tenant_slug             varchar(100) not null,
    table_name              varchar(255) not null,
    iteration               varchar(100) not null,
    migration_date          timestamp with time zone default now(),
    source_row_count        integer,
    target_row_count_before integer,
    target_row_count_after  integer,
    rows_inserted           integer,
    status                  varchar(50)              default 'success'::character varying,
    error_message           text,
    constraint unique_migration_run
        unique (tenant_id, table_name, iteration, migration_date)
);

alter table migration_log
    owner to postgres;

create index idx_migration_log_date
    on migration_log (migration_date desc);

create index idx_migration_log_iteration
    on migration_log (iteration);

create index idx_migration_log_slug
    on migration_log (tenant_slug);

create index idx_migration_log_tenant
    on migration_log (tenant_id);

create function merge_and_log_migration(p_tenant_id character varying, p_tenant_slug character varying, p_table_name character varying, p_temp_table character varying, p_iteration character varying) returns void
    language plpgsql
as
$$
DECLARE
    v_source_count INTEGER;
    v_target_before INTEGER;
    v_target_after INTEGER;
    v_columns TEXT;
    v_casted_columns TEXT;
BEGIN
    -- Get counts
    EXECUTE format('SELECT COUNT(*) FROM public.%I', p_temp_table) INTO v_source_count;
    EXECUTE format('SELECT COUNT(*) FROM public.%I', p_table_name) INTO v_target_before;

    -- Build column list with proper casting for timestamp columns
    SELECT
        string_agg(quote_ident(column_name), ', ' ORDER BY ordinal_position),
        string_agg(
            CASE
                WHEN data_type IN ('timestamp without time zone', 'timestamp with time zone')
                THEN quote_ident(column_name) || '::timestamptz'
                ELSE quote_ident(column_name)
            END,
            ', ' ORDER BY ordinal_position
        )
    INTO v_columns, v_casted_columns
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = p_table_name;

    -- Merge data with proper type casting
    EXECUTE format(
        'INSERT INTO public.%I (%s) SELECT %s FROM public.%I ON CONFLICT DO NOTHING',
        p_table_name, v_columns, v_casted_columns, p_temp_table
    );

    EXECUTE format('SELECT COUNT(*) FROM public.%I', p_table_name) INTO v_target_after;

    -- Log the migration
    INSERT INTO public.migration_log (
        tenant_id, tenant_slug, table_name, iteration,
        source_row_count, target_row_count_before, target_row_count_after, rows_inserted
    ) VALUES (
        p_tenant_id, p_tenant_slug, p_table_name, p_iteration,
        v_source_count, v_target_before, v_target_after, (v_target_after - v_target_before)
    );

    -- Drop temp table with CASCADE to handle foreign key dependencies
    EXECUTE format('DROP TABLE public.%I CASCADE', p_temp_table);
END;
$$;

alter function merge_and_log_migration(varchar, varchar, varchar, varchar, varchar) owner to postgres;
```

## **Dev Workflow**

Run migrations locally or from the Docker container:

```bash
# 1. Generate pgloader configs
python3 scripts/generate_pgloader.py dev iteration_01

# 2. Run migration
python3 scripts/run_dev_migration.py iteration_01 <sql_database_name>
```

## **Docker**

```bash
# Build image
docker build -t pg-migration .

# Run container
docker run -it --rm \
  -v $(pwd):/workspace \
  pg-migration

# Inside container:
python3 scripts/generate_pgloader.py dev iteration_01
python3 scripts/run_dev_migration.py iteration_01 <sql_database_name>
```

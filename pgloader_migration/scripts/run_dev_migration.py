#!/usr/bin/env python3
import os
import sys
import subprocess
import configparser
import yaml
from urllib.parse import quote_plus

def main():
    if len(sys.argv) < 2:
        print("Usage: python3 scripts/run_dev_migration.py <iteration> [sql_database]")
        print("Example: python3 scripts/run_dev_migration.py iteration_01 source-sql-server-db")
        sys.exit(1)

    iteration = sys.argv[1]
    
    # 1. Load credentials_dev.ini
    config_ini = configparser.ConfigParser()
    ini_path = 'credentials_dev.ini'
    if not os.path.exists(ini_path):
        print(f"Error: {ini_path} not found in the current directory.")
        sys.exit(1)
    config_ini.read(ini_path)
    
    # 2. Get connection details from tenants_dev.yaml
    yaml_path = 'tenants/tenants_dev.yaml'
    if not os.path.exists(yaml_path):
        print(f"Error: {yaml_path} not found.")
        sys.exit(1)
    with open(yaml_path) as f:
        yaml_config = yaml.safe_load(f)
    
    # Parse tenants
    tenants = yaml_config.get('tenants', [])
    if not tenants:
        print("Error: No tenants defined in tenants_dev.yaml")
        sys.exit(1)
    
    tenant = tenants[0]
    tenant_slug = tenant.get('slug', 'dev')
    tenant_id = tenant.get('tenant_id')
    
    # Get sql_database: 1st check arg, 2nd check yaml, 3rd prompt
    sql_database = sys.argv[2] if len(sys.argv) > 2 else tenant.get('sql_database')
    if not sql_database:
        sql_database = input(f"Enter source SQL Server database name (default: source-sqlserver-{tenant_slug}): ").strip()
        if not sql_database:
            sql_database = f"source-sqlserver-{tenant_slug}"

    # Extract credentials from INI
    try:
        sql_host = config_ini.get('mssql', 'sql_host')
        sql_user = config_ini.get('mssql', 'sql_user')
        sql_pass = config_ini.get('mssql', 'sql_pass')
        
        pg_host = config_ini.get('postgres', 'pg_host')
        pg_user = config_ini.get('postgres', 'pg_user')
        pg_pass = config_ini.get('postgres', 'pg_pass')
        pg_database = config_ini.get('postgres', 'target_db')
        pg_port = config_ini.get('postgres', 'pg_port', fallback='5432')
    except (configparser.NoSectionError, configparser.NoOptionError) as e:
        print(f"Error parsing credentials_dev.ini: {e}")
        sys.exit(1)
        
    # Auto-detect Docker environment and fix localhost mapping
    if os.path.exists('/.dockerenv') and pg_host in ['localhost', '127.0.0.1']:
        print(f"\n[Docker Network Redirect] Detected running inside a Docker container.")
        print(f"Automatically mapping PG host '{pg_host}' to 'host.docker.internal' so we can connect to your host's PostgreSQL.\n")
        pg_host = 'host.docker.internal'
    
    # 3. Read the conf template
    conf_path = f'configs/dev/{iteration}_dev_pgloader.conf'
    if not os.path.exists(conf_path):
        print(f"Error: {conf_path} not found. Please generate it first using generate_pgloader.py.")
        sys.exit(1)
        
    with open(conf_path, 'r') as f:
        content = f.read()
        
    # Replace placeholders
    content = content.replace('{{SQL_USER}}', sql_user)
    content = content.replace('{{SQL_PASS}}', sql_pass)
    content = content.replace('{{SQL_HOST}}', sql_host)
    content = content.replace('{{SQL_DATABASE}}', sql_database)
    
    content = content.replace('{{PG_USER}}', pg_user)
    content = content.replace('{{PG_PASS}}', pg_pass)
    
    # Append port to host for pgloader if it's not default 5432
    pg_host_with_port = f"{pg_host}:{pg_port}" if pg_port and pg_port != '5432' else pg_host
    content = content.replace('{{PG_HOST}}', pg_host_with_port)
    content = content.replace('{{PG_DATABASE}}', pg_database)
    
    # Write temp config
    temp_conf_path = conf_path + '.temp'
    with open(temp_conf_path, 'w') as f:
        f.write(content)
        
    print(f"Created temp config with credentials: {temp_conf_path}")
    
    # 4. Run pgloader
    print(f"Running pgloader on {temp_conf_path}...")
    try:
        subprocess.run(['pgloader', '--no-ssl-cert-verification', temp_conf_path], check=True)
        print("pgloader execution complete.")
    except subprocess.CalledProcessError as e:
        print(f"Error: pgloader failed: {e}")
        sys.exit(1)
    finally:
        if os.path.exists(temp_conf_path):
            os.remove(temp_conf_path)

    # 5. Run Merge Function in Postgres
    import psycopg2
    print("Connecting to PostgreSQL to perform data merge...")
    try:
        conn = psycopg2.connect(
            host=pg_host,
            port=pg_port,
            user=pg_user,
            password=pg_pass,
            database=pg_database
        )
        cur = conn.cursor()
        
        # Load tables list in the defined order
        tables_yaml_path = f'tables/{iteration}_tables.yaml'
        if not os.path.exists(tables_yaml_path):
            print(f"Error: {tables_yaml_path} not found.")
            sys.exit(1)
        with open(tables_yaml_path) as f:
            tables_config = yaml.safe_load(f)
        tables = tables_config.get('tables', [])
        
        # Filter for temp tables that actually exist in public schema
        temp_tables_to_merge = []
        for tbl in tables:
            source_name = tbl.get('name')
            target_name = tbl.get('rename', source_name)
            temp_name = f"{target_name}_temp_{tenant_slug}"
            cur.execute(
                "SELECT EXISTS(SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = %s)",
                (temp_name,)
            )
            exists = cur.fetchone()[0]
            if exists:
                temp_tables_to_merge.append((temp_name, target_name))
        
        if not temp_tables_to_merge:
            print("No temp tables found in public schema to merge.")
            sys.exit(0)
            
        print(f"Found {len(temp_tables_to_merge)} temp tables to merge in defined configuration order.")
        for temp_table, target_table in temp_tables_to_merge:
            print(f"Merging {temp_table} into {target_table}...")
            cur.execute(
                "SELECT public.merge_and_log_migration(%s, %s, %s, %s, %s)",
                (tenant_id, tenant_slug, target_table, temp_table, iteration)
            )
            res = cur.fetchone()
            print(f"  Merge Result: {res[0] if res else 'Unknown'}")
            
        conn.commit()
        cur.close()
        conn.close()
        print("All tables merged and logged successfully!")
    except Exception as e:
        print(f"Error during Postgres merge operations: {e}")
        sys.exit(1)

if __name__ == '__main__':
    main()

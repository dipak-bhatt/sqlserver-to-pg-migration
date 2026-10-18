import yaml
from jinja2 import Environment, FileSystemLoader
import sys
import os

def main():
    env_name = sys.argv[1] if len(sys.argv) > 1 else "staging"
    iteration = sys.argv[2] if len(sys.argv) > 2 else "iteration_01"
    # optional filters: --filter-table <name> or --filter-tables name1,name2
    filter_table = None
    filter_tables = None
    args = sys.argv[3:]
    for i in range(len(args)):
        if args[i] == '--filter-table' and i + 1 < len(args):
            filter_table = args[i+1]
        if args[i] == '--filter-tables' and i + 1 < len(args):
            filter_tables = [t.strip() for t in args[i+1].split(',') if t.strip()]

    # Use script's directory as base to allow running from anywhere
    script_dir = os.path.abspath(os.path.dirname(__file__))
    root_dir = os.path.dirname(script_dir)

    # Create environment-specific iterations directory if it doesn't exist
    iterations_dir = os.path.join(root_dir, 'iterations', env_name)
    os.makedirs(iterations_dir, exist_ok=True)
    lock_file = os.path.join(iterations_dir, f'{iteration}.locked')
    if os.path.exists(lock_file):
        print(f"Iteration {iteration} for environment {env_name} is marked complete (locked). Aborting.")
        sys.exit(1)
    
    # Load Jinja2 template
    try:
        env = Environment(
            loader=FileSystemLoader(os.path.join(root_dir, 'templates')),
            trim_blocks=True,
            lstrip_blocks=True
        )
        template = env.get_template('pgloader.conf.j2')
    except Exception as e:
        print(f"Error loading template: {e}")
        sys.exit(1)

    # Load tenant configuration
    tenants_file = os.path.join(root_dir, f'tenants/tenants_{env_name}.yaml')
    try:
        with open(tenants_file) as f:
            config = yaml.safe_load(f)
    except FileNotFoundError:
        print(f"Error: Tenant configuration not found at {tenants_file}")
        sys.exit(1)
    except yaml.YAMLError as e:
        print(f"Error parsing YAML file: {e}")
        sys.exit(1)
    
    # Validate configuration
    required_keys = ['tenants']
    missing_keys = [key for key in required_keys if key not in config]
    if missing_keys:
        print(f"Error: Missing required keys in {tenants_file}: {', '.join(missing_keys)}")
        sys.exit(1)

    tenants = config.get('tenants', [])
    if not tenants:
        print(f"Error: No tenants defined in {tenants_file}")
        sys.exit(1)

    # Validate required connection details are in YAML
    required_keys = ['pg_host', 'pg_database', 'sql_host']
    missing_keys = [key for key in required_keys if key not in config]
    if missing_keys:
        print(f"Error: Missing required keys in {tenants_file}: {', '.join(missing_keys)}")
        sys.exit(1)

    # Load table list for iteration (YAML format)
    tables_file = os.path.join(root_dir, f'tables/{iteration}_tables.yaml')
    try:
        with open(tables_file) as f:
            tables_yaml = yaml.safe_load(f)
            tables = tables_yaml.get('tables', [])

    except FileNotFoundError:
        print(f"Error: Table YAML not found at {tables_file}")
        print(f"Please create {iteration}_tables.yaml in the tables/ directory")
        sys.exit(1)
    except yaml.YAMLError as e:
        print(f"Error parsing YAML file: {e}")
        sys.exit(1)
    if not tables:
        print(f"Warning: No tables found in {tables_file}")
        sys.exit(1)

    # Create environment-specific configs directory
    configs_dir = os.path.join(root_dir, 'configs', env_name)
    os.makedirs(configs_dir, exist_ok=True)

    # Generate config for each tenant
    generated_count = 0
    
    for tenant in tenants:
        slug = tenant.get('slug')
        tid = tenant.get('tenant_id')
        if not slug or not tid:
            print(f"Warning: Skipping tenant missing 'slug' or 'tenant_id': {tenant}")
            continue

        # Format SQL Server DB name with environment
        # No need to calculate tenant_db here - use placeholder

        try:
            # Apply optional filters
            filtered_tables = []
            for tbl in tables:
                name = tbl.get('name')
                if filter_table and name != filter_table:
                    continue
                if filter_tables and name not in filter_tables:
                    continue
                tbl_copy = dict(tbl)
                if 'only' not in tbl_copy:
                    tbl_copy['only'] = True
                filtered_tables.append(tbl_copy)

            render = template.render(
                # These will be replaced at runtime by run_pgloader.sh
                SQL_USER="{{SQL_USER}}",
                SQL_PASS="{{SQL_PASS}}",
                SQL_HOST="{{SQL_HOST}}",
                SQL_DATABASE="{{SQL_DATABASE}}",
                PG_USER="{{PG_USER}}",
                PG_PASS="{{PG_PASS}}",
                PG_HOST="{{PG_HOST}}",
                PG_DATABASE="{{PG_DATABASE}}",
                # Static tenant identifiers
                tenant_id=tid,
                tenant_slug=slug,
                tables=filtered_tables,
                iteration=iteration
            )
            # Remove excessive blank lines (more than 2 consecutive newlines)
            import re
            cleaned = re.sub(r'\n{3,}', '\n\n', render)
            fname = os.path.join(configs_dir, f'{iteration}_{slug}_pgloader.conf')
            with open(fname, 'w') as out:
                out.write(cleaned)
            print(f"Generated: {fname}")
            generated_count += 1
        except Exception as e:
            print(f"Error generating config for tenant {slug}: {e}")
            continue
    
    print(f"\nSuccessfully generated {generated_count} pgloader configuration(s) for {iteration}")

if __name__ == "__main__":
    main()
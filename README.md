# Example Schema

Example schema version tracking for a database table using DbUp library.

## Template

This is a template project, which means are a few special considerations:

* An applied application of this template would order its update scripts in accordance with the guideline set out in
  the [File Organization and Maintenance](#file-organization-and-maintenance) section of this README. However, rather
  than document the development of the template and apply incremental updates as would be intended for an applied
  project, this template maintains a single file: `Scripts/0000/0000-01/0000-00-00-00-example.sql`. The data within the
  template tables while it is acting as a template are assumed to be entirely disposable.
* The tables declared in this template are in service of the API template
  at [RedShirt.Example.Api](https://github.com/adeutscher/RedShirt.Example.Api).

### Initialization

When initializing this template:

1. Run `init-repo.sh` to assign a new C# namespace and label (the label is the prefix associated with environment
   variables, such as `EXAMPLE_SCHEMA_HOST`).

    ```bash
   ./init-repo.sh "Malamute.Schema" PROJECT_MALAMUTE
    ```
    * After this point, `init-repo.sh` can be safely deleted.

2. This template was written with MariaDB/MySQL in mind. If you are not planning to use a MySQL-compatible server when
   applying this template, then please consider the following:
    * Consider switching to another DbUp package appropriate to your choice in database technology, such as:
        * `dbup-sqlserver`
        * `dbup-postgresql`
        * `dbup-sqlite`
        * `dbup-oracle`
        * Or others (my goodness, there are a lot of available DbUp packages)
    * If you switched DbUp packages, then you will also need to adjust the `MySqlDatabase` call in `SchemaUpgrader.cs`.
    * Consider updating the connection string defined in `Program.cs`.
    * If the intended type of server does not prefer using files with a `.sql` extension, then you should adjust the
      `EmbeddedResource` pattern in `RedShirt.Example.Schema.csproj`
    * Integration tests will need to be updated/replaced.

3. The `local-update.sh` script was made as a convenience button for local applies to a disposable database running on
   your local machine. If you would rather set up your environment variables in a different way, then you may wish to
   simply delete `local-update.sh` altogether. If you choose to keep `local-update.sh`, then please note that the
   username "root" is hard-coded in, which may need changing if your chosen database technology does not use root (e.g.
   SQL Server prefers "sa").

4. Once you have initialized this template, you will want to remove this [Template](#template) section from this README.

## File Organization and Maintenance

The DbUp library has the following behaviour:

* It shall iterate through the `.sql` files in the `Scripts/` directory of the `RedShirt.Example.Schema` project, which
  represent incremental updates to the schema.
* It shall consult a journal table (hard-coded as `Patches` in `SchemaUpgrader.cs`) to verify `.sql` files that have
  already been applied.

In order to ensure that updates are executed in the intended order and to avoid one large directory, the following
guidelines are strongly advised:

* Create a date-based directory structure to encourage numerical ordering.
* On top of the top level, the creation of subdirectories within that is encouraged.

For example, a script to create a `Records` table on September 1, 2026 (the second script to be committed that day)
might be placed at `Scripts/2026/2026-09/2026-09-01-02-create-records-table.sql`

The DbUp library's incremental updates means that applied scripts should never be updated (especially scripts that have
been established in the default branch). Editing an already-applied file will not re-run on existing DBs and will desync
environments.

## Development

To write initial statements for adjusting tables, it is encouraged to use a database tool such as DBeaver to output
proposed changes and then applying them to `.sql` update files in this project.

Integration tests under `test/RedShirt.Example.Schema.IntegrationTests` use Testcontainers and require a working Docker
daemon (they spin up a MariaDB container to apply and verify schema scripts). Run them with:

```bash
dotnet test
```

## Execution

Instructions on how to apply schema updates.

The target database must already exist before running `update.sh` or `local-update.sh`. These scripts apply schema
patches to an existing database; they do not create the database itself.

### Deployed Database

#### Setup

Set the following necessary environment variables in your `~/.bashrc` file:

* `EXAMPLE_SCHEMA_HOST`: Address of your SQL server
* `EXAMPLE_SCHEMA_NAME`: Name of your schema
* `EXAMPLE_SCHEMA_USER`: Username with which to access the target schema. The user must have access to update the
  schema.
* `EXAMPLE_SCHEMA_PASSWORD`: User password to access the target schema

Example:

```bash
export EXAMPLE_SCHEMA_HOST="127.0.0.1"
export EXAMPLE_SCHEMA_NAME="example"
export EXAMPLE_SCHEMA_USER="db-user"
export EXAMPLE_SCHEMA_PASSWORD="redacted"
```

Reload your `~/.bashrc` file:

```bash
. ~/.bashrc
```

#### Application

To deploy updates, use the `update.sh` shorthand script:

```bash
./update.sh
```

### Local Database

#### Setup

Ensure that the `LOCAL_SQL_PASSWORD` environment variable is set in your environment (for example in `~/.bashrc`). This
is the password for the local SQL server. The `local-update.sh` script maps it to `EXAMPLE_SCHEMA_PASSWORD` and supplies
local connection defaults:

* `EXAMPLE_SCHEMA_HOST`: `127.0.0.1`
* `EXAMPLE_SCHEMA_NAME`: `example`
* `EXAMPLE_SCHEMA_USER`: `root`

Example:

```bash
export LOCAL_SQL_PASSWORD="redacted"
```

Reload your `~/.bashrc` file if you added the variable there:

```bash
. ~/.bashrc
```

#### Application

To deploy updates against the local SQL server, use the `local-update.sh` shorthand script:

```bash
./local-update.sh
```

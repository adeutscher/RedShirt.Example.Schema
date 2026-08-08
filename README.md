# Example Schema

Example schema version tracking for a database table using DbUp library.

## Template

This is a template project, which means are a few special considerations.

An applied application of this template would order its update scripts in accordance with
the [File Organization and Maintenance](#file-organization-and-maintenance) section of this README. However, rather
than document the development of the template and apply incremental updates, this template maintains a single file:
`Scripts/0000/0000-01/0000-00-00-00-example.sql`. The data within the template tables while it is acting as a template
are assumed to be entirely disposable.

## File Organization and Maintenance

The DbUp library has the following behaviour:

* It shall iterate through the `.sql` files in the `Scripts/` directory of the `RedShirt.Example.Schema` project, which
  represent incremental updates to the schema.
* It shall consult a journal table to verify `.sql` files that have already been applied.

In order to ensure that updates are executed in the intended order and to avoid one large directory, the following
guidelines are strongly advised:

* Create a date-based directory structure to encourage numerical ordering.
* On top of the top level, the creation of subdirectories within that is encouraged.

For example, a script to create a `Records` table on September 1, 2026 (the second script to be committed that day)
might be placed at `Scripts/2026/2026-09/2026-09-01-02-create-records-table.sql`

## Execution

Instructions on how to apply schema updates.

### Deployed Database

#### Setup

Set the following necessary environment variables in your `~/.bashrc` file:

* `EXAMPLE_SCHEMA_HOST`: Address of your SQL server
* `EXAMPLE_SCHEMA_NAME`: Name of your realm schema
* `EXAMPLE_SCHEMA_USER`: Username with which to access the target schema. The user must have access to update the
  schema.
* `EXAMPLE_SCHEMA_PASSWORD`: User password to access the target schema

Example:

```bash
export EXAMPLE_SCHEMA_HOST="127.0.0.1"
export EXAMPLE_SCHEMA_NAME="realm"
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
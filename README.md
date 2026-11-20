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
    * The current namespace and label are detected from the repository, so the script can be run again later to move
      to another namespace and/or label. It can also be safely deleted once you are settled.

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
   username defaults to "root" (overridable with `LOCAL_SQL_USER`), which may need changing if your chosen database
   technology does not use root (e.g. SQL Server prefers "sa").

4. `scripts/cicd/check-scripts.sh` exempts `Scripts/0000/` from its naming check, and from its immutability check
   while `template_mode=1`, so that the example script can be edited while this repository is a template. The first
   run of `init-repo.sh` sets `template_mode=0`, after which the seed script is treated as applied and must not be
   edited. Later runs of `init-repo.sh` leave the seed script's name alone for the same reason. If you need to change
   the seed script before it has been applied anywhere, set `template_mode=1` again for that change.

5. Once you have initialized this template, you will want to remove this [Template](#template) section from this README.

## File Organization and Maintenance

The DbUp library has the following behaviour:

* It shall iterate through the `.sql` files in the `Scripts/` directory of the `RedShirt.Example.Schema` project, which
  represent incremental updates to the schema.
* It shall consult a journal table (hard-coded as `Patches` in `SchemaUpgrader.cs`) to verify `.sql` files that have
  already been applied. The journal is kept in its own schema, `<name>_changes` by default (see
  [Journal Schema](#journal-schema)).

In order to ensure that updates are executed in the intended order and to avoid one large directory, the following
guidelines are strongly advised:

* Create a date-based directory structure to encourage numerical ordering.
* On top of the top level, the creation of subdirectories within that is encouraged.

For example, a script to create a `Records` table on September 1, 2026 (the second script to be committed that day)
might be placed at `Scripts/2026/2026-09/2026-09-01-02-create-records-table.sql`

The DbUp library's incremental updates means that applied scripts should never be updated (especially scripts that have
been established in the default branch). Editing an already-applied file will not re-run on existing DBs and will desync
environments.

Both rules are enforced by `scripts/cicd/check-scripts.sh`, which runs as part of `scripts/cicd/lint.sh`:

* Every file under `Scripts/` must be a `.sql` file named `Scripts/YYYY/YYYY-MM/YYYY-MM-DD-NN-lowercase-name.sql`, with
  the directory year and month matching the file name.
* Scripts that already exist on the base branch must not be modified, renamed, or deleted. On pull requests the base is
  the pull request's target branch. Locally it defaults to `origin/develop` and can be changed with `BASE_REF`.

See [Update Script Lint](docs/update-script-lint.md) for how the checks work and what they do not catch.

The upgrader also checks this at runtime. DbUp journals scripts by name only, so the upgrader stores a SHA-256 checksum
of each applied script in a `PatchChecksums` table next to the journal, written in the same transaction as the journal
entry. Before
applying anything, an upgrade or dry run compares every applied script with its checksum and stops without changing
the database if one was edited, or if a journaled script is no longer embedded (renamed or deleted). Checksums ignore
line endings, so a Windows checkout matches a Linux one. Scripts applied before checksums were kept have their
checksums recorded by the next upgrade, and are trusted as they are at that point.

Only one upgrade of a database runs at a time. An upgrade takes a server-wide named lock (`GET_LOCK`) before checking
or applying anything, so upgrades started together, such as from several deployment replicas, run one after another.
An upgrade waits up to five minutes for another to finish, then stops without applying anything. If an upgrade process
dies, the server releases its lock when the connection closes. Dry runs do not take the lock.

Each script runs in its own transaction, so a failed script is not journaled and is retried on the next run. MariaDB
commits DDL statements implicitly, though, so see
[Transactions and DDL](docs/mariadb-table-reminders.md#transactions-and-ddl) before writing scripts that mix several
statements.

## Development

To write initial statements for adjusting tables, it is encouraged to use a database tool such as DBeaver to output
proposed changes and then applying them to `.sql` update files in this project.

For a quick refresher on MariaDB table syntax and other features, see the
[MariaDB Table Reminders](docs/mariadb-table-reminders.md) document.

Integration tests under `test/RedShirt.Example.Schema.IntegrationTests` use Testcontainers and require a working Docker
daemon (they spin up a MariaDB container to apply and verify schema scripts). Run them with:

```bash
dotnet test
```

### Applied schema review dump

`scripts/dump-applied-schema.sh` builds the same schema upgrader image as `docker-update.sh`, starts a **disposable**
MariaDB container on a private Docker network, applies all embedded update scripts, then writes a **schema-only** SQL
dump (no row data) suitable for review or diffing.

```bash
./scripts/dump-applied-schema.sh
./scripts/dump-applied-schema.sh -o /tmp/example-schema.sql
```

The script requires a working Docker daemon. It does not use your `.env` or local database. Optional overrides include
`DOCKER_IMAGE`, `TESTS_ENABLE`, `EXAMPLE_SCHEMA_NAME`, and `MARIADB_IMAGE` (defaults to `mariadb:12.3.2`, matching
integration tests).

Progress, warning and error messages from these scripts go to stderr and are coloured when stderr is a terminal. Set
`NO_COLOR` to turn colours off.

The disposable database is shared with the [table listing](#applied-table-listing) and
[DTO suggestion](#dto-suggestion) scripts through `scripts/lib/disposable-db.sh`. MariaDB-specific logic lives in that
file in two marked blocks:

* **Disposable database server**: Docker image (`MARIADB_IMAGE`), MariaDB bootstrap environment variables
  (`MARIADB_ROOT_PASSWORD`, `MARIADB_DATABASE`), and readiness via a TCP `mariadb` client on the same Docker
  network (not `mariadb-admin ping` on localhost, which can succeed before remote connections work).
  The script also creates the journal schema. The upgrader then runs as a migration user (`REVIEW_MIGRATION_USER`,
  default `schema_migrator`) that is created with only `MIGRATION_PRIVILEGES` on the review schema and
  `JOURNAL_PRIVILEGES` on the journal schema, as described in [Database Users](#database-users).
* **Queries and schema dump**: `mariadb --batch` for `information_schema` queries and `mariadb-dump` with `--no-data`
  (replace with your engine's client and schema export tool when pivoting away from MariaDB/MySQL).

The apply step still uses the published .NET upgrader (DbUp + `MySqlDatabase` in `SchemaUpgrader.cs`). Changing database
technology requires updating that project as described in [Initialization](#initialization), not only these shell
scripts.

### Applied table listing

`scripts/list-tables.sh` applies all embedded update scripts to the same kind of disposable database, then prints the
table names in the applied schema, one per line. The DbUp journal table (`Patches`) and the script checksum table
(`PatchChecksums`) are in the journal schema, so they are left out unless `--all` is given.

```bash
./scripts/list-tables.sh
./scripts/list-tables.sh --all -o /tmp/example-tables.txt
```

### DTO suggestion

`scripts/suggest-dto.sh` applies all embedded update scripts to the same kind of disposable database, then prints a
suggested C# DTO for one table, following the DTO conventions of the API template (such as `OrderDto`):

* Every column becomes a `required` property with an `init` accessor. Nullable columns get a nullable type.
* The class is marked with `[DbTable]`. A single-column primary key is marked with `[DbKey]`. `CreatedAtUtc` or
  `DateCreatedUtc` is marked with `[CreatedAtProperty]`, and `UpdatedAtUtc` or `DateUpdatedUtc` with
  `[UpdatedAtProperty]`.
* The class name defaults to `<Table>Dto`. Plural table names are not singularised, so use `--class-name` where needed.
* With `--root-namespace`, the `using` directives and namespace for the API's `DataStores` layout are written as well.
  Without it, only the class is printed and the `using` directives are left to the developer.

```bash
./scripts/suggest-dto.sh UploadAggregate
./scripts/suggest-dto.sh Customers --class-name CustomerDto --root-namespace RedShirt.Example.Api
./scripts/suggest-dto.sh Order --root-namespace RedShirt.Example.Api -o /tmp/OrderDto.cs
```

With `--entity-framework`, the script instead suggests an Entity Framework Core entity, following the conventions of
the API template's EF Core stores (such as `CustomerEntity`):

* The class is `internal sealed`, named `<Table>Entity`, and marked with `[Table]`. With `--root-namespace`, it is
  placed in the `<root>.DataStores.<Name>.Implementation.Entities` namespace.
* Properties have `set` accessors, since Entity Framework updates tracked entities in place. Only non-nullable columns
  are `required`.
* A commented-out `OnModelCreating` block follows the class, with the key, string lengths, decimal precision, column
  name mappings and indexes read from the applied schema.
* Entity Framework does not fill in created/updated timestamps, so a warning reminds you to set them in code or with
  a `SaveChanges` interceptor.

```bash
./scripts/suggest-dto.sh Customers --entity-framework --class-name CustomerEntity --root-namespace RedShirt.Example.Api
```

The suggestion is a starting point. Warnings about things that need attention (composite or non-`Guid` keys, missing
timestamp columns, renamed columns, unmapped types) are printed to stderr. Integer columns that represent enums or flags
(such as `UploadAggregate.State`) are suggested as plain integers.

## Execution

Instructions on how to apply schema updates.

The target database and its journal schema must already exist before running `update.sh` or `local-update.sh`. These
scripts apply schema patches to an existing database; they do not create either schema.

### Dry Run

To list the scripts that would be applied without changing the database, pass `--dry-run` or set
`EXAMPLE_SCHEMA_DRY_RUN=1`. The dry run still connects to the target database to read the journal table, but does not
create it.

```bash
./update.sh --dry-run
```

```bash
./local-update.sh --dry-run
```

The Docker scripts (`docker-update.sh` and `local-docker-update.sh`) accept `--dry-run` as well, or
`EXAMPLE_SCHEMA_DRY_RUN=1` can be set in `.env`.

### Database Users

Use separate database accounts for setting up the database, applying schema updates, and running the application.
None of them should be `root` in a deployed database.

| User        | Used by                    | Privileges                                        |
| ----------- | -------------------------- | ------------------------------------------------- |
| Admin       | DBA, one-time setup        | Global (existing account)                         |
| Migration   | This upgrader, each deploy | DDL and DML on the schema, append to its journal  |
| Application | The application services   | DML on the schema, no DDL, nothing on the journal |

The upgrader does not create either schema, so it never needs global privileges. An admin creates the schema, its
[journal schema](#journal-schema) and the two users once:

```sql
CREATE DATABASE `example`;
CREATE DATABASE `example_changes`;

CREATE USER 'example_migrator'@'%' IDENTIFIED BY 'redacted' REQUIRE SSL;
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES
  ON `example`.* TO 'example_migrator'@'%';
GRANT SELECT, INSERT, CREATE ON `example\_changes`.* TO 'example_migrator'@'%';

CREATE USER 'example_app'@'%' IDENTIFIED BY 'redacted' REQUIRE SSL;
GRANT SELECT, INSERT, UPDATE, DELETE ON `example`.* TO 'example_app'@'%';
```

**Migration user**:

* Set it as `EXAMPLE_SCHEMA_USER`. It needs DML on the application schema for scripts that move data.
* On the journal schema it only creates the journal tables and adds rows to them, so it cannot rewrite or delete
  journal history. `CREATE` and `INSERT` there also let it move journal tables in from the application schema (see
  [Journal Schema](#journal-schema)). `GET_LOCK` (the upgrade lock) needs no privilege.
* The grant above covers tables, indexes and foreign keys. When a script needs more, grant it on the same schema:
  `CREATE VIEW` and `SHOW VIEW` for views, `CREATE ROUTINE`, `ALTER ROUTINE` and `EXECUTE` for stored routines,
  `TRIGGER` for triggers, and `EVENT` for events. Avoid granting anything `ON *.*`.
* `scripts/list-tables.sh`, `scripts/suggest-dto.sh` and the integration tests apply the scripts as a user with exactly
  these grants (`MIGRATION_PRIVILEGES` and `JOURNAL_PRIVILEGES` in `scripts/lib/disposable-db.sh`, and
  `MigrationPrivileges` and `JournalPrivileges` in the tests), so a script that needs more fails there first with a
  "command denied" error. `scripts/cicd/lint.sh` checks that those lists match the grants above.

**Application user**:

* No DDL, so a compromised application cannot change the schema. Add `EXECUTE` if the application calls stored
  routines.
* The schema-wide grant covers tables added by later scripts, with no grant needed per table.
* Grant it nothing on the journal schema. A deleted `Patches` row would make a script run again, and a deleted
  `PatchChecksums` row would let the next upgrade trust an edited script.

**All users**:

* `REQUIRE SSL` makes the server refuse unencrypted connections for that account, whatever the client's TLS mode.
* Replace `'%'` with the host or network that connects, for example `'example_migrator'@'10.0.1.%'`, where you can.
* In the database name of a `GRANT ... ON`, `_` and `%` are wildcards. A grant on `` `my_db`.* `` also covers `myXdb`,
  `my1db` and so on. Escape them with a backslash, `` `my\_db`.* ``, so that the grant covers that schema only.
  `init-repo.sh` turns a label such as `PROJECT_MALAMUTE` into the schema name `project_malamute`, so this usually
  applies.

### Journal Schema

The `Patches` journal and `PatchChecksums` tables live in their own schema, `<name>_changes` by default, so that the
application's user can be granted the whole application schema without being able to change them. Set
`EXAMPLE_SCHEMA_JOURNAL_NAME` to use another name. Setting it to the application schema's own name keeps the journal
there, as before this was added, at the cost of the protection above.

**Existing databases**: Databases upgraded before this change have the journal tables in the application schema. Once
an admin has created the journal schema and granted the migration user on it, the next upgrade moves the tables there
with a single `RENAME TABLE` before doing anything else. A dry run reports the move without making it. The upgrader
refuses to run, and changes nothing, when:

* the journal schema does not exist, or the migration user has no privileges on it, or
* both schemas hold journal tables. Work out which journal is right and remove the other one by hand.

Do not create an empty journal in the new schema by hand instead of letting the upgrader move the old one. An empty
journal means that no script has been applied, and every script would run again.

**Backups**: The journal schema is part of the database's state. Back up and restore the two schemas together, at the
same point in time. If the application schema is restored from an older backup but the journal is not, the journal
lists scripts whose changes are no longer there. The upgrader skips them, and the integrity check cannot tell, since
the scripts themselves are unchanged. A `mariadb-dump` of the application schema alone leaves the journal out, so
include both, for example with `mariadb-dump --databases example example_changes`.

### Deployed Database

#### Setup

Set the following necessary environment variables for the run:

* `EXAMPLE_SCHEMA_HOST`: Address of your SQL server
* `EXAMPLE_SCHEMA_NAME`: Name of your schema
* `EXAMPLE_SCHEMA_JOURNAL_NAME`: Optional. Schema for the journal and checksum tables. Defaults to
  `EXAMPLE_SCHEMA_NAME` with `_changes` added. See [Journal Schema](#journal-schema).
* `EXAMPLE_SCHEMA_USER`: Username with which to access the target schema. Use a migration user as described in
  [Database Users](#database-users).
* `EXAMPLE_SCHEMA_PASSWORD`: User password to access the target schema
* `EXAMPLE_SCHEMA_PASSWORD_FILE`: Path to a file holding the password, used instead of `EXAMPLE_SCHEMA_PASSWORD` (set
  only one). One trailing line ending is ignored. This suits mounted secrets, and keeps the password out of the process
  environment.

The following optional variables control TLS:

* `EXAMPLE_SCHEMA_SSL_MODE`: TLS mode for the connection. Defaults to `Required`. See the table below.
* `EXAMPLE_SCHEMA_SSL_CA`: Path to a CA certificate file (PEM) used to validate the server certificate with `VerifyCA`
  or `VerifyFull`. When it is not set, the system trust store is used. With Docker, the file must be mounted into the
  container and the path must be the path inside the container.

| Mode         | Encrypted      | Server certificate checked          |
| ------------ | -------------- | ----------------------------------- |
| `None`       | No             | No                                  |
| `Preferred`  | When available | No                                  |
| `Required`   | Yes            | No                                  |
| `VerifyCA`   | Yes            | Signed by a trusted CA              |
| `VerifyFull` | Yes            | Trusted CA and host name must match |

The default, `Required`, never falls back to plaintext, but it does not protect against an attacker who can present
their own certificate. Use `VerifyFull` for deployed databases wherever a CA certificate is available. Use `Preferred`
or `None` only for a local server that has no TLS set up. MariaDB 11.4 and later turn on TLS by default with a
generated certificate, so `Required` works with them without extra setup.

Do not export these from `~/.bashrc` or another shell profile. Every program started from that shell inherits the
password, and profiles tend to end up in backups and dotfile repositories. Keep them in a file outside the repository
that only you can read, and load it only for the run. For example, `~/.config/example-schema/production.env`:

```bash
EXAMPLE_SCHEMA_HOST=db.example.com
EXAMPLE_SCHEMA_NAME=example
EXAMPLE_SCHEMA_USER=db-user
EXAMPLE_SCHEMA_PASSWORD_FILE="${HOME}/.config/example-schema/production.password"
EXAMPLE_SCHEMA_SSL_MODE=VerifyFull
```

Create the directory and files so that only you can read them:

```bash
install -d -m 700 ~/.config/example-schema
```

```bash
(umask 077 && touch ~/.config/example-schema/production.env ~/.config/example-schema/production.password)
```

Using `EXAMPLE_SCHEMA_PASSWORD_FILE` instead of `EXAMPLE_SCHEMA_PASSWORD` keeps the password out of the environment as
well. In CI or an orchestrator, use its secret store to provide these values or the mounted password file instead.

#### Application

To deploy updates, use the `update.sh` shorthand script. Load the settings in a subshell so that they do not stay in
your shell afterwards:

```bash
(set -a && . ~/.config/example-schema/production.env && set +a && ./update.sh)
```

> **Warning**: If you source a configuration file like this, make sure that the file has the appropriate permissions
> so that it is not casually readable by another user.

#### Docker

Copy `.env.example` to `.env`, set the `EXAMPLE_SCHEMA_*` variables, and run:

```bash
./docker-update.sh
```

If the database listens on your host machine (not inside Docker), set `EXAMPLE_SCHEMA_HOST` to
`host.docker.internal` in `.env`. The script adds a matching `host.docker.internal` host entry for Linux.

Values in `.env` are passed to the container as environment variables, which anyone who can run `docker inspect` can
read. To keep the password out of them, leave `EXAMPLE_SCHEMA_PASSWORD` out of `.env` and set `PASSWORD_FILE` to a
file that holds it:

```bash
PASSWORD_FILE=~/.config/example-schema/password ./docker-update.sh
```

The file is mounted read-only into the container and passed as `EXAMPLE_SCHEMA_PASSWORD_FILE`. `local-docker-update.sh`
does this for `LOCAL_SQL_PASSWORD`.

Set `TESTS_ENABLE=1` when building the image if you want `dotnet test` to run during `docker build` (the default is
`0` for faster applies).

### Local Database

#### Setup

Ensure that the `LOCAL_SQL_PASSWORD` environment variable is set when running the local scripts. This is the password
for the local SQL server. Use it only for a local development server with a throwaway password, never for a shared or
deployed one. The `local-update.sh` script maps it to `EXAMPLE_SCHEMA_PASSWORD` and supplies
local connection defaults:

* `EXAMPLE_SCHEMA_HOST`: `127.0.0.1`
* `EXAMPLE_SCHEMA_NAME`: `example`
* `EXAMPLE_SCHEMA_USER`: `LOCAL_SQL_USER`, or `root` when that is not set

`root` is kept as the default so that a fresh local server works without setup. To catch scripts that need more than
the migration grant before they reach a deployed database, create a local migration user as described in
[Database Users](#database-users) and set `LOCAL_SQL_USER` to it.

TLS defaults to `Required`, as for a deployed database. If your local server has no TLS set up, set
`EXAMPLE_SCHEMA_SSL_MODE=Preferred` (or `None`) in your environment before running the local scripts.

Example, reading it from a file that only you can read:

```bash
LOCAL_SQL_PASSWORD="$(cat ~/.config/example-schema/local.password)" ./local-update.sh
```

#### Application

To deploy updates against the local SQL server, use the `local-update.sh` shorthand script:

```bash
./local-update.sh
```

#### Docker

With `LOCAL_SQL_PASSWORD` set (same as [Local Database setup](#local-database)), run:

```bash
./local-docker-update.sh
```

This builds and runs the schema upgrader in Docker and connects to MariaDB/MySQL on the host via
`host.docker.internal`. Alternatively, copy `.env.example` to `.env`, set `EXAMPLE_SCHEMA_HOST` to
`host.docker.internal`, fill in the other variables, and use `./docker-update.sh`.

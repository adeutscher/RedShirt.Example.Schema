using System.Text;
using MySqlConnector;
using Testcontainers.MariaDb;

namespace RedShirt.Example.Schema.IntegrationTests.Tests;

public class SchemaUpgradeTests
{
    private const string DatabaseName = "example";
    private const string Username = "test";
    private const string Password = "test";

    /// <summary>The default journal schema for <see cref="DatabaseName" />.</summary>
    private const string JournalSchema = "example_changes";

    /// <summary>The migration user's privileges on the journal schema, from the README (Database Users).</summary>
    private const string JournalPrivileges = "SELECT, INSERT, CREATE";

    /// <summary>A journal or checksum table in the journal schema, quoted for SQL.</summary>
    private static string Journal(string table)
    {
        return $"`{JournalSchema}`.`{table}`";
    }

    /// <summary>
    ///     The migration user's privileges from the README (Database Users). scripts/cicd/check-privileges.sh checks that
    ///     this matches the GRANT there and MIGRATION_PRIVILEGES in scripts/lib/disposable-db.sh.
    /// </summary>
    private const string MigrationPrivileges = "SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES";

    private static async Task AssertSchemaAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        foreach (var tableName in new[] {"Product", "Customers", "Order", "UploadAggregate", "UploadEvent"})
        {
            await using var command = new MySqlCommand(
                """
                SELECT COUNT(*)
                FROM information_schema.tables
                WHERE table_schema = @database
                  AND table_name = @tableName
                """,
                connection);
            command.Parameters.AddWithValue("@database", DatabaseName);
            command.Parameters.AddWithValue("@tableName", tableName);
            var tableCount = Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, tableCount);
        }

        await using (var command = new MySqlCommand(
                         """
                         SELECT COUNT(*)
                         FROM information_schema.tables
                         WHERE table_schema = @database
                           AND table_name = @journal
                         """,
                         connection))
        {
            command.Parameters.AddWithValue("@database", JournalSchema);
            command.Parameters.AddWithValue("@journal", SchemaUpgrader.JournalTableName);
            var journalTableCount =
                Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, journalTableCount);
        }

        await using (var command = new MySqlCommand(
                         $"SELECT COUNT(*) FROM {Journal(SchemaUpgrader.JournalTableName)}",
                         connection))
        {
            var journalRowCount =
                Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.True(journalRowCount >= 1);
        }

        // Every journaled script has a checksum.
        await using (var command = new MySqlCommand(
                         $"""
                          SELECT COUNT(*) FROM {Journal(SchemaUpgrader.JournalTableName)} p
                          LEFT JOIN {Journal(ScriptChecksums.TableName)} c ON c.ScriptName = p.ScriptName
                          WHERE c.ScriptName IS NULL
                          """,
                         connection))
        {
            var missing = Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, missing);
        }
    }

    /// <summary>
    ///     Escapes <c>_</c> and <c>%</c>, which are wildcards in the database name of a GRANT, as the README describes.
    /// </summary>
    private static string EscapeGrantWildcards(string databaseName)
    {
        return databaseName.Replace("_", @"\_").Replace("%", @"\%");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 120_000)]
    public async Task Upgrade_Refuses_WhenAppliedScriptChecksumDiffers()
    {
        await using var mariaDb = BuildContainer();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = mariaDb.GetConnectionString();

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));

        // Stands in for an applied script that was edited afterwards.
        await ExecuteAsync(connectionString,
            $"UPDATE {Journal(ScriptChecksums.TableName)} SET `Sha256` = REPEAT('0', 64)");

        Assert.Equal(-1, SchemaUpgrader.DryRun(connectionString, DatabaseName));
        Assert.Equal(-1, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
    }

    [Fact(Timeout = 120_000)]
    public async Task Upgrade_Refuses_WhenAppliedScriptIsMissing()
    {
        await using var mariaDb = BuildContainer();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = mariaDb.GetConnectionString();

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));

        // Stands in for an applied script that was renamed or deleted.
        await ExecuteAsync(connectionString,
            $"INSERT INTO {Journal(SchemaUpgrader.JournalTableName)} (`ScriptName`, `Applied`) VALUES ('Removed.sql', NOW())");

        Assert.Equal(-1, SchemaUpgrader.DryRun(connectionString, DatabaseName));
        Assert.Equal(-1, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
    }

    /// <summary>
    ///     Databases upgraded before checksums were kept have journal entries without checksums. The next upgrade
    ///     records them.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Upgrade_RecordsMissingChecksums_ForPreviouslyAppliedScripts()
    {
        await using var mariaDb = BuildContainer();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = mariaDb.GetConnectionString();

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        await ExecuteAsync(connectionString, $"DROP TABLE {Journal(ScriptChecksums.TableName)}");

        Assert.Equal(0, SchemaUpgrader.DryRun(connectionString, DatabaseName));
        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        await AssertSchemaAsync(connectionString);
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@name", SchemaUpgrader.LockName(DatabaseName));
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 120_000)]
    public async Task Upgrade_WaitsForLock_AndGivesUpWithoutApplying()
    {
        await using var mariaDb = BuildContainer();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = mariaDb.GetConnectionString();

        // Stands in for another upgrade that is still running.
        await using var other = new MySqlConnection(connectionString);
        await other.OpenAsync(TestContext.Current.CancellationToken);
        await using (var take = new MySqlCommand("SELECT GET_LOCK(@name, 0)", other))
        {
            take.Parameters.AddWithValue("@name", SchemaUpgrader.LockName(DatabaseName));
            Assert.Equal(1L, Convert.ToInt64(await take.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        }

        Assert.Equal(-1, SchemaUpgrader.Upgrade(connectionString, DatabaseName, lockTimeout: TimeSpan.FromSeconds(1)));
        Assert.False(await JournalExistsAsync(connectionString));

        await other.CloseAsync();
        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        Assert.Equal(1L, await ScalarAsync(connectionString, "SELECT IS_FREE_LOCK(@name)"));
    }

    [Fact(Timeout = 120_000)]
    public async Task Upgrade_RunAtTheSameTime_AppliesEachScriptOnce()
    {
        await using var mariaDb = BuildContainer();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = mariaDb.GetConnectionString();

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => SchemaUpgrader.Upgrade(connectionString, DatabaseName))));

        Assert.All(results, result => Assert.Equal(0, result));
        Assert.Equal(
            await ScalarAsync(connectionString,
                $"SELECT COUNT(DISTINCT `ScriptName`) FROM {Journal(SchemaUpgrader.JournalTableName)}"),
            await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {Journal(SchemaUpgrader.JournalTableName)}"));
        await AssertSchemaAsync(connectionString);
    }

    [Fact]
    public void LockName_FitsServerLimit()
    {
        Assert.Equal("schema-upgrade:example", SchemaUpgrader.LockName("example"));
        Assert.True(SchemaUpgrader.LockName(new string('x', 64)).Length <= 64);
        Assert.NotEqual(SchemaUpgrader.LockName(new string('x', 64)), SchemaUpgrader.LockName(new string('y', 64)));
    }

    [Fact]
    public void Checksum_IgnoresLineEndingsAndByteOrderMark()
    {
        Assert.Equal(ScriptChecksums.Compute("CREATE TABLE `A`;\nSELECT 1;\n"),
            ScriptChecksums.Compute("﻿CREATE TABLE `A`;\r\nSELECT 1;\r\n"));
        Assert.NotEqual(ScriptChecksums.Compute("SELECT 1;"), ScriptChecksums.Compute("SELECT 2;"));
    }

    /// <summary>
    ///     Databases upgraded before the journal had its own schema have it in the application schema. The next upgrade
    ///     moves it rather than starting an empty journal, which would run every script again.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Upgrade_MovesJournal_FromApplicationSchema()
    {
        await using var mariaDb = BuildContainer();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = mariaDb.GetConnectionString();

        // The old layout.
        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName, journalSchema: DatabaseName));
        var applied = await ScalarAsync(connectionString,
            $"SELECT COUNT(*) FROM `{DatabaseName}`.`{SchemaUpgrader.JournalTableName}`");

        // A dry run reports from the journal where it is, and moves nothing.
        Assert.Equal(0, SchemaUpgrader.DryRun(connectionString, DatabaseName));
        Assert.True(await TableExistsAsync(connectionString, DatabaseName, SchemaUpgrader.JournalTableName));
        Assert.False(await TableExistsAsync(connectionString, JournalSchema, SchemaUpgrader.JournalTableName));

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));

        foreach (var table in new[] {SchemaUpgrader.JournalTableName, ScriptChecksums.TableName})
        {
            Assert.False(await TableExistsAsync(connectionString, DatabaseName, table), table);
            Assert.True(await TableExistsAsync(connectionString, JournalSchema, table), table);
        }

        // Nothing ran again.
        Assert.Equal(applied,
            await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {Journal(SchemaUpgrader.JournalTableName)}"));
        Assert.Empty(SchemaUpgrader.GetPendingScripts(connectionString, DatabaseName));
        await AssertSchemaAsync(connectionString);
    }

    [Fact(Timeout = 120_000)]
    public async Task Upgrade_Refuses_WhenJournalIsInBothSchemas()
    {
        await using var mariaDb = BuildContainer();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = mariaDb.GetConnectionString();

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        await ExecuteAsync(connectionString,
            $"CREATE TABLE `{DatabaseName}`.`{SchemaUpgrader.JournalTableName}` LIKE {Journal(SchemaUpgrader.JournalTableName)}");

        Assert.Equal(-1, SchemaUpgrader.DryRun(connectionString, DatabaseName));
        Assert.Equal(-1, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
    }

    [Fact(Timeout = 120_000)]
    public async Task Upgrade_Refuses_WhenJournalSchemaIsMissing()
    {
        await using var mariaDb = BuildContainer();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);
        var connectionString = mariaDb.GetConnectionString();

        Assert.Equal(-1, SchemaUpgrader.DryRun(connectionString, DatabaseName, "missing"));
        Assert.Equal(-1, SchemaUpgrader.Upgrade(connectionString, DatabaseName, journalSchema: "missing"));
        Assert.False(await TableExistsAsync(connectionString, DatabaseName, "Customers"));
    }

    [Fact]
    public void DefaultJournalSchema_AddsSuffix_AndRejectsOverlongNames()
    {
        Assert.Equal(JournalSchema, SchemaUpgrader.DefaultJournalSchema(DatabaseName));
        Assert.Equal(64, SchemaUpgrader.DefaultJournalSchema(new string('x', 56)).Length);
        Assert.Throws<ArgumentException>(() => SchemaUpgrader.DefaultJournalSchema(new string('x', 57)));
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string schema, string table)
    {
        return await ScalarAsync(connectionString,
            $"""
             SELECT COUNT(*) FROM information_schema.TABLES
             WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{table}'
             """) > 0;
    }

    private static string RootConnectionString(MariaDbContainer mariaDb)
    {
        return new MySqlConnectionStringBuilder(mariaDb.GetConnectionString()) {UserID = "root"}.ConnectionString;
    }

    /// <summary>
    ///     With a journal schema, the journal and checksum tables are created there and the application schema holds only
    ///     the application's tables.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Upgrade_KeepsJournal_InJournalSchema()
    {
        const string journalSchema = "tracking";

        await using var mariaDb = BuildContainer();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(RootConnectionString(mariaDb),
            $"CREATE DATABASE `{journalSchema}`; GRANT ALL ON `{journalSchema}`.* TO '{Username}'@'%';");
        var connectionString = mariaDb.GetConnectionString();

        Assert.Equal(0, SchemaUpgrader.DryRun(connectionString, DatabaseName, journalSchema));
        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName, journalSchema: journalSchema));
        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName, journalSchema: journalSchema));
        Assert.Empty(SchemaUpgrader.GetPendingScripts(connectionString, DatabaseName, journalSchema));

        foreach (var table in new[] {SchemaUpgrader.JournalTableName, ScriptChecksums.TableName})
        {
            Assert.True(await TableExistsAsync(connectionString, journalSchema, table), table);
            Assert.False(await TableExistsAsync(connectionString, DatabaseName, table), table);
        }

        Assert.True(await TableExistsAsync(connectionString, DatabaseName, "Customers"));
        Assert.True(await ScalarAsync(connectionString,
            $"SELECT COUNT(*) FROM `{journalSchema}`.`{ScriptChecksums.TableName}`") >= 1);
    }

    private static async Task<bool> JournalExistsAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new MySqlCommand(
            """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = @database
              AND table_name = @journal
            """,
            connection);
        command.Parameters.AddWithValue("@database", JournalSchema);
        command.Parameters.AddWithValue("@journal", SchemaUpgrader.JournalTableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) > 0;
    }

    /// <summary>
    ///     A MariaDB container with the application schema and, as an admin would set up, the journal schema. The test
    ///     user gets all privileges on both, as the container image gives it on the application schema.
    /// </summary>
    private static MariaDbContainer BuildContainer()
    {
        var setup = $"""
                     CREATE DATABASE `{JournalSchema}`;
                     GRANT ALL ON `{EscapeGrantWildcards(JournalSchema)}`.* TO '{Username}'@'%';
                     """;

        return new MariaDbBuilder("mariadb:12.3.2")
            .WithDatabase(DatabaseName)
            .WithUsername(Username)
            .WithPassword(Password)
            .WithResourceMapping(Encoding.UTF8.GetBytes(setup), "/docker-entrypoint-initdb.d/journal-schema.sql")
            .Build();
    }

    /// <summary>
    ///     Applies all scripts except the last, then the rest, to mimic an existing database receiving a new update.
    ///     Dry runs between stages must report exactly the pending scripts and must not create the journal table.
    ///     With a single embedded script, stage one applies nothing and the test still covers the pending path.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Upgrade_InStages_AppliesOnlyPendingScripts()
    {
        await using var mariaDb = BuildContainer();

        await mariaDb.StartAsync(TestContext.Current.CancellationToken);

        var connectionString = mariaDb.GetConnectionString();

        var allScripts = SchemaUpgrader.GetPendingScripts(connectionString, DatabaseName);
        Assert.NotEmpty(allScripts);
        Assert.False(await JournalExistsAsync(connectionString));
        Assert.Equal(0, SchemaUpgrader.DryRun(connectionString, DatabaseName));
        Assert.False(await JournalExistsAsync(connectionString));

        var lastScript = allScripts[^1];
        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName, name => name != lastScript));
        Assert.Equal([lastScript], SchemaUpgrader.GetPendingScripts(connectionString, DatabaseName));

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        Assert.Empty(SchemaUpgrader.GetPendingScripts(connectionString, DatabaseName));
        await AssertSchemaAsync(connectionString);
    }

    /// <summary>
    ///     Runs every upgrade path (apply, dry run, journal move, integrity check, checksum backfill and the lock) as a user
    ///     with only the documented migration privileges on the application and journal schemas, as a deployed database
    ///     should be set up.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Upgrade_Succeeds_AsLeastPrivilegeMigrationUser()
    {
        const string migrationUser = "example_migrator";
        const string migrationPassword = "migrator;pw";

        await using var mariaDb = BuildContainer();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);

        var rootConnectionString = RootConnectionString(mariaDb);
        await ExecuteAsync(rootConnectionString,
            $"""
             CREATE USER '{migrationUser}'@'%' IDENTIFIED BY '{migrationPassword}' REQUIRE SSL;
             GRANT {MigrationPrivileges} ON `{EscapeGrantWildcards(DatabaseName)}`.* TO '{migrationUser}'@'%';
             GRANT {JournalPrivileges} ON `{EscapeGrantWildcards(JournalSchema)}`.* TO '{migrationUser}'@'%';
             """);

        var connectionString = new MySqlConnectionStringBuilder(mariaDb.GetConnectionString())
        {
            UserID = migrationUser,
            Password = migrationPassword
        }.ConnectionString;

        // Start from the old layout, with the journal in the application schema, so that the move to the journal schema
        // is also done with only these privileges.
        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName, journalSchema: DatabaseName));
        Assert.Equal(0, SchemaUpgrader.DryRun(connectionString, DatabaseName));
        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        Assert.False(await TableExistsAsync(connectionString, DatabaseName, SchemaUpgrader.JournalTableName));
        await AssertSchemaAsync(connectionString);

        // Drop the checksums so that the second upgrade also backfills them. The migration user cannot delete from the
        // journal, so do it as root.
        await ExecuteAsync(rootConnectionString, $"DELETE FROM {Journal(ScriptChecksums.TableName)}");
        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        await AssertSchemaAsync(connectionString);

        // The grant does not reach other schemas.
        var denied = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteAsync(connectionString, "CREATE DATABASE `other`"));
        Assert.Equal(MySqlErrorCode.DatabaseAccessDenied, denied.ErrorCode);

        // With _ escaped, a grant on one schema does not also match a schema whose name differs at the _.
        await ExecuteAsync(rootConnectionString,
            $"""
             CREATE DATABASE `other_db`;
             CREATE DATABASE `otherXdb`;
             GRANT CREATE ON `{EscapeGrantWildcards("other_db")}`.* TO '{migrationUser}'@'%';
             """);
        await ExecuteAsync(connectionString, "CREATE TABLE `other_db`.`Allowed` (`Id` INT)");
        var lookalike = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteAsync(connectionString, "CREATE TABLE `otherXdb`.`Denied` (`Id` INT)"));
        Assert.Equal(MySqlErrorCode.TableAccessDenied, lookalike.ErrorCode);
    }

    /// <summary>
    ///     Starts a MariaDB Testcontainer, applies embedded schema scripts via <see cref="SchemaUpgrader" />, and
    ///     verifies the resulting tables and journal. Runs the upgrade a second time to confirm idempotency.
    ///     Timeout is 120 seconds to allow for container image pull and database readiness on cold CI runners.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Upgrade_AppliesEmbeddedScripts_AndIsIdempotent()
    {
        await using var mariaDb = BuildContainer();

        await mariaDb.StartAsync(TestContext.Current.CancellationToken);

        var connectionString = mariaDb.GetConnectionString();

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        await AssertSchemaAsync(connectionString);

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        await AssertSchemaAsync(connectionString);
        Assert.Empty(SchemaUpgrader.GetPendingScripts(connectionString, DatabaseName));
    }
}

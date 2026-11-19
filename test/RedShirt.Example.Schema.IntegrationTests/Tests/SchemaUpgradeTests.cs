using MySqlConnector;
using Testcontainers.MariaDb;

namespace RedShirt.Example.Schema.IntegrationTests.Tests;

public class SchemaUpgradeTests
{
    private const string DatabaseName = "example";
    private const string Username = "test";
    private const string Password = "test";

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
            command.Parameters.AddWithValue("@database", DatabaseName);
            command.Parameters.AddWithValue("@journal", SchemaUpgrader.JournalTableName);
            var journalTableCount =
                Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, journalTableCount);
        }

        await using (var command = new MySqlCommand(
                         $"SELECT COUNT(*) FROM `{SchemaUpgrader.JournalTableName}`",
                         connection))
        {
            var journalRowCount =
                Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.True(journalRowCount >= 1);
        }

        // Every journaled script has a checksum.
        await using (var command = new MySqlCommand(
                         $"""
                          SELECT COUNT(*) FROM `{SchemaUpgrader.JournalTableName}` p
                          LEFT JOIN `{ScriptChecksums.TableName}` c ON c.ScriptName = p.ScriptName
                          WHERE c.ScriptName IS NULL
                          """,
                         connection))
        {
            var missing = Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, missing);
        }
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
            $"UPDATE `{ScriptChecksums.TableName}` SET `Sha256` = REPEAT('0', 64)");

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
            $"INSERT INTO `{SchemaUpgrader.JournalTableName}` (`ScriptName`, `Applied`) VALUES ('Removed.sql', NOW())");

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
        await ExecuteAsync(connectionString, $"DROP TABLE `{ScriptChecksums.TableName}`");

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
                $"SELECT COUNT(DISTINCT `ScriptName`) FROM `{SchemaUpgrader.JournalTableName}`"),
            await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM `{SchemaUpgrader.JournalTableName}`"));
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
        command.Parameters.AddWithValue("@database", DatabaseName);
        command.Parameters.AddWithValue("@journal", SchemaUpgrader.JournalTableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) > 0;
    }

    private static MariaDbContainer BuildContainer()
    {
        return new MariaDbBuilder("mariadb:12.3.2")
            .WithDatabase(DatabaseName)
            .WithUsername(Username)
            .WithPassword(Password)
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

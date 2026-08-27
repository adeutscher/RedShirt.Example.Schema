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

        foreach (var tableName in new[] {"Product", "Customers", "Order"})
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
    }

    /// <summary>
    ///     Starts a MariaDB Testcontainer, applies embedded schema scripts via <see cref="SchemaUpgrader" />, and
    ///     verifies the resulting tables and journal. Runs the upgrade a second time to confirm idempotency.
    ///     Timeout is 120 seconds to allow for container image pull and database readiness on cold CI runners.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Upgrade_AppliesEmbeddedScripts_AndIsIdempotent()
    {
        await using var mariaDb = new MariaDbBuilder("mariadb:12.3.2")
            .WithDatabase(DatabaseName)
            .WithUsername(Username)
            .WithPassword(Password)
            .Build();

        await mariaDb.StartAsync(TestContext.Current.CancellationToken);

        var connectionString = mariaDb.GetConnectionString();

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        await AssertSchemaAsync(connectionString);

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        await AssertSchemaAsync(connectionString);
    }
}
using MySqlConnector;
using Testcontainers.MySql;

namespace RedShirt.Example.Schema.IntegrationTests.Tests;

public class SchemaUpgradeTests
{
    private const string DatabaseName = "example";
    private const string Username = "test";
    private const string Password = "test";

    private static async Task AssertSchemaAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var command = new MySqlCommand(
                         """
                         SELECT COUNT(*)
                         FROM information_schema.tables
                         WHERE table_schema = @database
                           AND table_name = 'DapperData'
                         """,
                         connection))
        {
            command.Parameters.AddWithValue("@database", DatabaseName);
            var tableCount = Convert.ToInt32(await command.ExecuteScalarAsync());
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
            var journalTableCount = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, journalTableCount);
        }

        await using (var command = new MySqlCommand(
                         $"SELECT COUNT(*) FROM `{SchemaUpgrader.JournalTableName}`",
                         connection))
        {
            var journalRowCount = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.True(journalRowCount >= 1);
        }
    }

    [Fact]
    public async Task Upgrade_AppliesEmbeddedScripts_AndIsIdempotent()
    {
        await using var mySql = new MySqlBuilder()
            .WithDatabase(DatabaseName)
            .WithUsername(Username)
            .WithPassword(Password)
            .Build();

        await mySql.StartAsync();

        var connectionString = mySql.GetConnectionString();

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        await AssertSchemaAsync(connectionString);

        Assert.Equal(0, SchemaUpgrader.Upgrade(connectionString, DatabaseName));
        await AssertSchemaAsync(connectionString);
    }
}
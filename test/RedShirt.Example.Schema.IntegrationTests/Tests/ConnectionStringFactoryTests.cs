using MySqlConnector;
using Testcontainers.MariaDb;

namespace RedShirt.Example.Schema.IntegrationTests.Tests;

public class ConnectionStringFactoryTests
{
    [Fact]
    public void Build_EscapesValues_InsteadOfAddingOptions()
    {
        const string password = "p;SslMode=None;x='\"=";

        var connectionString = ConnectionStringFactory.Build("db.example", "user;Database=other", password, "example");
        var parsed = new MySqlConnectionStringBuilder(connectionString);

        Assert.Equal(password, parsed.Password);
        Assert.Equal("user;Database=other", parsed.UserID);
        Assert.Equal("example", parsed.Database);
        Assert.Equal(MySqlSslMode.Required, parsed.SslMode);
    }

    [Fact]
    public void Build_DefaultsToRequiredTls()
    {
        var parsed = new MySqlConnectionStringBuilder(ConnectionStringFactory.Build("h", "u", "p", "d"));

        Assert.Equal(MySqlSslMode.Required, parsed.SslMode);
        Assert.Equal(string.Empty, parsed.SslCa);
    }

    [Fact]
    public void Build_SetsCaFile_WhenGiven()
    {
        var parsed = new MySqlConnectionStringBuilder(
            ConnectionStringFactory.Build("h", "u", "p", "d", MySqlSslMode.VerifyFull, "/certs/ca.pem"));

        Assert.Equal(MySqlSslMode.VerifyFull, parsed.SslMode);
        Assert.Equal("/certs/ca.pem", parsed.SslCa);
    }

    [Theory]
    [InlineData(null, MySqlSslMode.Required)]
    [InlineData("", MySqlSslMode.Required)]
    [InlineData("  ", MySqlSslMode.Required)]
    [InlineData("required", MySqlSslMode.Required)]
    [InlineData(" VerifyFull ", MySqlSslMode.VerifyFull)]
    [InlineData("verifyca", MySqlSslMode.VerifyCA)]
    [InlineData("Preferred", MySqlSslMode.Preferred)]
    [InlineData("None", MySqlSslMode.None)]
    public void TryParseSslMode_AcceptsNames(string? value, MySqlSslMode expected)
    {
        Assert.True(ConnectionStringFactory.TryParseSslMode(value, out var sslMode));
        Assert.Equal(expected, sslMode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("Verify Full")]
    [InlineData("Required,None")]
    [InlineData("Strict")]
    public void TryParseSslMode_RejectsOtherValues(string value)
    {
        Assert.False(ConnectionStringFactory.TryParseSslMode(value, out _));
    }

    /// <summary>
    ///     MariaDB 11.4 and later turn on TLS with a generated certificate by default, so the default mode connects to
    ///     the test container over TLS.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Build_DefaultMode_ConnectsOverTls()
    {
        await using var mariaDb = new MariaDbBuilder("mariadb:12.3.2")
            .WithDatabase("example")
            .WithUsername("test")
            .WithPassword("test;with=chars")
            .Build();
        await mariaDb.StartAsync(TestContext.Current.CancellationToken);

        var builder = new MySqlConnectionStringBuilder(
            ConnectionStringFactory.Build(mariaDb.Hostname, "test", "test;with=chars", "example"))
        {
            Port = mariaDb.GetMappedPublicPort(MariaDbBuilder.MariaDbPort)
        };

        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new MySqlCommand("SHOW SESSION STATUS LIKE 'Ssl_cipher'", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.False(string.IsNullOrEmpty(reader.GetString(1)));
    }
}

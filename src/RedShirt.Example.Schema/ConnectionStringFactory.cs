using MySqlConnector;

namespace RedShirt.Example.Schema;

internal static class ConnectionStringFactory
{
    /// <summary>
    ///     Used when no TLS mode is configured. The connection fails rather than falling back to plaintext, but the server
    ///     certificate is not validated. Use VerifyCA or VerifyFull wherever a trusted CA certificate is available.
    /// </summary>
    public const MySqlSslMode DefaultSslMode = MySqlSslMode.Required;

    /// <summary>
    ///     Builds a connection string with <see cref="MySqlConnectionStringBuilder" />, so that values containing
    ///     <c>;</c>, <c>=</c> or quotes are escaped instead of being read as extra connection options.
    /// </summary>
    public static string Build(string host, string user, string password, string database,
        MySqlSslMode sslMode = DefaultSslMode, string? sslCa = null)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = host,
            UserID = user,
            Password = password,
            Database = database,
            SslMode = sslMode
        };

        if (!string.IsNullOrWhiteSpace(sslCa))
        {
            builder.SslCa = sslCa;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    ///     Parses a TLS mode name such as "Required" or "VerifyFull" (case-insensitive). Blank input gives
    ///     <see cref="DefaultSslMode" />. Numeric values are rejected, so that a typo cannot select a mode by accident.
    /// </summary>
    public static bool TryParseSslMode(string? value, out MySqlSslMode sslMode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            sslMode = DefaultSslMode;
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.All(char.IsAsciiLetter)
            && Enum.TryParse(trimmed, true, out sslMode)
            && Enum.IsDefined(sslMode))
        {
            return true;
        }

        sslMode = DefaultSslMode;
        return false;
    }
}

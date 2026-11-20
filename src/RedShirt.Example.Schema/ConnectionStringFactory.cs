using MySqlConnector;
using System.Net;

namespace RedShirt.Example.Schema;

internal static class ConnectionStringFactory
{
    /// <summary>
    ///     Used when no TLS mode is configured. The connection fails rather than falling back to plaintext, but the server
    ///     certificate is not validated. Use VerifyCA or VerifyFull wherever a trusted CA certificate is available.
    /// </summary>
    private const MySqlSslMode DefaultSslMode = MySqlSslMode.Required;

    /// <summary>Loopback addresses, a Unix socket path, and the Docker host as seen from a container.</summary>
    private static bool IsLocalHost(string host)
    {
        if (host.StartsWith('/')
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address)
               && IPAddress.IsLoopback(address);
    }

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

    /// <summary>
    ///     Returns a warning when the server certificate is not validated and the server is not on this machine, or null.
    ///     An attacker on the network between here and the server could then read or change everything, including the
    ///     password. An explicitly set <c>Required</c> is taken as a deliberate choice and not warned about, so that a
    ///     server without a verifiable certificate can be used without the warning. <c>Preferred</c> and <c>None</c>,
    ///     which can fall back to plaintext, are always warned about.
    /// </summary>
    public static string? UnverifiedTlsWarning(string host, MySqlSslMode sslMode, bool sslModeIsDefault)
    {
        if (sslMode is MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull)
        {
            return null;
        }

        if (sslMode == MySqlSslMode.Required && !sslModeIsDefault)
        {
            return null;
        }

        // MySqlConnector accepts a comma-separated list of hosts.
        var remote = host.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(h => !IsLocalHost(h))
            .ToList();
        if (remote.Count == 0)
        {
            return null;
        }

        var fallback = sslMode == MySqlSslMode.Required
            ? "TLS is required, but the server certificate is not validated"
            : $"TLS mode {sslMode} can fall back to plaintext, and does not validate the server certificate";
        return $"Warning: {fallback}, so an attacker between here and {string.Join(", ", remote)} could read or " +
               "change the connection, including the password. Set the TLS mode to VerifyFull (or VerifyCA), with " +
               "a CA file if needed, or set it to Required explicitly to accept this.";
    }
}
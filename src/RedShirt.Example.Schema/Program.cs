using MySqlConnector;
using RedShirt.Example.Schema;
using RedShirt.Example.Schema.Exceptions;

const string varHost = "EXAMPLE_SCHEMA_HOST";
const string varName = "EXAMPLE_SCHEMA_NAME";
const string varUser = "EXAMPLE_SCHEMA_USER";
const string varPassword = "EXAMPLE_SCHEMA_PASSWORD";
const string varDryRun = "EXAMPLE_SCHEMA_DRY_RUN";
const string varSslMode = "EXAMPLE_SCHEMA_SSL_MODE";
const string varSslCa = "EXAMPLE_SCHEMA_SSL_CA";

var schemaHost = Environment.GetEnvironmentVariable(varHost);
var schemaName = Environment.GetEnvironmentVariable(varName);
var schemaUser = Environment.GetEnvironmentVariable(varUser);
var schemaPassword = Environment.GetEnvironmentVariable(varPassword);
var schemaSslMode = Environment.GetEnvironmentVariable(varSslMode);
var schemaSslCa = Environment.GetEnvironmentVariable(varSslCa);

if (string.IsNullOrWhiteSpace(schemaHost))
{
    throw new EnvironmentVariableNotFoundException($"{varHost} environment variable not found");
}

if (string.IsNullOrWhiteSpace(schemaName))
{
    throw new EnvironmentVariableNotFoundException($"{varName} environment variable not found");
}

if (string.IsNullOrWhiteSpace(schemaUser))
{
    throw new EnvironmentVariableNotFoundException($"{varUser} environment variable not found");
}

if (string.IsNullOrWhiteSpace(schemaPassword))
{
    throw new EnvironmentVariableNotFoundException($"{varPassword} environment variable not found");
}

// TLS defaults to Required (no plaintext fallback). Set VerifyCA or VerifyFull, with a CA file if needed, to also
// validate the server certificate.
if (!ConnectionStringFactory.TryParseSslMode(schemaSslMode, out var sslMode))
{
    throw new InvalidEnvironmentVariableException(
        $"{varSslMode} must be one of: {string.Join(", ", Enum.GetNames<MySqlSslMode>())}");
}

if (!string.IsNullOrWhiteSpace(schemaSslCa) && !File.Exists(schemaSslCa))
{
    throw new InvalidEnvironmentVariableException($"{varSslCa} file not found: {schemaSslCa}");
}

var connectionString = ConnectionStringFactory.Build(schemaHost, schemaUser, schemaPassword, schemaName, sslMode,
    schemaSslCa);

// Dry run lists pending scripts without applying them. Enable with --dry-run or EXAMPLE_SCHEMA_DRY_RUN=1 (or "true").
var dryRunValue = Environment.GetEnvironmentVariable(varDryRun)?.Trim();
var dryRun = args.Contains("--dry-run")
             || dryRunValue == "1"
             || string.Equals(dryRunValue, "true", StringComparison.OrdinalIgnoreCase);

return dryRun
    ? SchemaUpgrader.DryRun(connectionString, schemaName)
    : SchemaUpgrader.Upgrade(connectionString, schemaName);
using MySqlConnector;
using RedShirt.Example.Schema;
using RedShirt.Example.Schema.Exceptions;

const string varHost = "EXAMPLE_SCHEMA_HOST";
const string varName = "EXAMPLE_SCHEMA_NAME";
const string varJournalSchemaName = "EXAMPLE_SCHEMA_JOURNAL_SCHEMA_NAME";
const string varUser = "EXAMPLE_SCHEMA_USER";
const string varPassword = "EXAMPLE_SCHEMA_PASSWORD";
const string varPasswordFile = "EXAMPLE_SCHEMA_PASSWORD_FILE";
const string varDryRun = "EXAMPLE_SCHEMA_DRY_RUN";
const string varSslMode = "EXAMPLE_SCHEMA_SSL_MODE";
const string varSslCa = "EXAMPLE_SCHEMA_SSL_CA";

var schemaHost = Environment.GetEnvironmentVariable(varHost);
var schemaName = Environment.GetEnvironmentVariable(varName);
var schemaJournalSchemaName = Environment.GetEnvironmentVariable(varJournalSchemaName);
var schemaUser = Environment.GetEnvironmentVariable(varUser);
var schemaPassword = Environment.GetEnvironmentVariable(varPassword);
var schemaPasswordFile = Environment.GetEnvironmentVariable(varPasswordFile);
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

// The password can come from a file instead (for example a mounted secret), which keeps it out of the environment.
if (!string.IsNullOrWhiteSpace(schemaPasswordFile))
{
    if (!string.IsNullOrEmpty(schemaPassword))
    {
        throw new InvalidEnvironmentVariableException($"Set only one of {varPassword} and {varPasswordFile}");
    }

    if (!File.Exists(schemaPasswordFile))
    {
        throw new InvalidEnvironmentVariableException($"{varPasswordFile} file not found: {schemaPasswordFile}");
    }

    schemaPassword = PasswordFile.Read(schemaPasswordFile);
}

if (string.IsNullOrWhiteSpace(schemaPassword))
{
    throw new EnvironmentVariableNotFoundException(
        $"{varPassword} environment variable not found (or {varPasswordFile} is empty)");
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

// The journal and checksum tables live in their own schema (by default the schema name with _changes added), so that
// the application's user can be granted the whole application schema without being able to change them.
var journalSchema = string.IsNullOrWhiteSpace(schemaJournalSchemaName)
    ? SchemaUpgrader.DefaultJournalSchema(schemaName)
    : schemaJournalSchemaName.Trim();

return dryRun
    ? SchemaUpgrader.DryRun(connectionString, schemaName, journalSchema)
    : SchemaUpgrader.Upgrade(connectionString, schemaName, journalSchema: journalSchema);
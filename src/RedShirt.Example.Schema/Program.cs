using RedShirt.Example.Schema;
using RedShirt.Example.Schema.Exceptions;

const string varHost = "EXAMPLE_SCHEMA_HOST";
const string varName = "EXAMPLE_SCHEMA_NAME";
const string varUser = "EXAMPLE_SCHEMA_USER";
const string varPassword = "EXAMPLE_SCHEMA_PASSWORD";
const string varDryRun = "EXAMPLE_SCHEMA_DRY_RUN";

var schemaHost = Environment.GetEnvironmentVariable(varHost);
var schemaName = Environment.GetEnvironmentVariable(varName);
var schemaUser = Environment.GetEnvironmentVariable(varUser);
var schemaPassword = Environment.GetEnvironmentVariable(varPassword);

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

var connectionString = $"server={schemaHost};user={schemaUser};password={schemaPassword};database={schemaName};";

// Dry run lists pending scripts without applying them. Enable with --dry-run or EXAMPLE_SCHEMA_DRY_RUN=1 (or "true").
var dryRunValue = Environment.GetEnvironmentVariable(varDryRun)?.Trim();
var dryRun = args.Contains("--dry-run")
             || dryRunValue == "1"
             || string.Equals(dryRunValue, "true", StringComparison.OrdinalIgnoreCase);

return dryRun
    ? SchemaUpgrader.DryRun(connectionString, schemaName)
    : SchemaUpgrader.Upgrade(connectionString, schemaName);
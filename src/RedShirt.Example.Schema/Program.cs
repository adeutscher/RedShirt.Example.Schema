using DbUp;
using RedShirt.Example.Schema.Exceptions;
using System.Reflection;

const string varHost = "EXAMPLE_SCHEMA_HOST";
const string varName = "EXAMPLE_SCHEMA_NAME";
const string varUser = "EXAMPLE_SCHEMA_USER";
const string varPassword = "EXAMPLE_SCHEMA_PASSWORD";

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

var upgrader = DeployChanges.To
    .MySqlDatabase(connectionString)
    .JournalToMySqlTable(schemaName, "Patches")
    .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
    .LogToConsole()
    .Build();

var result = upgrader.PerformUpgrade();

if (!result.Successful)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(result.Error);
    Console.ResetColor();
    return -1;
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("Success!");
Console.ResetColor();
return 0;
using DbUp;
using DbUp.Engine;

namespace RedShirt.Example.Schema;

internal static class SchemaUpgrader
{
    public const string JournalTableName = "Patches";

    /// <summary>
    ///     Builds the upgrade engine. Each script runs in its own transaction, so a failed script is not journaled and
    ///     its DML is rolled back. Note that MariaDB/MySQL commit DDL statements implicitly, so a failed script may still
    ///     leave behind DDL that ran before the failing statement.
    /// </summary>
    /// <param name="scriptFilter">Optional filter on embedded script names. Defaults to all embedded scripts.</param>
    private static UpgradeEngine BuildEngine(string connectionString, string databaseName,
        Func<string, bool>? scriptFilter = null)
    {
        return DeployChanges.To
            .MySqlDatabase(connectionString)
            .JournalToMySqlTable(databaseName, JournalTableName)
            .WithScriptsEmbeddedInAssembly(typeof(SchemaUpgrader).Assembly, scriptFilter ?? (_ => true))
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();
    }

    /// <summary>
    ///     Lists the embedded scripts that have not yet been journaled, in the order they would run. Does not create the
    ///     journal table or change the database.
    /// </summary>
    public static IReadOnlyList<string> GetPendingScripts(string connectionString, string databaseName)
    {
        return BuildEngine(connectionString, databaseName)
            .GetScriptsToExecute()
            .Select(s => s.Name)
            .ToList();
    }

    public static int DryRun(string connectionString, string databaseName)
    {
        var pending = GetPendingScripts(connectionString, databaseName);

        if (pending.Count == 0)
        {
            Console.WriteLine("Dry run: no pending scripts.");
            return 0;
        }

        Console.WriteLine($"Dry run: {pending.Count} pending script(s) would be applied:");
        foreach (var name in pending)
        {
            Console.WriteLine($"  {name}");
        }

        return 0;
    }

    public static int Upgrade(string connectionString, string databaseName, Func<string, bool>? scriptFilter = null)
    {
        var upgrader = BuildEngine(connectionString, databaseName, scriptFilter);

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
    }
}

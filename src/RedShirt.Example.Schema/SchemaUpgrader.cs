using DbUp;

namespace RedShirt.Example.Schema;

public static class SchemaUpgrader
{
    public const string JournalTableName = "Patches";

    public static int Upgrade(string connectionString, string databaseName)
    {
        var upgrader = DeployChanges.To
            .MySqlDatabase(connectionString)
            .JournalToMySqlTable(databaseName, JournalTableName)
            .WithScriptsEmbeddedInAssembly(typeof(SchemaUpgrader).Assembly)
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
    }
}
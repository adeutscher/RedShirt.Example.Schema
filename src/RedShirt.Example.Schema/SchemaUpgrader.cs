using DbUp;
using DbUp.Engine;
using DbUp.MySql;
using MySqlConnector;

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
            .JournalTo((connectionManager, log) => new ChecksumJournal(
                new MySqlTableJournal(connectionManager, log, databaseName, JournalTableName), databaseName))
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

    /// <summary>
    ///     Result of comparing applied scripts with the embedded scripts. Problems block an upgrade. Unrecorded scripts
    ///     were applied before checksums were kept, and have their checksums recorded on the next upgrade.
    /// </summary>
    internal sealed record IntegrityReport(
        IReadOnlyList<string> Problems,
        IReadOnlyList<(string Name, string Sha256)> Unrecorded);

    /// <summary>
    ///     Checks that every applied script still matches its recorded checksum and still exists. Does not change the
    ///     database.
    /// </summary>
    internal static IntegrityReport CheckIntegrity(UpgradeEngine engine, string connectionString,
        string databaseName)
    {
        var problems = new List<string>();
        var unrecorded = new List<(string, string)>();

        var executed = engine.GetExecutedScripts().ToHashSet(StringComparer.Ordinal);
        if (executed.Count == 0)
        {
            return new IntegrityReport(problems, unrecorded);
        }

        var recorded = ReadChecksums(connectionString, databaseName);

        foreach (var script in engine.GetDiscoveredScripts().Where(s => executed.Contains(s.Name)))
        {
            var actual = ScriptChecksums.Compute(script.Contents);
            if (!recorded.TryGetValue(script.Name, out var expected))
            {
                unrecorded.Add((script.Name, actual));
            }
            else if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                problems.Add($"{script.Name}: changed since it was applied (checksum {expected}, now {actual}). " +
                             "Restore the original and add a new script instead.");
            }
        }

        foreach (var name in engine.GetExecutedButNotDiscoveredScripts())
        {
            problems.Add($"{name}: applied, but no longer embedded. It was renamed or deleted. A renamed script " +
                         "would run again. Restore it under its original name.");
        }

        return new IntegrityReport(problems, unrecorded);
    }

    private static Dictionary<string, string> ReadChecksums(string connectionString, string databaseName)
    {
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);

        using var connection = new MySqlConnection(connectionString);
        connection.Open();

        using (var exists = new MySqlCommand(
                   """
                   SELECT COUNT(*) FROM information_schema.TABLES
                   WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @table
                   """, connection))
        {
            exists.Parameters.AddWithValue("@database", databaseName);
            exists.Parameters.AddWithValue("@table", ScriptChecksums.TableName);
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
            {
                return checksums;
            }
        }

        using var select = new MySqlCommand(ScriptChecksums.SelectSql(databaseName), connection);
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            checksums[reader.GetString(0)] = reader.GetString(1);
        }

        return checksums;
    }

    private static void RecordChecksums(string connectionString, string databaseName,
        IReadOnlyList<(string Name, string Sha256)> scripts)
    {
        using var connection = new MySqlConnection(connectionString);
        connection.Open();

        using (var create = new MySqlCommand(ScriptChecksums.CreateTableSql(databaseName), connection))
        {
            create.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (name, sha256) in scripts)
        {
            using var insert = new MySqlCommand(ScriptChecksums.InsertSql(databaseName), connection, transaction);
            ScriptChecksums.AddInsertParameters(insert, name, sha256);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void WriteProblems(IReadOnlyList<string> problems)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"{problems.Count} applied script(s) do not match what is embedded:");
        foreach (var problem in problems)
        {
            Console.WriteLine($"  {problem}");
        }

        Console.ResetColor();
    }

    public static int DryRun(string connectionString, string databaseName)
    {
        var engine = BuildEngine(connectionString, databaseName);
        var integrity = CheckIntegrity(engine, connectionString, databaseName);
        if (integrity.Problems.Count > 0)
        {
            WriteProblems(integrity.Problems);
            return -1;
        }

        if (integrity.Unrecorded.Count > 0)
        {
            Console.WriteLine($"Dry run: checksums would be recorded for {integrity.Unrecorded.Count} applied " +
                              "script(s) that have none yet.");
        }

        var pending = engine.GetScriptsToExecute().Select(s => s.Name).ToList();

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

        var integrity = CheckIntegrity(upgrader, connectionString, databaseName);
        if (integrity.Problems.Count > 0)
        {
            WriteProblems(integrity.Problems);
            return -1;
        }

        if (integrity.Unrecorded.Count > 0)
        {
            // Scripts applied before checksums were kept are trusted as they are now.
            RecordChecksums(connectionString, databaseName, integrity.Unrecorded);
            Console.WriteLine($"Recorded checksums for {integrity.Unrecorded.Count} previously applied script(s).");
        }

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

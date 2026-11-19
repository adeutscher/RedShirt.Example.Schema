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
    /// <param name="databaseName"></param>
    /// <param name="scriptFilter">Optional filter on embedded script names. Defaults to all embedded scripts.</param>
    /// <param name="connectionString"></param>
    private static UpgradeEngine BuildEngine(string connectionString, string databaseName,
        Func<string, bool>? scriptFilter = null)
    {
        return DeployChanges.To
            .MySqlDatabase(connectionString)
            // Define a custom journal
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

    /// <summary>How long an upgrade waits for another upgrade of the same database to finish.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Name of the server-wide lock that keeps two upgrades of the same database from running at once. MariaDB/MySQL
    ///     lock names are at most 64 characters, so long database names are replaced by a hash.
    /// </summary>
    internal static string LockName(string databaseName)
    {
        var name = $"schema-upgrade:{databaseName}";
        return name.Length <= 64
            ? name
            : $"schema-upgrade:{ScriptChecksums.Compute(databaseName)[..40]}";
    }

    /// <summary>
    ///     Holds the upgrade lock on its own connection. Disposing releases the lock explicitly, since a pooled connection
    ///     goes back to the pool rather than ending its session. If the process dies, the server releases the lock when
    ///     the session ends.
    /// </summary>
    private sealed class UpgradeLock(MySqlConnection connection, string name) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                using var command = new MySqlCommand("SELECT RELEASE_LOCK(@name)", connection);
                command.Parameters.AddWithValue("@name", name);
                command.ExecuteScalar();
            }
            finally
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>Takes the upgrade lock. Returns null if the lock could not be taken in time.</summary>
    private static UpgradeLock? AcquireUpgradeLock(string connectionString, string databaseName, TimeSpan timeout)
    {
        var connection = new MySqlConnection(connectionString);
        try
        {
            connection.Open();

            if (TryGetLock(connection, databaseName, TimeSpan.Zero))
            {
                return new UpgradeLock(connection, LockName(databaseName));
            }

            Console.WriteLine($"Another upgrade of {databaseName} is running. Waiting up to {timeout} for it to finish...");
            if (TryGetLock(connection, databaseName, timeout))
            {
                return new UpgradeLock(connection, LockName(databaseName));
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        connection.Dispose();
        return null;
    }

    private static bool TryGetLock(MySqlConnection connection, string databaseName, TimeSpan timeout)
    {
        using var command = new MySqlCommand("SELECT GET_LOCK(@name, @timeout)", connection);
        command.Parameters.AddWithValue("@name", LockName(databaseName));
        command.Parameters.AddWithValue("@timeout", (int)Math.Ceiling(timeout.TotalSeconds));
        // GET_LOCK waits on the server, so the command must not time out first.
        command.CommandTimeout = 0;
        // 1 when taken, 0 on timeout, NULL on error.
        return command.ExecuteScalar() is { } result and not DBNull && Convert.ToInt32(result) == 1;
    }

    public static int Upgrade(string connectionString, string databaseName, Func<string, bool>? scriptFilter = null,
        TimeSpan? lockTimeout = null)
    {
        // Two upgrades running at once would both see the same pending scripts and both run them. DbUp's MySQL support
        // takes no lock, so hold a named server lock for the whole check and upgrade.
        var timeout = lockTimeout ?? DefaultLockTimeout;
        using var upgradeLock = AcquireUpgradeLock(connectionString, databaseName, timeout);
        if (upgradeLock is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Another upgrade of {databaseName} did not finish within {timeout}. Nothing was applied.");
            Console.ResetColor();
            return -1;
        }

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

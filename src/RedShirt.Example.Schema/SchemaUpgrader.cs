using DbUp;
using DbUp.Engine;
using DbUp.MySql;
using MySqlConnector;

namespace RedShirt.Example.Schema;

internal static class SchemaUpgrader
{
    /// <summary>Default name of the DbUp journal table, which lists the applied scripts.</summary>
    public const string DefaultJournalTable = "Patches";

    /// <summary>The journal and checksum tables, which move together between schemas.</summary>
    private static string[] JournalTables(string journalTable)
    {
        return [journalTable, ScriptChecksums.TableName];
    }

    /// <summary>
    ///     Default schema for the journal and checksum tables: the application schema's name with <c>_changes</c> added.
    ///     Keeping them out of the application schema means that the application's user can be granted that whole schema
    ///     without being able to change the journal.
    /// </summary>
    public static string DefaultJournalSchema(string databaseName)
    {
        var name = $"{databaseName}_changes";
        // MariaDB/MySQL schema names are at most 64 characters.
        return name.Length <= 64
            ? name
            : throw new ArgumentException(
                $"Default journal schema name {name} is longer than 64 characters. Set the journal schema explicitly.",
                nameof(databaseName));
    }

    /// <summary>
    ///     Builds the upgrade engine. Each script runs in its own transaction, so a failed script is not journaled and
    ///     its DML is rolled back. Note that MariaDB/MySQL commit DDL statements implicitly, so a failed script may still
    ///     leave behind DDL that ran before the failing statement.
    /// </summary>
    /// <param name="connectionString"></param>
    /// <param name="journalSchema">Schema that holds the journal and checksum tables.</param>
    /// <param name="journalTable">Name of the journal table.</param>
    /// <param name="scriptFilter">Optional filter on embedded script names. Defaults to all embedded scripts.</param>
    private static UpgradeEngine BuildEngine(string connectionString, string journalSchema, string journalTable,
        Func<string, bool>? scriptFilter = null)
    {
        return DeployChanges.To
            .MySqlDatabase(connectionString)
            // Define a custom journal
            .JournalTo((connectionManager, log) => new ChecksumJournal(
                new MySqlTableJournal(connectionManager, log, journalSchema, journalTable), journalSchema))
            .WithScriptsEmbeddedInAssembly(typeof(SchemaUpgrader).Assembly, scriptFilter ?? (_ => true))
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();
    }

    /// <summary>
    ///     Lists the embedded scripts that have not yet been journaled, in the order they would run. Does not create the
    ///     journal table or change the database.
    /// </summary>
    public static IReadOnlyList<string> GetPendingScripts(string connectionString, string databaseName,
        string? journalSchema = null, string? journalTable = null)
    {
        return BuildEngine(connectionString, journalSchema ?? DefaultJournalSchema(databaseName),
                journalTable ?? DefaultJournalTable)
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
        string journalSchema)
    {
        var problems = new List<string>();
        var unrecorded = new List<(string, string)>();

        var executed = engine.GetExecutedScripts().ToHashSet(StringComparer.Ordinal);
        if (executed.Count == 0)
        {
            return new IntegrityReport(problems, unrecorded);
        }

        var recorded = ReadChecksums(connectionString, journalSchema);

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

    /// <summary>
    ///     Checks for checksums of applied scripts while the journal is missing or empty. Checksums are only recorded for
    ///     applied scripts, so this means that the journal is somewhere else: its table was renamed, or the journal
    ///     schema setting no longer points at it. Upgrading would start an empty journal and run every script again.
    ///     Looks in the journal schema, the application schema and the default journal schema. Returns the problem, or
    ///     null. Does not change the database.
    /// </summary>
    internal static string? FindOrphanedChecksums(UpgradeEngine engine, string connectionString, string databaseName,
        string journalSchema, string journalTable)
    {
        if (engine.GetExecutedScripts().Count > 0)
        {
            return null;
        }

        var schemas = new List<string> {journalSchema, databaseName};
        if (DefaultJournalSchemaOrNull(databaseName) is { } defaultSchema)
        {
            schemas.Add(defaultSchema);
        }

        using var connection = new MySqlConnection(connectionString);
        connection.Open();

        foreach (var schema in schemas.Distinct(StringComparer.Ordinal))
        {
            // Not visible without privileges on it, in which case it cannot be checked.
            if (!TableExists(connection, schema, ScriptChecksums.TableName))
            {
                continue;
            }

            long count;
            try
            {
                using var command = new MySqlCommand(
                    $"SELECT COUNT(*) FROM {ScriptChecksums.QuoteIdentifier(schema)}." +
                    ScriptChecksums.QuoteIdentifier(ScriptChecksums.TableName), connection);
                count = Convert.ToInt64(command.ExecuteScalar());
            }
            catch (MySqlException e) when (e.ErrorCode is MySqlErrorCode.TableAccessDenied
                                               or MySqlErrorCode.DatabaseAccessDenied)
            {
                continue;
            }

            if (count > 0)
            {
                return $"{schema}.{ScriptChecksums.TableName} has checksums for {count} applied script(s), but the " +
                       $"journal {journalSchema}.{journalTable} is missing or empty, so every script would run " +
                       "again. Check that the journal schema and table name settings point at the existing journal. " +
                       $"To start over deliberately, delete the rows in {schema}.{ScriptChecksums.TableName} first.";
            }
        }

        return null;
    }

    private static string? DefaultJournalSchemaOrNull(string databaseName)
    {
        try
        {
            return DefaultJournalSchema(databaseName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TableExists(MySqlConnection connection, string schema, string table)
    {
        using var command = new MySqlCommand(
            """
            SELECT COUNT(*) FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            """, connection);
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    ///     Where the journal is, relative to where it should be. <see cref="TablesToMove" /> are journal tables still in
    ///     the application schema, from before the journal had its own schema.
    /// </summary>
    private sealed record JournalLocation(string? Problem, IReadOnlyList<string> TablesToMove);

    /// <summary>
    ///     Checks that the journal schema exists and finds journal tables left in the application schema. Moving them
    ///     matters: an upgrade that read an empty journal in the new schema would run every script again.
    /// </summary>
    private static JournalLocation LocateJournal(string connectionString, string databaseName, string journalSchema,
        string journalTable)
    {
        if (string.Equals(journalSchema, databaseName, StringComparison.Ordinal))
        {
            return new JournalLocation(null, []);
        }

        using var connection = new MySqlConnection(connectionString);
        connection.Open();

        // SCHEMATA lists only schemas that the user has privileges on, so a missing grant looks like a missing schema.
        using (var exists = new MySqlCommand(
                   "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @schema", connection))
        {
            exists.Parameters.AddWithValue("@schema", journalSchema);
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
            {
                return new JournalLocation(
                    $"Journal schema {journalSchema} does not exist, or this user has no privileges on it. An admin " +
                    "must create it and grant the migration user CREATE, SELECT and INSERT on it. To keep the " +
                    $"journal in {databaseName} instead, set the journal schema to {databaseName}.", []);
            }
        }

        var tables = JournalTables(journalTable);
        var old = tables.Where(t => TableExists(connection, databaseName, t)).ToList();
        var current = tables.Where(t => TableExists(connection, journalSchema, t)).ToList();

        if (old.Count > 0 && current.Count > 0)
        {
            return new JournalLocation(
                $"Journal tables exist in both {databaseName} ({string.Join(", ", old)}) and {journalSchema} " +
                $"({string.Join(", ", current)}). Work out which journal is right and remove the other one.", []);
        }

        return new JournalLocation(null, old);
    }

    /// <summary>Moves the journal tables in one statement, so that they move together or not at all.</summary>
    private static void MoveJournal(string connectionString, string databaseName, string journalSchema,
        IReadOnlyList<string> tables)
    {
        var renames = tables.Select(t =>
            $"{ScriptChecksums.QuoteIdentifier(databaseName)}.{ScriptChecksums.QuoteIdentifier(t)} TO " +
            $"{ScriptChecksums.QuoteIdentifier(journalSchema)}.{ScriptChecksums.QuoteIdentifier(t)}");

        using var connection = new MySqlConnection(connectionString);
        connection.Open();
        using var command = new MySqlCommand($"RENAME TABLE {string.Join(", ", renames)}", connection);
        command.ExecuteNonQuery();

        Console.WriteLine($"Moved {string.Join(", ", tables)} from {databaseName} to {journalSchema}.");
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static Dictionary<string, string> ReadChecksums(string connectionString, string journalSchema)
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
            exists.Parameters.AddWithValue("@database", journalSchema);
            exists.Parameters.AddWithValue("@table", ScriptChecksums.TableName);
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
            {
                return checksums;
            }
        }

        using var select = new MySqlCommand(ScriptChecksums.SelectSql(journalSchema), connection);
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            checksums[reader.GetString(0)] = reader.GetString(1);
        }

        return checksums;
    }

    private static void RecordChecksums(string connectionString, string journalSchema,
        IReadOnlyList<(string Name, string Sha256)> scripts)
    {
        using var connection = new MySqlConnection(connectionString);
        connection.Open();

        using (var create = new MySqlCommand(ScriptChecksums.CreateTableSql(journalSchema), connection))
        {
            create.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (name, sha256) in scripts)
        {
            using var insert = new MySqlCommand(ScriptChecksums.InsertSql(journalSchema), connection, transaction);
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

    /// <param name="connectionString"></param>
    /// <param name="databaseName">Schema that the update scripts change.</param>
    /// <param name="journalSchema">
    ///     Schema that holds the journal and checksum tables. Defaults to <see cref="DefaultJournalSchema" />.
    /// </param>
    /// <param name="journalTable">Name of the journal table. Defaults to <see cref="DefaultJournalTable" />.</param>
    public static int DryRun(string connectionString, string databaseName, string? journalSchema = null,
        string? journalTable = null)
    {
        var journal = journalSchema ?? DefaultJournalSchema(databaseName);
        var table = journalTable ?? DefaultJournalTable;

        var location = LocateJournal(connectionString, databaseName, journal, table);
        if (location.Problem is not null)
        {
            WriteError(location.Problem);
            return -1;
        }

        if (location.TablesToMove.Count > 0)
        {
            // Report from the journal where it is now, since nothing is moved in a dry run.
            Console.WriteLine($"Dry run: {string.Join(", ", location.TablesToMove)} would be moved from " +
                              $"{databaseName} to {journal}.");
            journal = databaseName;
        }

        var engine = BuildEngine(connectionString, journal, table);

        if (FindOrphanedChecksums(engine, connectionString, databaseName, journal, table) is { } orphaned)
        {
            WriteError(orphaned);
            return -1;
        }

        var integrity = CheckIntegrity(engine, connectionString, journal);
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

    /// <param name="connectionString"></param>
    /// <param name="databaseName">Schema that the update scripts change.</param>
    /// <param name="scriptFilter">Optional filter on embedded script names. Defaults to all embedded scripts.</param>
    /// <param name="lockTimeout">How long to wait for another upgrade. Defaults to <see cref="DefaultLockTimeout" />.</param>
    /// <param name="journalSchema">
    ///     Schema that holds the journal and checksum tables. Defaults to <see cref="DefaultJournalSchema" />.
    /// </param>
    /// <param name="journalTable">Name of the journal table. Defaults to <see cref="DefaultJournalTable" />.</param>
    public static int Upgrade(string connectionString, string databaseName, Func<string, bool>? scriptFilter = null,
        TimeSpan? lockTimeout = null, string? journalSchema = null, string? journalTable = null)
    {
        var journal = journalSchema ?? DefaultJournalSchema(databaseName);
        var table = journalTable ?? DefaultJournalTable;

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

        // Under the lock, so that two upgrades do not both try to move the journal.
        var location = LocateJournal(connectionString, databaseName, journal, table);
        if (location.Problem is not null)
        {
            WriteError(location.Problem);
            return -1;
        }

        if (location.TablesToMove.Count > 0)
        {
            MoveJournal(connectionString, databaseName, journal, location.TablesToMove);
        }

        var upgrader = BuildEngine(connectionString, journal, table, scriptFilter);

        if (FindOrphanedChecksums(upgrader, connectionString, databaseName, journal, table) is { } orphaned)
        {
            WriteError(orphaned);
            return -1;
        }

        var integrity = CheckIntegrity(upgrader, connectionString, journal);
        if (integrity.Problems.Count > 0)
        {
            WriteProblems(integrity.Problems);
            return -1;
        }

        if (integrity.Unrecorded.Count > 0)
        {
            // Scripts applied before checksums were kept are trusted as they are now.
            RecordChecksums(connectionString, journal, integrity.Unrecorded);
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

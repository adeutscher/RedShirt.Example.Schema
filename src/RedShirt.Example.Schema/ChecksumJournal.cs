using System.Data;
using DbUp.Engine;

namespace RedShirt.Example.Schema;

/// <summary>
///     Wraps the DbUp journal so that each script's checksum is stored along with its journal entry, using the same
///     command factory and so the same per-script transaction.
/// </summary>
internal sealed class ChecksumJournal(IJournal inner, string databaseName) : IJournal
{
    private bool _checksumTableEnsured;

    public string[] GetExecutedScripts()
    {
        return inner.GetExecutedScripts();
    }

    public void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
    {
        inner.StoreExecutedScript(script, dbCommandFactory);

        using var command = dbCommandFactory();
        command.CommandText = ScriptChecksums.InsertSql(databaseName);
        ScriptChecksums.AddInsertParameters(command, script.Name, ScriptChecksums.Compute(script.Contents));
        command.ExecuteNonQuery();
    }

    public void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory)
    {
        inner.EnsureTableExistsAndIsLatestVersion(dbCommandFactory);

        // DbUp calls this before every script. CREATE TABLE commits implicitly on MariaDB/MySQL, so run it only once,
        // before the first script, rather than inside every script's transaction.
        if (_checksumTableEnsured)
        {
            return;
        }

        using var command = dbCommandFactory();
        command.CommandText = ScriptChecksums.CreateTableSql(databaseName);
        command.ExecuteNonQuery();
        _checksumTableEnsured = true;
    }
}

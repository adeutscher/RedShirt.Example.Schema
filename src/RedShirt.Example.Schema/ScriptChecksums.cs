using System.Data;
using System.Security.Cryptography;
using System.Text;

namespace RedShirt.Example.Schema;

/// <summary>
///     SHA-256 checksums of applied update scripts, kept next to the DbUp journal. DbUp journals scripts by name only, so
///     without these an applied script could be edited without anything noticing.
/// </summary>
internal static class ScriptChecksums
{
    public const string TableName = "PatchChecksums";

    /// <summary>
    ///     Checksum of a script's contents. Line endings are normalised and a leading byte order mark is ignored, so that
    ///     the same script checked out on Windows and Linux has the same checksum.
    /// </summary>
    public static string Compute(string contents)
    {
        var normalised = contents.TrimStart('﻿').Replace("\r\n", "\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }

    public static string QuoteIdentifier(string name)
    {
        return $"`{name.Replace("`", "``")}`";
    }

    private static string QualifiedTableName(string databaseName)
    {
        return $"{QuoteIdentifier(databaseName)}.{QuoteIdentifier(TableName)}";
    }

    public static string CreateTableSql(string databaseName)
    {
        return $"""
                CREATE TABLE IF NOT EXISTS {QualifiedTableName(databaseName)}
                (
                    `ScriptName` VARCHAR(255) NOT NULL,
                    `Sha256` CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                    `RecordedAtUtc` DATETIME(6) NOT NULL,
                    PRIMARY KEY (`ScriptName`)
                )
                """;
    }

    public static string SelectSql(string databaseName)
    {
        return $"SELECT `ScriptName`, `Sha256` FROM {QualifiedTableName(databaseName)}";
    }

    public static string InsertSql(string databaseName)
    {
        return $"""
                INSERT INTO {QualifiedTableName(databaseName)} (`ScriptName`, `Sha256`, `RecordedAtUtc`)
                VALUES (@scriptName, @sha256, UTC_TIMESTAMP(6))
                """;
    }

    public static void AddInsertParameters(IDbCommand command, string scriptName, string sha256)
    {
        AddParameter(command, "@scriptName", scriptName);
        AddParameter(command, "@sha256", sha256);
    }

    private static void AddParameter(IDbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

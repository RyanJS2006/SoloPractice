using Microsoft.Data.Sqlite;
using SoloPractice.Utilities;

namespace SoloPractice.Data;

internal static class Database
{
    private static string ConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

    public static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        ExecuteNonQuery(
            connection,
            """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """);

        return connection;
    }

    public static void Initialize()
    {
        AppPaths.EnsureApplicationDirectoriesExist();

        using var connection = OpenConnection();

        ExecuteNonQuery(connection, LoadSchemaSql());
        ValidateForeignKeys(connection);
        ExecuteNonQuery(connection, "PRAGMA optimize;");
    }

    private static string LoadSchemaSql()
    {
        const string suffix = ".Data.schema.sql";
        System.Reflection.Assembly assembly = typeof(Database).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema resource {resourceName} was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void ValidateForeignKeys(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
            return;

        string childTable = reader.GetString(0);
        string rowId = reader.IsDBNull(1)
            ? "(without rowid)"
            : reader.GetValue(1).ToString() ?? "?";
        string parentTable = reader.GetString(2);

        throw new InvalidDataException(
            $"Foreign-key validation failed: {childTable} row {rowId} " +
            $"references a missing row in {parentTable}.");
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

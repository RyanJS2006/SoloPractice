using Microsoft.Data.Sqlite;
using SoloPractice.Utilities;
using System.Reflection;

namespace SoloPractice.Data;

internal static class Database
{
    private const int SchemaVersion = 2;
    private const int ApplicationId = 1397705807;

    private static string DatabasePath =>
        Environment.GetEnvironmentVariable("SOLOPRACTICE_DATABASE_PATH")
        ?? AppPaths.DatabasePath;

    private static string ConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

    public static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    public static void Initialize()
    {
        string? directory = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrEmpty(directory))
            AppPaths.EnsureDirectoriesExist();
        else
            Directory.CreateDirectory(directory);
        using var connection = OpenConnection();

        long version;
        long applicationId;
        using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText = "PRAGMA user_version;";
            version = (long)(metadata.ExecuteScalar() ?? 0L);
            metadata.CommandText = "PRAGMA application_id;";
            applicationId = (long)(metadata.ExecuteScalar() ?? 0L);
        }

        bool hasSchema;
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='Transactions');";
            hasSchema = Convert.ToInt64(probe.ExecuteScalar()) != 0;
        }

        if (hasSchema && (version != SchemaVersion || applicationId != ApplicationId))
        {
            throw new InvalidOperationException(
                "This database uses an older SoloPractice schema. Recreate it and re-import the preserved Chase CSV downloads (schema v2 is intentionally a clean rebuild).");
        }

        if (!hasSchema)
        {
            using Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("SoloPractice.Data.schema.sql")
                ?? throw new InvalidOperationException("Embedded database schema was not found.");
            using var reader = new StreamReader(stream);
            using var create = connection.CreateCommand();
            create.CommandText = reader.ReadToEnd();
            create.ExecuteNonQuery();
        }

        using var optimize = connection.CreateCommand();
        optimize.CommandText = "PRAGMA optimize;";
        optimize.ExecuteNonQuery();
    }
}

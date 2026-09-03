using Microsoft.Data.Sqlite;
using SoloPractice.Data;
using SoloPractice.Utilities;

namespace SoloPractice.Services;

internal sealed record DatabaseBackupResult(
    string BackupPath,
    long Bytes,
    int SchemaVersion);

internal static class DatabaseBackupService
{
    private const string TemporaryBackupFileName = "SoloPractice_bak.tmp.db";

    public static DatabaseBackupResult? CreateVerifiedBackup()
    {
        if (!File.Exists(AppPaths.DatabasePath))
            return null;

        using SqliteConnection connection = Database.OpenConnection();
        return CreateVerifiedBackup(connection);
    }

    internal static DatabaseBackupResult CreateVerifiedBackup(
        SqliteConnection sourceConnection)
    {
        AppPaths.EnsureApplicationDirectoriesExist();
        string temporaryPath = Path.Combine(
            AppPaths.BackupsDirectory,
            TemporaryBackupFileName);
        string destinationPath = AppPaths.DatabaseBackupPath;

        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);

        int sourceVersion = ReadIntPragma(sourceConnection, "user_version");

        try
        {
            using (SqliteCommand vacuum = sourceConnection.CreateCommand())
            {
                vacuum.CommandText = "VACUUM INTO $destination;";
                vacuum.Parameters.AddWithValue("$destination", temporaryPath);
                vacuum.CommandTimeout = 120;
                vacuum.ExecuteNonQuery();
            }

            VerifyBackup(temporaryPath, sourceVersion);
            ReplaceSafely(temporaryPath, destinationPath);

            return new DatabaseBackupResult(
                destinationPath,
                new FileInfo(destinationPath).Length,
                sourceVersion);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private static void VerifyBackup(string path, int expectedVersion)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            string result = Convert.ToString(integrity.ExecuteScalar()) ?? string.Empty;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Database backup integrity check failed: {result}");
        }

        using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = foreignKeys.ExecuteReader();
            if (reader.Read())
                throw new InvalidDataException("Database backup contains foreign-key violations.");
        }

        if (ReadIntPragma(connection, "user_version") != expectedVersion)
            throw new InvalidDataException("Database backup schema version does not match the source database.");

        foreach (string requiredTable in new[] { "Accounts", "Transactions", "ImportFiles" })
        {
            using SqliteCommand table = connection.CreateCommand();
            table.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);";
            table.Parameters.AddWithValue("$name", requiredTable);
            if (Convert.ToInt64(table.ExecuteScalar()) == 0)
                throw new InvalidDataException($"Database backup is missing required table {requiredTable}.");
        }
    }

    private static void ReplaceSafely(string temporaryPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(temporaryPath, destinationPath);
            return;
        }

        try
        {
            File.Replace(
                temporaryPath,
                destinationPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
    }

    private static int ReadIntPragma(SqliteConnection connection, string pragma)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup must never hide the backup failure that caused it.
        }
        catch (UnauthorizedAccessException)
        {
            // The next backup attempt will retry stale-temp cleanup explicitly.
        }
    }
}

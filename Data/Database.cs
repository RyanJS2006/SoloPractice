using SoloPractice.Utilities;
using Microsoft.Data.Sqlite;

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

        using var command = connection.CreateCommand();

        command.CommandText = """
            PRAGMA foreign_keys = ON;
            """;

        command.ExecuteNonQuery();

        return connection;
    }

    public static void Initialize()
    {
        AppPaths.EnsureDirectoriesExist();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS Accounts
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                Name                TEXT NOT NULL,
                ChaseLast4          TEXT,
                WorkbookSheetName   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Imports
            (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                AccountId           INTEGER NOT NULL,
                SourceFileName      TEXT NOT NULL,
                SourceFileSha256    TEXT NOT NULL UNIQUE,
                ImportedAtUtc       TEXT NOT NULL,

                FOREIGN KEY (AccountId)
                    REFERENCES Accounts(Id)
            );

            CREATE TABLE IF NOT EXISTS Transactions
            (
                Id                  TEXT PRIMARY KEY,

                AccountId           INTEGER NOT NULL,
                ImportId            INTEGER NOT NULL,

                Fingerprint         TEXT NOT NULL,
                OccurrenceIndex     INTEGER NOT NULL DEFAULT 0,

                Details             TEXT,
                PostingDate         TEXT NOT NULL,

                RawDescription      TEXT NOT NULL,
                UserDescription     TEXT,

                AmountCents         INTEGER NOT NULL,
                Type                TEXT,

                BalanceCents        INTEGER,
                CheckOrSlipNumber   TEXT,

                Explanation         TEXT,

                CreatedAtUtc        TEXT NOT NULL,
                UpdatedAtUtc        TEXT NOT NULL,

                UNIQUE
                (
                    AccountId,
                    Fingerprint,
                    OccurrenceIndex
                ),

                FOREIGN KEY (AccountId)
                    REFERENCES Accounts(Id),

                FOREIGN KEY (ImportId)
                    REFERENCES Imports(Id)
            );

            CREATE TABLE IF NOT EXISTS Categories
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                Name            TEXT NOT NULL UNIQUE,
                SortOrder       INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS TransactionAllocations
            (
                TransactionId   TEXT NOT NULL,
                CategoryId      INTEGER NOT NULL,
                AmountCents     INTEGER NOT NULL,

                PRIMARY KEY
                (
                    TransactionId,
                    CategoryId
                ),

                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE,

                FOREIGN KEY (CategoryId)
                    REFERENCES Categories(Id)
            );

            CREATE TABLE IF NOT EXISTS WorkbookSync
            (
                Id                      INTEGER PRIMARY KEY
                                            CHECK (Id = 1),

                WorkbookPath            TEXT,
                LastWorkbookSha256      TEXT,
                LastSyncedAtUtc         TEXT
            );
            """;

        command.ExecuteNonQuery();
    }
}
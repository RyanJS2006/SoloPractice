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
            PRAGMA foreign_keys = ON;
            
            CREATE TABLE IF NOT EXISTS Accounts
            (
                Last4 TEXT PRIMARY KEY
                    CHECK (
                        length(Last4) = 4
                        AND Last4 GLOB '[0-9][0-9][0-9][0-9]'
                    )
            ) STRICT;
            
            -- Date-only values are stored as Unix seconds at 00:00:00 UTC.
            -- This keeps one integer representation and lets SQLite use date(x, 'unixepoch').
            CREATE TABLE IF NOT EXISTS DateValues
            (
                UnixSeconds INTEGER PRIMARY KEY
                    CHECK (UnixSeconds % 86400 = 0)
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS MoneyValues
            (
                Cents INTEGER PRIMARY KEY
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS TransactionTypes
            (
                Code TEXT PRIMARY KEY
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS DepositDetails
            (
                Code TEXT PRIMARY KEY
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS ImportFormats
            (
                Name TEXT PRIMARY KEY
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS ImportFiles
            (
                FileSha256              BLOB PRIMARY KEY
                    CHECK (length(FileSha256) = 32),
            
                AccountLast4            TEXT NOT NULL,
                FormatName              TEXT NOT NULL,
                DownloadDateUnixSeconds INTEGER NOT NULL,
                ImportedAtUnixSeconds   INTEGER NOT NULL,
            
                FOREIGN KEY (AccountLast4)
                    REFERENCES Accounts(Last4),
            
                FOREIGN KEY (FormatName)
                    REFERENCES ImportFormats(Name),
            
                FOREIGN KEY (DownloadDateUnixSeconds)
                    REFERENCES DateValues(UnixSeconds)
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS Transactions
            (
                Id                     INTEGER PRIMARY KEY,
                AccountLast4           TEXT NOT NULL,
                PostingDateUnixSeconds INTEGER NOT NULL,
                AmountCents            INTEGER NOT NULL,
                TypeCode               TEXT NOT NULL,
            
                FOREIGN KEY (AccountLast4)
                    REFERENCES Accounts(Last4),
            
                FOREIGN KEY (PostingDateUnixSeconds)
                    REFERENCES DateValues(UnixSeconds),
            
                FOREIGN KEY (AmountCents)
                    REFERENCES MoneyValues(Cents),
            
                FOREIGN KEY (TypeCode)
                    REFERENCES TransactionTypes(Code)
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS DepositTransactions
            (
                TransactionId     INTEGER PRIMARY KEY,
                DetailsCode       TEXT NOT NULL,
                BalanceCents      INTEGER,
                CheckOrSlipNumber TEXT,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE,
            
                FOREIGN KEY (DetailsCode)
                    REFERENCES DepositDetails(Code),
            
                FOREIGN KEY (BalanceCents)
                    REFERENCES MoneyValues(Cents)
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS CreditCardMerchants
            (
                Name TEXT PRIMARY KEY
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS CreditCardCategories
            (
                Name TEXT PRIMARY KEY
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS CreditCardTransactions
            (
                TransactionId             INTEGER PRIMARY KEY,
                TransactionDateUnixSeconds INTEGER NOT NULL,
                MerchantName              TEXT NOT NULL,
                CategoryName              TEXT,
                Memo                      TEXT,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE,
            
                FOREIGN KEY (TransactionDateUnixSeconds)
                    REFERENCES DateValues(UnixSeconds),
            
                FOREIGN KEY (MerchantName)
                    REFERENCES CreditCardMerchants(Name),
            
                FOREIGN KEY (CategoryName)
                    REFERENCES CreditCardCategories(Name)
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS AchOriginators
            (
                Id          INTEGER PRIMARY KEY,
                CompanyId   TEXT NOT NULL,
                CompanyName TEXT NOT NULL,
            
                UNIQUE (CompanyId, CompanyName)
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS AchEntryDescriptions
            (
                Description TEXT PRIMARY KEY
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS AchSecCodes
            (
                Code TEXT PRIMARY KEY
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS AchBankReferenceKinds
            (
                Name TEXT PRIMARY KEY
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS AchTransactions
            (
                TransactionId                 INTEGER PRIMARY KEY,
                OriginatorId                  INTEGER NOT NULL,
                CompanyDescriptiveDate        TEXT,
                EntryDescription              TEXT NOT NULL,
                SecCode                       TEXT NOT NULL,
                TraceNumber                   TEXT,
                EffectiveEntryDateUnixSeconds INTEGER,
                IndividualId                  TEXT,
                IndividualName                TEXT,
                PaymentRelatedInformation     TEXT,
                BankReferenceKind             TEXT,
                BankReference                 TEXT,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE,
            
                FOREIGN KEY (OriginatorId)
                    REFERENCES AchOriginators(Id),
            
                FOREIGN KEY (EntryDescription)
                    REFERENCES AchEntryDescriptions(Description),
            
                FOREIGN KEY (SecCode)
                    REFERENCES AchSecCodes(Code),
            
                FOREIGN KEY (EffectiveEntryDateUnixSeconds)
                    REFERENCES DateValues(UnixSeconds),
            
                FOREIGN KEY (BankReferenceKind)
                    REFERENCES AchBankReferenceKinds(Name),
            
                CHECK (
                    (BankReferenceKind IS NULL AND BankReference IS NULL)
                    OR
                    (BankReferenceKind IS NOT NULL AND BankReference IS NOT NULL)
                )
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS TransferCounterparties
            (
                Id           INTEGER PRIMARY KEY,
                Institution  TEXT NOT NULL,
                AccountLabel TEXT NOT NULL,
                Last4        TEXT NOT NULL,
            
                UNIQUE (Institution, AccountLabel, Last4)
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS AccountTransfers
            (
                TransactionId          INTEGER PRIMARY KEY,
                Direction              TEXT NOT NULL
                    CHECK (Direction IN ('TO', 'FROM')),
                IsRealtime             INTEGER NOT NULL
                    CHECK (IsRealtime IN (0, 1)),
                CounterpartyId         INTEGER NOT NULL,
                ChaseTransactionNumber TEXT NOT NULL,
                ChaseReference         TEXT,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE,
            
                FOREIGN KEY (CounterpartyId)
                    REFERENCES TransferCounterparties(Id)
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS ChaseCardPayments
            (
                TransactionId   INTEGER PRIMARY KEY,
                TargetCardLast4 TEXT NOT NULL,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE,
            
                FOREIGN KEY (TargetCardLast4)
                    REFERENCES Accounts(Last4)
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS DebitCardTransactions
            (
                TransactionId     INTEGER PRIMARY KEY,
                MerchantDescriptor TEXT NOT NULL,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS AtmTransactions
            (
                TransactionId INTEGER PRIMARY KEY,
                Action        TEXT NOT NULL
                    CHECK (Action IN ('WITHDRAWAL', 'CASH_DEPOSIT')),
                TerminalId    TEXT,
                Location      TEXT,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS FeeTransactions
            (
                TransactionId INTEGER PRIMARY KEY,
                Description   TEXT NOT NULL,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE
            ) STRICT;
            
            CREATE TABLE IF NOT EXISTS RealTimePayments
            (
                TransactionId          INTEGER PRIMARY KEY,
                AbaRoutingNumber       TEXT NOT NULL,
                Sender                 TEXT NOT NULL,
                Reference              TEXT NOT NULL,
                OriginatorCompanyId    TEXT NOT NULL,
                PaymentCode            TEXT NOT NULL,
                Tin                    TEXT,
                Npi                    TEXT,
                ReceiverName           TEXT,
                Purpose                TEXT,
                InstructionId          TEXT NOT NULL,
                ReceivedSecondOfDay    INTEGER NOT NULL
                    CHECK (ReceivedSecondOfDay BETWEEN 0 AND 86399),
                BankReference          TEXT NOT NULL,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE
            ) STRICT;
            
            -- Safety valve for a Chase description format we have not modeled yet.
            -- Current supplied files should create zero rows here.
            CREATE TABLE IF NOT EXISTS UnparsedDepositDescriptions
            (
                TransactionId INTEGER PRIMARY KEY,
                Description   TEXT NOT NULL,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE
            ) STRICT;
            
            -- This relation is intentionally retained. TransactionId is not the source-row
            -- number once overlapping Chase downloads are deduplicated.
            CREATE TABLE IF NOT EXISTS ImportRows
            (
                ImportFileSha256 BLOB NOT NULL,
                SourceRowNumber  INTEGER NOT NULL
                    CHECK (SourceRowNumber >= 2),
                TransactionId    INTEGER NOT NULL,
            
                PRIMARY KEY (ImportFileSha256, SourceRowNumber),
            
                FOREIGN KEY (ImportFileSha256)
                    REFERENCES ImportFiles(FileSha256)
                    ON DELETE CASCADE,
            
                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
            ) STRICT;
            
            CREATE INDEX IF NOT EXISTS IX_Transactions_Account_PostingDate
                ON Transactions(AccountLast4, PostingDateUnixSeconds);
            
            CREATE INDEX IF NOT EXISTS IX_Transactions_Type
                ON Transactions(TypeCode);
            
            CREATE INDEX IF NOT EXISTS IX_ImportRows_Transaction
                ON ImportRows(TransactionId);
            
            CREATE INDEX IF NOT EXISTS IX_AchTransactions_Originator
                ON AchTransactions(OriginatorId);
            
            CREATE INDEX IF NOT EXISTS IX_AchTransactions_Trace
                ON AchTransactions(TraceNumber);
            
            CREATE INDEX IF NOT EXISTS IX_AccountTransfers_Counterparty
                ON AccountTransfers(CounterpartyId);
            """;

        command.ExecuteNonQuery();
    }
}

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
                    ),
                Name TEXT NOT NULL UNIQUE
            ) STRICT;

            -- The three currently configured Chase accounts.
            INSERT INTO Accounts (Last4, Name)
            VALUES
                ('9350', 'Savings'),
                ('8936', 'Checkings'),
                ('8027', 'Chase Visa')
            ON CONFLICT (Last4) DO UPDATE
            SET Name = excluded.Name;

            -- Date-only values are stored as Unix seconds at 00:00:00 UTC.
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
                TransactionId              INTEGER PRIMARY KEY,
                TransactionDateUnixSeconds INTEGER NOT NULL,
                MerchantName               TEXT NOT NULL,
                CategoryName               TEXT,
                Memo                       TEXT,

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
                Id          INTEGER PRIMARY KEY,
                Description TEXT NOT NULL UNIQUE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS AchSecCodes
            (
                Id   INTEGER PRIMARY KEY,
                Code TEXT NOT NULL UNIQUE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS AchTraceNumbers
            (
                Id          INTEGER PRIMARY KEY,
                TraceNumber TEXT NOT NULL UNIQUE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS AchIndividualIdentifiers
            (
                Id         INTEGER PRIMARY KEY,
                IndividualId TEXT NOT NULL UNIQUE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS AchIndividualNames
            (
                Id   INTEGER PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS AchPaymentRelatedInformation
            (
                Id          INTEGER PRIMARY KEY,
                Information TEXT NOT NULL UNIQUE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS AchBankReferenceKinds
            (
                Id   INTEGER PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS AchBankReferences
            (
                Id        INTEGER PRIMARY KEY,
                Reference TEXT NOT NULL UNIQUE
            ) STRICT;

            CREATE TABLE IF NOT EXISTS AchTransactions
            (
                TransactionId                 INTEGER PRIMARY KEY,
                OriginatorId                  INTEGER NOT NULL,
                CompanyDescriptiveDate        TEXT,
                EntryDescriptionId            INTEGER NOT NULL,
                SecCodeId                     INTEGER NOT NULL,
                TraceNumberId                 INTEGER,
                EffectiveEntryDateUnixSeconds INTEGER,
                IndividualIdentifierId        INTEGER,
                IndividualNameId              INTEGER,
                PaymentRelatedInformationId   INTEGER,
                BankReferenceKindId           INTEGER,
                BankReferenceId               INTEGER,

                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE,

                FOREIGN KEY (OriginatorId)
                    REFERENCES AchOriginators(Id),

                FOREIGN KEY (EntryDescriptionId)
                    REFERENCES AchEntryDescriptions(Id),

                FOREIGN KEY (SecCodeId)
                    REFERENCES AchSecCodes(Id),

                FOREIGN KEY (TraceNumberId)
                    REFERENCES AchTraceNumbers(Id),

                FOREIGN KEY (EffectiveEntryDateUnixSeconds)
                    REFERENCES DateValues(UnixSeconds),

                FOREIGN KEY (IndividualIdentifierId)
                    REFERENCES AchIndividualIdentifiers(Id),

                FOREIGN KEY (IndividualNameId)
                    REFERENCES AchIndividualNames(Id),

                FOREIGN KEY (PaymentRelatedInformationId)
                    REFERENCES AchPaymentRelatedInformation(Id),

                FOREIGN KEY (BankReferenceKindId)
                    REFERENCES AchBankReferenceKinds(Id),

                FOREIGN KEY (BankReferenceId)
                    REFERENCES AchBankReferences(Id),

                CHECK (
                    (BankReferenceKindId IS NULL AND BankReferenceId IS NULL)
                    OR
                    (BankReferenceKindId IS NOT NULL AND BankReferenceId IS NOT NULL)
                )
            ) STRICT;

            CREATE TABLE IF NOT EXISTS TransferDirections
            (
                Id   INTEGER PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE
                    CHECK (Name IN ('TO', 'FROM'))
            ) STRICT;

            INSERT INTO TransferDirections (Id, Name)
            VALUES
                (1, 'TO'),
                (2, 'FROM')
            ON CONFLICT (Id) DO UPDATE
            SET Name = excluded.Name;

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
                DirectionId            INTEGER NOT NULL,
                IsRealtime             INTEGER NOT NULL
                    CHECK (IsRealtime IN (0, 1)),
                CounterpartyId         INTEGER NOT NULL,
                ChaseTransactionNumber TEXT NOT NULL,
                ChaseReference         TEXT,

                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE,

                FOREIGN KEY (DirectionId)
                    REFERENCES TransferDirections(Id),

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
                TransactionId      INTEGER PRIMARY KEY,
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
                TransactionId       INTEGER PRIMARY KEY,
                AbaRoutingNumber    TEXT NOT NULL,
                Sender              TEXT NOT NULL,
                Reference           TEXT NOT NULL,
                OriginatorCompanyId TEXT NOT NULL,
                PaymentCode         TEXT NOT NULL,
                Tin                 TEXT,
                Npi                 TEXT,
                ReceiverName        TEXT,
                Purpose             TEXT,
                InstructionId       TEXT NOT NULL,
                ReceivedSecondOfDay INTEGER NOT NULL
                    CHECK (ReceivedSecondOfDay BETWEEN 0 AND 86399),
                BankReference       TEXT NOT NULL,

                FOREIGN KEY (TransactionId)
                    REFERENCES Transactions(Id)
                    ON DELETE CASCADE
            ) STRICT;

            -- Safety valve for a Chase description format we have not modeled yet.
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

            CREATE INDEX IF NOT EXISTS IX_AchTransactions_EntryDescription
                ON AchTransactions(EntryDescriptionId);

            CREATE INDEX IF NOT EXISTS IX_AchTransactions_SecCode
                ON AchTransactions(SecCodeId);

            CREATE INDEX IF NOT EXISTS IX_AchTransactions_Trace
                ON AchTransactions(TraceNumberId);

            CREATE INDEX IF NOT EXISTS IX_AchTransactions_IndividualIdentifier
                ON AchTransactions(IndividualIdentifierId);

            CREATE INDEX IF NOT EXISTS IX_AchTransactions_IndividualName
                ON AchTransactions(IndividualNameId);

            CREATE INDEX IF NOT EXISTS IX_AchTransactions_PaymentInformation
                ON AchTransactions(PaymentRelatedInformationId);

            CREATE INDEX IF NOT EXISTS IX_AchTransactions_BankReferenceKind
                ON AchTransactions(BankReferenceKindId);

            CREATE INDEX IF NOT EXISTS IX_AchTransactions_BankReference
                ON AchTransactions(BankReferenceId);

            CREATE INDEX IF NOT EXISTS IX_AccountTransfers_Direction
                ON AccountTransfers(DirectionId);

            CREATE INDEX IF NOT EXISTS IX_AccountTransfers_Counterparty
                ON AccountTransfers(CounterpartyId);
            """;

        command.ExecuteNonQuery();
    }
}
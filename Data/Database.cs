using Microsoft.Data.Sqlite;
using SoloPractice.Utilities;

namespace SoloPractice.Data;

internal static class Database
{
    private const int CurrentSchemaVersion = 5;

    private const string CurrentSchemaSql = """
        PRAGMA foreign_keys = ON;

        -- Lookup/entity tables use surrogate INTEGER primary keys. Human-readable
        -- values remain UNIQUE so the importer can safely resolve them to IDs.
        CREATE TABLE IF NOT EXISTS Accounts
        (
            Id    INTEGER PRIMARY KEY,
            Last4 TEXT NOT NULL UNIQUE
                CHECK (
                    length(Last4) = 4
                    AND Last4 GLOB '[0-9][0-9][0-9][0-9]'
                ),
            Name  TEXT NOT NULL UNIQUE
        ) STRICT;

        INSERT INTO Accounts (Last4, Name)
        VALUES
            ('9350', 'Savings'),
            ('8936', 'Checkings'),
            ('8027', 'Chase Visa')
        ON CONFLICT (Last4) DO UPDATE
        SET Name = excluded.Name;

        -- Date-only values are stored as Unix seconds at 00:00:00 UTC. Other tables
        -- store DateValues.Id, never a duplicate Unix timestamp.
        CREATE TABLE IF NOT EXISTS DateValues
        (
            Id          INTEGER PRIMARY KEY,
            UnixSeconds INTEGER NOT NULL UNIQUE
                CHECK (UnixSeconds % 86400 = 0)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS MoneyValues
        (
            Id    INTEGER PRIMARY KEY,
            Cents INTEGER NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE IF NOT EXISTS TransactionTypes
        (
            Id   INTEGER PRIMARY KEY,
            Code TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE IF NOT EXISTS DepositDetails
        (
            Id   INTEGER PRIMARY KEY,
            Code TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE IF NOT EXISTS ImportFormats
        (
            Id   INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE IF NOT EXISTS CreditCardMerchants
        (
            Id   INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE IF NOT EXISTS CreditCardCategories
        (
            Id   INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE IF NOT EXISTS ImportFiles
        (
            Id             INTEGER PRIMARY KEY,
            FileSha256     BLOB NOT NULL UNIQUE
                CHECK (length(FileSha256) = 32),
            AccountId      INTEGER NOT NULL,
            FormatId       INTEGER NOT NULL,
            DownloadDateId INTEGER NOT NULL,
            ImportedAtUtc  TEXT NOT NULL,

            FOREIGN KEY (AccountId)
                REFERENCES Accounts(Id),
            FOREIGN KEY (FormatId)
                REFERENCES ImportFormats(Id),
            FOREIGN KEY (DownloadDateId)
                REFERENCES DateValues(Id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS Transactions
        (
            Id            INTEGER PRIMARY KEY,
            AccountId     INTEGER NOT NULL,
            PostingDateId INTEGER NOT NULL,
            AmountId      INTEGER NOT NULL,
            TypeId        INTEGER NOT NULL,

            FOREIGN KEY (AccountId)
                REFERENCES Accounts(Id),
            FOREIGN KEY (PostingDateId)
                REFERENCES DateValues(Id),
            FOREIGN KEY (AmountId)
                REFERENCES MoneyValues(Id),
            FOREIGN KEY (TypeId)
                REFERENCES TransactionTypes(Id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS DepositTransactions
        (
            TransactionId   INTEGER PRIMARY KEY,
            DetailsId       INTEGER NOT NULL,
            BalanceAmountId INTEGER,
            CheckOrSlipNumber TEXT,

            FOREIGN KEY (TransactionId)
                REFERENCES Transactions(Id)
                ON DELETE CASCADE,
            FOREIGN KEY (DetailsId)
                REFERENCES DepositDetails(Id),
            FOREIGN KEY (BalanceAmountId)
                REFERENCES MoneyValues(Id)
        ) STRICT;

        CREATE TABLE IF NOT EXISTS CreditCardTransactions
        (
            TransactionId   INTEGER PRIMARY KEY,
            TransactionDateId INTEGER NOT NULL,
            MerchantId      INTEGER NOT NULL,
            CategoryId      INTEGER,
            Memo            TEXT,

            FOREIGN KEY (TransactionId)
                REFERENCES Transactions(Id)
                ON DELETE CASCADE,
            FOREIGN KEY (TransactionDateId)
                REFERENCES DateValues(Id),
            FOREIGN KEY (MerchantId)
                REFERENCES CreditCardMerchants(Id),
            FOREIGN KEY (CategoryId)
                REFERENCES CreditCardCategories(Id)
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
            Id           INTEGER PRIMARY KEY,
            IndividualId TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE IF NOT EXISTS AchIndividualNames
        (
            Id   INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        ) STRICT;

        -- ACH payment-related information is highly repetitive. Store only the
        -- variable payload and a small format ID; the full Chase string is rebuilt
        -- losslessly by AchPaymentRelatedInformationExpanded when it is needed.
        CREATE TABLE IF NOT EXISTS AchPaymentInformationFormats
        (
            Id                 INTEGER PRIMARY KEY,
            Name               TEXT NOT NULL UNIQUE,
            Prefix             TEXT NOT NULL,
            Suffix             TEXT NOT NULL,
            ReconstructionKind INTEGER NOT NULL
                CHECK (ReconstructionKind IN (0, 1))
        ) STRICT;

        INSERT INTO AchPaymentInformationFormats
            (Id, Name, Prefix, Suffix, ReconstructionKind)
        VALUES
            (1,  'TRN_1411289245_0000877_26',
                 'TRN*1*', '*1411289245*0000877 26\', 0),
            (2,  'TRN_1411289245_000087726_ALT',
                 'TRN*1*', '*1411289245*000087726 \', 0),
            (3,  'TRN_1411648670',
                 'TRN*1*', '*1411648670\', 0),
            (4,  'TRN_1066033492',
                 'TRN*1*', '*1066033492\', 0),
            (5,  'TRN_1361236610_CP_DERIVED',
                 'TRN*1*', '', 1),
            (6,  'TRN_1341858379',
                 'TRN*1*', '*1341858379\', 0),
            (7,  'TRN_1364004756_4004756',
                 'TRN*1*', '*1364004756*36 4004756~                                      0', 0),
            (8,  'TRN_1391995276_UMR01',
                 'TRN*1*', '*139 1995276*0000UMR01\', 0),
            (9,  'TRN_1860507074_UHCEX',
                 'TRN*1*', '*1860507074*0 000UHCEX\', 0),
            (10, 'TRN_1591031071_HCCLAIMPMT',
                 'TRN*1*', '*1591031071~                                                    HCCLAIMPMT', 0),
            (11, 'TXP_337743360SOLE_012_20261231_T',
                 'TXP*337743360SOLE*012*20261231*T*', '\', 0),
            (12, 'RAW',
                 '', '', 0)
        ON CONFLICT (Id) DO UPDATE
        SET
            Name = excluded.Name,
            Prefix = excluded.Prefix,
            Suffix = excluded.Suffix,
            ReconstructionKind = excluded.ReconstructionKind;

        CREATE TABLE IF NOT EXISTS AchPaymentRelatedInformation
        (
            Id       INTEGER PRIMARY KEY,
            FormatId INTEGER NOT NULL,
            Payload  TEXT NOT NULL,

            UNIQUE (FormatId, Payload),
            FOREIGN KEY (FormatId)
                REFERENCES AchPaymentInformationFormats(Id)
        ) STRICT;

        CREATE VIEW IF NOT EXISTS AchPaymentRelatedInformationExpanded AS
        SELECT
            p.Id,
            CASE f.ReconstructionKind
                WHEN 1 THEN
                    f.Prefix
                    || p.Payload
                    || '*1361236610*CP '
                    || strftime(
                        '%Y%m%d',
                        date(
                            printf(
                                '%04d-01-01',
                                2000 + CAST(substr(p.Payload, 2, 2) AS INTEGER)
                            ),
                            printf(
                                '+%d days',
                                CAST(substr(p.Payload, 4, 3) AS INTEGER) - 1
                            )
                        )
                    )
                    || substr(p.Payload, 7)
                    || '0-1376879510\'
                ELSE
                    f.Prefix || p.Payload || f.Suffix
            END AS Information
        FROM AchPaymentRelatedInformation p
        JOIN AchPaymentInformationFormats f
          ON f.Id = p.FormatId;

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
            TransactionId               INTEGER PRIMARY KEY,
            OriginatorId                INTEGER NOT NULL,
            CompanyDescriptiveDate      TEXT,
            EntryDescriptionId          INTEGER NOT NULL,
            SecCodeId                   INTEGER NOT NULL,
            TraceNumberId               INTEGER,
            EffectiveEntryDateId        INTEGER,
            IndividualIdentifierId      INTEGER,
            IndividualNameId            INTEGER,
            PaymentRelatedInformationId INTEGER,
            BankReferenceKindId         INTEGER,
            BankReferenceId             INTEGER,

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
            FOREIGN KEY (EffectiveEntryDateId)
                REFERENCES DateValues(Id),
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
            TargetAccountId INTEGER NOT NULL,

            FOREIGN KEY (TransactionId)
                REFERENCES Transactions(Id)
                ON DELETE CASCADE,
            FOREIGN KEY (TargetAccountId)
                REFERENCES Accounts(Id)
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

        CREATE TABLE IF NOT EXISTS UnparsedDepositDescriptions
        (
            TransactionId INTEGER PRIMARY KEY,
            Description   TEXT NOT NULL,
            FOREIGN KEY (TransactionId)
                REFERENCES Transactions(Id)
                ON DELETE CASCADE
        ) STRICT;

        CREATE TABLE IF NOT EXISTS ImportRows
        (
            Id              INTEGER PRIMARY KEY,
            ImportFileId    INTEGER NOT NULL,
            SourceRowNumber INTEGER NOT NULL
                CHECK (SourceRowNumber >= 2),
            TransactionId   INTEGER NOT NULL,

            UNIQUE (ImportFileId, SourceRowNumber),
            FOREIGN KEY (ImportFileId)
                REFERENCES ImportFiles(Id)
                ON DELETE CASCADE,
            FOREIGN KEY (TransactionId)
                REFERENCES Transactions(Id)
        ) STRICT;

        -- Child-key indexes. These speed joins/deduplication and the lookups SQLite
        -- performs while enforcing foreign-key relationships.
        CREATE INDEX IF NOT EXISTS IX_ImportFiles_Account
            ON ImportFiles(AccountId);
        CREATE INDEX IF NOT EXISTS IX_ImportFiles_Format
            ON ImportFiles(FormatId);
        CREATE INDEX IF NOT EXISTS IX_ImportFiles_DownloadDate
            ON ImportFiles(DownloadDateId);

        CREATE INDEX IF NOT EXISTS IX_Transactions_Dedup
            ON Transactions(AccountId, PostingDateId, AmountId, TypeId);
        CREATE INDEX IF NOT EXISTS IX_Transactions_PostingDate
            ON Transactions(PostingDateId);
        CREATE INDEX IF NOT EXISTS IX_Transactions_Amount
            ON Transactions(AmountId);
        CREATE INDEX IF NOT EXISTS IX_Transactions_Type
            ON Transactions(TypeId);

        CREATE INDEX IF NOT EXISTS IX_DepositTransactions_Details
            ON DepositTransactions(DetailsId);
        CREATE INDEX IF NOT EXISTS IX_DepositTransactions_Balance
            ON DepositTransactions(BalanceAmountId);

        CREATE INDEX IF NOT EXISTS IX_CreditCardTransactions_TransactionDate
            ON CreditCardTransactions(TransactionDateId);
        CREATE INDEX IF NOT EXISTS IX_CreditCardTransactions_Merchant
            ON CreditCardTransactions(MerchantId);
        CREATE INDEX IF NOT EXISTS IX_CreditCardTransactions_Category
            ON CreditCardTransactions(CategoryId);

        CREATE INDEX IF NOT EXISTS IX_ChaseCardPayments_TargetAccount
            ON ChaseCardPayments(TargetAccountId);
        CREATE INDEX IF NOT EXISTS IX_AchTransactions_EffectiveEntryDate
            ON AchTransactions(EffectiveEntryDateId);

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

        PRAGMA user_version = 5;
        PRAGMA optimize;
        """;

    private const string MigrationCreateLookupTablesSql = """
        DROP TABLE IF EXISTS __sp4_Accounts;
        DROP TABLE IF EXISTS __sp4_DateValues;
        DROP TABLE IF EXISTS __sp4_MoneyValues;
        DROP TABLE IF EXISTS __sp4_TransactionTypes;
        DROP TABLE IF EXISTS __sp4_DepositDetails;
        DROP TABLE IF EXISTS __sp4_ImportFormats;
        DROP TABLE IF EXISTS __sp4_CreditCardMerchants;
        DROP TABLE IF EXISTS __sp4_CreditCardCategories;

        CREATE TABLE __sp4_Accounts
        (
            Id    INTEGER PRIMARY KEY,
            Last4 TEXT NOT NULL UNIQUE
                CHECK (
                    length(Last4) = 4
                    AND Last4 GLOB '[0-9][0-9][0-9][0-9]'
                ),
            Name  TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE __sp4_DateValues
        (
            Id          INTEGER PRIMARY KEY,
            UnixSeconds INTEGER NOT NULL UNIQUE
                CHECK (UnixSeconds % 86400 = 0)
        ) STRICT;

        CREATE TABLE __sp4_MoneyValues
        (
            Id    INTEGER PRIMARY KEY,
            Cents INTEGER NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE __sp4_TransactionTypes
        (
            Id   INTEGER PRIMARY KEY,
            Code TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE __sp4_DepositDetails
        (
            Id   INTEGER PRIMARY KEY,
            Code TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE __sp4_ImportFormats
        (
            Id   INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE __sp4_CreditCardMerchants
        (
            Id   INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        ) STRICT;

        CREATE TABLE __sp4_CreditCardCategories
        (
            Id   INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        ) STRICT;
        """;

    private const string MigrationPopulateLookupTablesSql = """
        INSERT OR IGNORE INTO __sp4_Accounts (Last4, Name)
        SELECT Last4, Name FROM Accounts;

        INSERT OR IGNORE INTO __sp4_DateValues (UnixSeconds)
        SELECT UnixSeconds FROM DateValues;
        INSERT OR IGNORE INTO __sp4_DateValues (UnixSeconds)
        SELECT DownloadDateUnixSeconds FROM ImportFiles;
        INSERT OR IGNORE INTO __sp4_DateValues (UnixSeconds)
        SELECT PostingDateUnixSeconds FROM Transactions;
        INSERT OR IGNORE INTO __sp4_DateValues (UnixSeconds)
        SELECT TransactionDateUnixSeconds FROM CreditCardTransactions;
        INSERT OR IGNORE INTO __sp4_DateValues (UnixSeconds)
        SELECT EffectiveEntryDateUnixSeconds
        FROM AchTransactions
        WHERE EffectiveEntryDateUnixSeconds IS NOT NULL;

        INSERT OR IGNORE INTO __sp4_MoneyValues (Cents)
        SELECT Cents FROM MoneyValues;
        INSERT OR IGNORE INTO __sp4_MoneyValues (Cents)
        SELECT AmountCents FROM Transactions;
        INSERT OR IGNORE INTO __sp4_MoneyValues (Cents)
        SELECT BalanceCents
        FROM DepositTransactions
        WHERE BalanceCents IS NOT NULL;

        INSERT OR IGNORE INTO __sp4_TransactionTypes (Code)
        SELECT Code FROM TransactionTypes;
        INSERT OR IGNORE INTO __sp4_TransactionTypes (Code)
        SELECT DISTINCT TypeCode FROM Transactions;

        INSERT OR IGNORE INTO __sp4_DepositDetails (Code)
        SELECT Code FROM DepositDetails;
        INSERT OR IGNORE INTO __sp4_DepositDetails (Code)
        SELECT DISTINCT DetailsCode FROM DepositTransactions;

        INSERT OR IGNORE INTO __sp4_ImportFormats (Name)
        SELECT Name FROM ImportFormats;
        INSERT OR IGNORE INTO __sp4_ImportFormats (Name)
        SELECT DISTINCT FormatName FROM ImportFiles;

        INSERT OR IGNORE INTO __sp4_CreditCardMerchants (Name)
        SELECT Name FROM CreditCardMerchants;
        INSERT OR IGNORE INTO __sp4_CreditCardMerchants (Name)
        SELECT DISTINCT MerchantName FROM CreditCardTransactions;

        INSERT OR IGNORE INTO __sp4_CreditCardCategories (Name)
        SELECT Name FROM CreditCardCategories;
        INSERT OR IGNORE INTO __sp4_CreditCardCategories (Name)
        SELECT DISTINCT CategoryName
        FROM CreditCardTransactions
        WHERE CategoryName IS NOT NULL;
        """;

    private const string MigrationCreateReplacementTablesSql = """
        DROP TABLE IF EXISTS __sp4_ImportFiles;
        DROP TABLE IF EXISTS __sp4_Transactions;
        DROP TABLE IF EXISTS __sp4_DepositTransactions;
        DROP TABLE IF EXISTS __sp4_CreditCardTransactions;
        DROP TABLE IF EXISTS __sp4_AchTransactions;
        DROP TABLE IF EXISTS __sp4_ChaseCardPayments;
        DROP TABLE IF EXISTS __sp4_ImportRows;

        CREATE TABLE __sp4_ImportFiles
        (
            Id             INTEGER PRIMARY KEY,
            FileSha256     BLOB NOT NULL UNIQUE
                CHECK (length(FileSha256) = 32),
            AccountId      INTEGER NOT NULL,
            FormatId       INTEGER NOT NULL,
            DownloadDateId INTEGER NOT NULL,
            ImportedAtUtc  TEXT NOT NULL,

            FOREIGN KEY (AccountId) REFERENCES Accounts(Id),
            FOREIGN KEY (FormatId) REFERENCES ImportFormats(Id),
            FOREIGN KEY (DownloadDateId) REFERENCES DateValues(Id)
        ) STRICT;

        CREATE TABLE __sp4_Transactions
        (
            Id            INTEGER PRIMARY KEY,
            AccountId     INTEGER NOT NULL,
            PostingDateId INTEGER NOT NULL,
            AmountId      INTEGER NOT NULL,
            TypeId        INTEGER NOT NULL,

            FOREIGN KEY (AccountId) REFERENCES Accounts(Id),
            FOREIGN KEY (PostingDateId) REFERENCES DateValues(Id),
            FOREIGN KEY (AmountId) REFERENCES MoneyValues(Id),
            FOREIGN KEY (TypeId) REFERENCES TransactionTypes(Id)
        ) STRICT;

        CREATE TABLE __sp4_DepositTransactions
        (
            TransactionId     INTEGER PRIMARY KEY,
            DetailsId         INTEGER NOT NULL,
            BalanceAmountId   INTEGER,
            CheckOrSlipNumber TEXT,

            FOREIGN KEY (TransactionId)
                REFERENCES Transactions(Id) ON DELETE CASCADE,
            FOREIGN KEY (DetailsId) REFERENCES DepositDetails(Id),
            FOREIGN KEY (BalanceAmountId) REFERENCES MoneyValues(Id)
        ) STRICT;

        CREATE TABLE __sp4_CreditCardTransactions
        (
            TransactionId    INTEGER PRIMARY KEY,
            TransactionDateId INTEGER NOT NULL,
            MerchantId       INTEGER NOT NULL,
            CategoryId       INTEGER,
            Memo             TEXT,

            FOREIGN KEY (TransactionId)
                REFERENCES Transactions(Id) ON DELETE CASCADE,
            FOREIGN KEY (TransactionDateId) REFERENCES DateValues(Id),
            FOREIGN KEY (MerchantId) REFERENCES CreditCardMerchants(Id),
            FOREIGN KEY (CategoryId) REFERENCES CreditCardCategories(Id)
        ) STRICT;

        CREATE TABLE __sp4_AchTransactions
        (
            TransactionId               INTEGER PRIMARY KEY,
            OriginatorId                INTEGER NOT NULL,
            CompanyDescriptiveDate      TEXT,
            EntryDescriptionId          INTEGER NOT NULL,
            SecCodeId                   INTEGER NOT NULL,
            TraceNumberId               INTEGER,
            EffectiveEntryDateId        INTEGER,
            IndividualIdentifierId      INTEGER,
            IndividualNameId            INTEGER,
            PaymentRelatedInformationId INTEGER,
            BankReferenceKindId         INTEGER,
            BankReferenceId             INTEGER,

            FOREIGN KEY (TransactionId)
                REFERENCES Transactions(Id) ON DELETE CASCADE,
            FOREIGN KEY (OriginatorId) REFERENCES AchOriginators(Id),
            FOREIGN KEY (EntryDescriptionId) REFERENCES AchEntryDescriptions(Id),
            FOREIGN KEY (SecCodeId) REFERENCES AchSecCodes(Id),
            FOREIGN KEY (TraceNumberId) REFERENCES AchTraceNumbers(Id),
            FOREIGN KEY (EffectiveEntryDateId) REFERENCES DateValues(Id),
            FOREIGN KEY (IndividualIdentifierId) REFERENCES AchIndividualIdentifiers(Id),
            FOREIGN KEY (IndividualNameId) REFERENCES AchIndividualNames(Id),
            FOREIGN KEY (PaymentRelatedInformationId) REFERENCES AchPaymentRelatedInformation(Id),
            FOREIGN KEY (BankReferenceKindId) REFERENCES AchBankReferenceKinds(Id),
            FOREIGN KEY (BankReferenceId) REFERENCES AchBankReferences(Id),

            CHECK (
                (BankReferenceKindId IS NULL AND BankReferenceId IS NULL)
                OR
                (BankReferenceKindId IS NOT NULL AND BankReferenceId IS NOT NULL)
            )
        ) STRICT;

        CREATE TABLE __sp4_ChaseCardPayments
        (
            TransactionId   INTEGER PRIMARY KEY,
            TargetAccountId INTEGER NOT NULL,

            FOREIGN KEY (TransactionId)
                REFERENCES Transactions(Id) ON DELETE CASCADE,
            FOREIGN KEY (TargetAccountId) REFERENCES Accounts(Id)
        ) STRICT;

        CREATE TABLE __sp4_ImportRows
        (
            Id              INTEGER PRIMARY KEY,
            ImportFileId    INTEGER NOT NULL,
            SourceRowNumber INTEGER NOT NULL
                CHECK (SourceRowNumber >= 2),
            TransactionId   INTEGER NOT NULL,

            UNIQUE (ImportFileId, SourceRowNumber),
            FOREIGN KEY (ImportFileId)
                REFERENCES ImportFiles(Id) ON DELETE CASCADE,
            FOREIGN KEY (TransactionId) REFERENCES Transactions(Id)
        ) STRICT;
        """;

    private const string MigrationCopyCoreDataSql = """
        INSERT INTO __sp4_Transactions
        (
            Id,
            AccountId,
            PostingDateId,
            AmountId,
            TypeId
        )
        SELECT
            t.Id,
            a.Id,
            d.Id,
            m.Id,
            ty.Id
        FROM Transactions t
        JOIN __sp4_Accounts a
          ON a.Last4 = t.AccountLast4
        JOIN __sp4_DateValues d
          ON d.UnixSeconds = t.PostingDateUnixSeconds
        JOIN __sp4_MoneyValues m
          ON m.Cents = t.AmountCents
        JOIN __sp4_TransactionTypes ty
          ON ty.Code = t.TypeCode;

        INSERT INTO __sp4_DepositTransactions
        (
            TransactionId,
            DetailsId,
            BalanceAmountId,
            CheckOrSlipNumber
        )
        SELECT
            d.TransactionId,
            dd.Id,
            mv.Id,
            d.CheckOrSlipNumber
        FROM DepositTransactions d
        JOIN __sp4_DepositDetails dd
          ON dd.Code = d.DetailsCode
        LEFT JOIN __sp4_MoneyValues mv
          ON mv.Cents = d.BalanceCents;

        INSERT INTO __sp4_CreditCardTransactions
        (
            TransactionId,
            TransactionDateId,
            MerchantId,
            CategoryId,
            Memo
        )
        SELECT
            c.TransactionId,
            dv.Id,
            m.Id,
            cat.Id,
            c.Memo
        FROM CreditCardTransactions c
        JOIN __sp4_DateValues dv
          ON dv.UnixSeconds = c.TransactionDateUnixSeconds
        JOIN __sp4_CreditCardMerchants m
          ON m.Name = c.MerchantName
        LEFT JOIN __sp4_CreditCardCategories cat
          ON cat.Name = c.CategoryName;

        INSERT INTO __sp4_AchTransactions
        (
            TransactionId,
            OriginatorId,
            CompanyDescriptiveDate,
            EntryDescriptionId,
            SecCodeId,
            TraceNumberId,
            EffectiveEntryDateId,
            IndividualIdentifierId,
            IndividualNameId,
            PaymentRelatedInformationId,
            BankReferenceKindId,
            BankReferenceId
        )
        SELECT
            a.TransactionId,
            a.OriginatorId,
            a.CompanyDescriptiveDate,
            a.EntryDescriptionId,
            a.SecCodeId,
            a.TraceNumberId,
            dv.Id,
            a.IndividualIdentifierId,
            a.IndividualNameId,
            a.PaymentRelatedInformationId,
            a.BankReferenceKindId,
            a.BankReferenceId
        FROM AchTransactions a
        LEFT JOIN __sp4_DateValues dv
          ON dv.UnixSeconds = a.EffectiveEntryDateUnixSeconds;

        INSERT INTO __sp4_ChaseCardPayments
        (
            TransactionId,
            TargetAccountId
        )
        SELECT
            p.TransactionId,
            a.Id
        FROM ChaseCardPayments p
        JOIN __sp4_Accounts a
          ON a.Last4 = p.TargetCardLast4;
        """;

    private const string MigrationSwapTablesSql = """
        DROP TABLE ImportRows;
        DROP TABLE ChaseCardPayments;
        DROP TABLE AchTransactions;
        DROP TABLE CreditCardTransactions;
        DROP TABLE DepositTransactions;
        DROP TABLE Transactions;
        DROP TABLE ImportFiles;

        DROP TABLE CreditCardCategories;
        DROP TABLE CreditCardMerchants;
        DROP TABLE ImportFormats;
        DROP TABLE DepositDetails;
        DROP TABLE TransactionTypes;
        DROP TABLE MoneyValues;
        DROP TABLE DateValues;
        DROP TABLE Accounts;

        ALTER TABLE __sp4_Accounts RENAME TO Accounts;
        ALTER TABLE __sp4_DateValues RENAME TO DateValues;
        ALTER TABLE __sp4_MoneyValues RENAME TO MoneyValues;
        ALTER TABLE __sp4_TransactionTypes RENAME TO TransactionTypes;
        ALTER TABLE __sp4_DepositDetails RENAME TO DepositDetails;
        ALTER TABLE __sp4_ImportFormats RENAME TO ImportFormats;
        ALTER TABLE __sp4_CreditCardMerchants RENAME TO CreditCardMerchants;
        ALTER TABLE __sp4_CreditCardCategories RENAME TO CreditCardCategories;

        ALTER TABLE __sp4_ImportFiles RENAME TO ImportFiles;
        ALTER TABLE __sp4_Transactions RENAME TO Transactions;
        ALTER TABLE __sp4_DepositTransactions RENAME TO DepositTransactions;
        ALTER TABLE __sp4_CreditCardTransactions RENAME TO CreditCardTransactions;
        ALTER TABLE __sp4_AchTransactions RENAME TO AchTransactions;
        ALTER TABLE __sp4_ChaseCardPayments RENAME TO ChaseCardPayments;
        ALTER TABLE __sp4_ImportRows RENAME TO ImportRows;
        """;


    private const string MigrationCompressAchPaymentInformationToVersion5Sql = """
        DROP VIEW IF EXISTS AchPaymentRelatedInformationExpanded;
        DROP TABLE IF EXISTS __sp5_AchTransactions;
        DROP TABLE IF EXISTS __sp5_AchPaymentRelatedInformation;

        CREATE TABLE IF NOT EXISTS AchPaymentInformationFormats
        (
            Id                 INTEGER PRIMARY KEY,
            Name               TEXT NOT NULL UNIQUE,
            Prefix             TEXT NOT NULL,
            Suffix             TEXT NOT NULL,
            ReconstructionKind INTEGER NOT NULL
                CHECK (ReconstructionKind IN (0, 1))
        ) STRICT;

        INSERT INTO AchPaymentInformationFormats
            (Id, Name, Prefix, Suffix, ReconstructionKind)
        VALUES
            (1,  'TRN_1411289245_0000877_26',
                 'TRN*1*', '*1411289245*0000877 26\', 0),
            (2,  'TRN_1411289245_000087726_ALT',
                 'TRN*1*', '*1411289245*000087726 \', 0),
            (3,  'TRN_1411648670',
                 'TRN*1*', '*1411648670\', 0),
            (4,  'TRN_1066033492',
                 'TRN*1*', '*1066033492\', 0),
            (5,  'TRN_1361236610_CP_DERIVED',
                 'TRN*1*', '', 1),
            (6,  'TRN_1341858379',
                 'TRN*1*', '*1341858379\', 0),
            (7,  'TRN_1364004756_4004756',
                 'TRN*1*', '*1364004756*36 4004756~                                      0', 0),
            (8,  'TRN_1391995276_UMR01',
                 'TRN*1*', '*139 1995276*0000UMR01\', 0),
            (9,  'TRN_1860507074_UHCEX',
                 'TRN*1*', '*1860507074*0 000UHCEX\', 0),
            (10, 'TRN_1591031071_HCCLAIMPMT',
                 'TRN*1*', '*1591031071~                                                    HCCLAIMPMT', 0),
            (11, 'TXP_337743360SOLE_012_20261231_T',
                 'TXP*337743360SOLE*012*20261231*T*', '\', 0),
            (12, 'RAW',
                 '', '', 0)
        ON CONFLICT (Id) DO UPDATE
        SET
            Name = excluded.Name,
            Prefix = excluded.Prefix,
            Suffix = excluded.Suffix,
            ReconstructionKind = excluded.ReconstructionKind;

        CREATE TABLE __sp5_AchPaymentRelatedInformation
        (
            Id       INTEGER PRIMARY KEY,
            FormatId INTEGER NOT NULL,
            Payload  TEXT NOT NULL,

            UNIQUE (FormatId, Payload),
            FOREIGN KEY (FormatId)
                REFERENCES AchPaymentInformationFormats(Id)
        ) STRICT;

        WITH Base AS
        (
            SELECT
                Id,
                Information,
                CASE
                    WHEN substr(Information, 1, 6) = 'TRN*1*'
                     AND instr(Information, '*1361236610*CP ') > 6
                     AND substr(Information, -12) = '-1376879510\'
                    THEN substr(
                        Information,
                        7,
                        instr(Information, '*1361236610*CP ') - 7
                    )
                END AS CpPayload
            FROM AchPaymentRelatedInformation
        ),
        Classified AS
        (
            SELECT
                Id,
                Information,
                CpPayload,
                CASE
                    WHEN CpPayload IS NOT NULL
                     AND (
                            'TRN*1*'
                            || CpPayload
                            || '*1361236610*CP '
                            || strftime(
                                '%Y%m%d',
                                date(
                                    printf(
                                        '%04d-01-01',
                                        2000 + CAST(substr(CpPayload, 2, 2) AS INTEGER)
                                    ),
                                    printf(
                                        '+%d days',
                                        CAST(substr(CpPayload, 4, 3) AS INTEGER) - 1
                                    )
                                )
                            )
                            || substr(CpPayload, 7)
                            || '0-1376879510\'
                        ) = Information
                    THEN 5

                    WHEN substr(Information, 1, 6) = 'TRN*1*'
                     AND substr(
                            Information,
                            -length('*1411289245*0000877 26\')
                         ) = '*1411289245*0000877 26\'
                    THEN 1

                    WHEN substr(Information, 1, 6) = 'TRN*1*'
                     AND substr(
                            Information,
                            -length('*1411289245*000087726 \')
                         ) = '*1411289245*000087726 \'
                    THEN 2

                    WHEN substr(Information, 1, 6) = 'TRN*1*'
                     AND substr(
                            Information,
                            -length('*1411648670\')
                         ) = '*1411648670\'
                    THEN 3

                    WHEN substr(Information, 1, 6) = 'TRN*1*'
                     AND substr(
                            Information,
                            -length('*1066033492\')
                         ) = '*1066033492\'
                    THEN 4

                    WHEN substr(Information, 1, 6) = 'TRN*1*'
                     AND substr(
                            Information,
                            -length('*1341858379\')
                         ) = '*1341858379\'
                    THEN 6

                    WHEN substr(Information, 1, 6) = 'TRN*1*'
                     AND substr(
                            Information,
                            -length('*1364004756*36 4004756~                                      0')
                         ) = '*1364004756*36 4004756~                                      0'
                    THEN 7

                    WHEN substr(Information, 1, 6) = 'TRN*1*'
                     AND substr(
                            Information,
                            -length('*139 1995276*0000UMR01\')
                         ) = '*139 1995276*0000UMR01\'
                    THEN 8

                    WHEN substr(Information, 1, 6) = 'TRN*1*'
                     AND substr(
                            Information,
                            -length('*1860507074*0 000UHCEX\')
                         ) = '*1860507074*0 000UHCEX\'
                    THEN 9

                    WHEN substr(Information, 1, 6) = 'TRN*1*'
                     AND substr(
                            Information,
                            -length('*1591031071~                                                    HCCLAIMPMT')
                         ) = '*1591031071~                                                    HCCLAIMPMT'
                    THEN 10

                    WHEN substr(
                            Information,
                            1,
                            length('TXP*337743360SOLE*012*20261231*T*')
                         ) = 'TXP*337743360SOLE*012*20261231*T*'
                     AND substr(Information, -1) = '\'
                    THEN 11

                    ELSE 12
                END AS FormatId
            FROM Base
        )
        INSERT INTO __sp5_AchPaymentRelatedInformation
            (Id, FormatId, Payload)
        SELECT
            Id,
            FormatId,
            CASE FormatId
                WHEN 1 THEN substr(
                    Information,
                    7,
                    length(Information)
                    - 6
                    - length('*1411289245*0000877 26\')
                )
                WHEN 2 THEN substr(
                    Information,
                    7,
                    length(Information)
                    - 6
                    - length('*1411289245*000087726 \')
                )
                WHEN 3 THEN substr(
                    Information,
                    7,
                    length(Information)
                    - 6
                    - length('*1411648670\')
                )
                WHEN 4 THEN substr(
                    Information,
                    7,
                    length(Information)
                    - 6
                    - length('*1066033492\')
                )
                WHEN 5 THEN CpPayload
                WHEN 6 THEN substr(
                    Information,
                    7,
                    length(Information)
                    - 6
                    - length('*1341858379\')
                )
                WHEN 7 THEN substr(
                    Information,
                    7,
                    length(Information)
                    - 6
                    - length('*1364004756*36 4004756~                                      0')
                )
                WHEN 8 THEN substr(
                    Information,
                    7,
                    length(Information)
                    - 6
                    - length('*139 1995276*0000UMR01\')
                )
                WHEN 9 THEN substr(
                    Information,
                    7,
                    length(Information)
                    - 6
                    - length('*1860507074*0 000UHCEX\')
                )
                WHEN 10 THEN substr(
                    Information,
                    7,
                    length(Information)
                    - 6
                    - length('*1591031071~                                                    HCCLAIMPMT')
                )
                WHEN 11 THEN substr(
                    Information,
                    length('TXP*337743360SOLE*012*20261231*T*') + 1,
                    length(Information)
                    - length('TXP*337743360SOLE*012*20261231*T*')
                    - 1
                )
                ELSE Information
            END
        FROM Classified
        ORDER BY Id;

        CREATE TABLE __sp5_AchTransactions
        (
            TransactionId               INTEGER PRIMARY KEY,
            OriginatorId                INTEGER NOT NULL,
            CompanyDescriptiveDate      TEXT,
            EntryDescriptionId          INTEGER NOT NULL,
            SecCodeId                   INTEGER NOT NULL,
            TraceNumberId               INTEGER,
            EffectiveEntryDateId        INTEGER,
            IndividualIdentifierId      INTEGER,
            IndividualNameId            INTEGER,
            PaymentRelatedInformationId INTEGER,
            BankReferenceKindId         INTEGER,
            BankReferenceId             INTEGER,

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
            FOREIGN KEY (EffectiveEntryDateId)
                REFERENCES DateValues(Id),
            FOREIGN KEY (IndividualIdentifierId)
                REFERENCES AchIndividualIdentifiers(Id),
            FOREIGN KEY (IndividualNameId)
                REFERENCES AchIndividualNames(Id),
            FOREIGN KEY (PaymentRelatedInformationId)
                REFERENCES __sp5_AchPaymentRelatedInformation(Id),
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

        INSERT INTO __sp5_AchTransactions
        (
            TransactionId,
            OriginatorId,
            CompanyDescriptiveDate,
            EntryDescriptionId,
            SecCodeId,
            TraceNumberId,
            EffectiveEntryDateId,
            IndividualIdentifierId,
            IndividualNameId,
            PaymentRelatedInformationId,
            BankReferenceKindId,
            BankReferenceId
        )
        SELECT
            TransactionId,
            OriginatorId,
            CompanyDescriptiveDate,
            EntryDescriptionId,
            SecCodeId,
            TraceNumberId,
            EffectiveEntryDateId,
            IndividualIdentifierId,
            IndividualNameId,
            PaymentRelatedInformationId,
            BankReferenceKindId,
            BankReferenceId
        FROM AchTransactions;

        DROP TABLE AchTransactions;
        DROP TABLE AchPaymentRelatedInformation;

        ALTER TABLE __sp5_AchPaymentRelatedInformation
            RENAME TO AchPaymentRelatedInformation;
        ALTER TABLE __sp5_AchTransactions
            RENAME TO AchTransactions;
        """;

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
        AppPaths.EnsureDirectoriesExist();

        using var connection = OpenConnection();

        bool hasTransactions = TableExists(connection, "Transactions");

        // Do not trust PRAGMA user_version alone. A previous schema script may have
        // advanced user_version while CREATE TABLE IF NOT EXISTS left the old tables
        // untouched. Detect the actual column layout and migrate what is on disk.
        if (hasTransactions && IsNaturalKeySchema(connection))
        {
            MigrateNaturalKeySchemaToVersion4(connection);
        }
        else if (hasTransactions && IsIntegerForeignKeySchema(connection))
        {
            if (!ColumnExists(connection, null, "ImportRows", "Id"))
                MigrateVersion3ImportRowsToVersion4(connection);
        }
        else if (hasTransactions)
        {
            throw new InvalidDataException(
                "SoloPractice found an unsupported partially-migrated database schema. " +
                "Restore the backup made before the schema migration, then run this build again.");
        }

        // Version 5 compresses the highly repetitive ACH payment-related
        // information strings into a format ID plus only the variable payload.
        // Detect the actual old column instead of trusting PRAGMA user_version.
        if (TableExists(connection, "AchPaymentRelatedInformation") &&
            ColumnExists(
                connection,
                null,
                "AchPaymentRelatedInformation",
                "Information"))
        {
            MigrateVersion4PaymentInformationToVersion5(connection);
        }

        ExecuteNonQuery(connection, CurrentSchemaSql);
        ValidateSchemaShape(connection);
        ValidateForeignKeys(connection);

        // PRAGMA optimize is intentionally cheap to run repeatedly. It updates
        // planner statistics when SQLite decides doing so would be useful.
        ExecuteNonQuery(connection, "PRAGMA optimize;");
    }

    private static bool IsNaturalKeySchema(SqliteConnection connection) =>
        ColumnExists(connection, null, "Transactions", "AccountLast4") &&
        ColumnExists(connection, null, "CreditCardTransactions", "MerchantName") &&
        ColumnExists(connection, null, "ImportRows", "ImportFileSha256");

    private static bool IsIntegerForeignKeySchema(SqliteConnection connection) =>
        ColumnExists(connection, null, "Accounts", "Id") &&
        ColumnExists(connection, null, "Transactions", "AccountId") &&
        ColumnExists(connection, null, "CreditCardTransactions", "MerchantId") &&
        ColumnExists(connection, null, "ImportFiles", "Id") &&
        ColumnExists(connection, null, "ImportRows", "ImportFileId");

    private static void MigrateNaturalKeySchemaToVersion4(
        SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = OFF;");

        try
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            ExecuteNonQuery(
                connection,
                MigrationCreateLookupTablesSql,
                transaction);

            ExecuteNonQuery(
                connection,
                MigrationPopulateLookupTablesSql,
                transaction);

            ExecuteNonQuery(
                connection,
                MigrationCreateReplacementTablesSql,
                transaction);

            string importedAtExpression;

            if (ColumnExists(
                    connection,
                    transaction,
                    "ImportFiles",
                    "ImportedAtUtc"))
            {
                importedAtExpression = "i.ImportedAtUtc";
            }
            else if (ColumnExists(
                         connection,
                         transaction,
                         "ImportFiles",
                         "ImportedAtUnixSeconds"))
            {
                importedAtExpression =
                    "strftime('%Y-%m-%dT%H:%M:%SZ', " +
                    "i.ImportedAtUnixSeconds, 'unixepoch')";
            }
            else
            {
                throw new InvalidDataException(
                    "ImportFiles has neither ImportedAtUtc nor " +
                    "ImportedAtUnixSeconds; the database is not a supported " +
                    "SoloPractice schema.");
            }

            ExecuteNonQuery(
                connection,
                $"""
                INSERT INTO __sp4_ImportFiles
                (
                    FileSha256,
                    AccountId,
                    FormatId,
                    DownloadDateId,
                    ImportedAtUtc
                )
                SELECT
                    i.FileSha256,
                    a.Id,
                    f.Id,
                    d.Id,
                    {importedAtExpression}
                FROM ImportFiles i
                JOIN __sp4_Accounts a
                  ON a.Last4 = i.AccountLast4
                JOIN __sp4_ImportFormats f
                  ON f.Name = i.FormatName
                JOIN __sp4_DateValues d
                  ON d.UnixSeconds = i.DownloadDateUnixSeconds;
                """,
                transaction);

            ExecuteNonQuery(
                connection,
                MigrationCopyCoreDataSql,
                transaction);

            ExecuteNonQuery(
                connection,
                """
                INSERT INTO __sp4_ImportRows
                (
                    ImportFileId,
                    SourceRowNumber,
                    TransactionId
                )
                SELECT
                    nf.Id,
                    r.SourceRowNumber,
                    r.TransactionId
                FROM ImportRows r
                JOIN ImportFiles ofile
                  ON ofile.FileSha256 = r.ImportFileSha256
                JOIN __sp4_ImportFiles nf
                  ON nf.FileSha256 = ofile.FileSha256;
                """,
                transaction);

            ExecuteNonQuery(
                connection,
                MigrationSwapTablesSql,
                transaction);

            SetUserVersion(
                connection,
                transaction,
                4);

            ValidateForeignKeys(connection, transaction);
            transaction.Commit();
        }
        finally
        {
            ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        }
    }

    private static void MigrateVersion3ImportRowsToVersion4(
        SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = OFF;");

        try
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            ExecuteNonQuery(
                connection,
                """
                DROP TABLE IF EXISTS __sp4_ImportRows;

                CREATE TABLE __sp4_ImportRows
                (
                    Id              INTEGER PRIMARY KEY,
                    ImportFileId    INTEGER NOT NULL,
                    SourceRowNumber INTEGER NOT NULL
                        CHECK (SourceRowNumber >= 2),
                    TransactionId   INTEGER NOT NULL,

                    UNIQUE (ImportFileId, SourceRowNumber),
                    FOREIGN KEY (ImportFileId)
                        REFERENCES ImportFiles(Id)
                        ON DELETE CASCADE,
                    FOREIGN KEY (TransactionId)
                        REFERENCES Transactions(Id)
                ) STRICT;

                INSERT INTO __sp4_ImportRows
                    (ImportFileId, SourceRowNumber, TransactionId)
                SELECT ImportFileId, SourceRowNumber, TransactionId
                FROM ImportRows
                ORDER BY ImportFileId, SourceRowNumber;

                DROP TABLE ImportRows;
                ALTER TABLE __sp4_ImportRows RENAME TO ImportRows;
                """,
                transaction);

            SetUserVersion(
                connection,
                transaction,
                4);

            ValidateForeignKeys(connection, transaction);
            transaction.Commit();
        }
        finally
        {
            ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        }
    }

    private static void MigrateVersion4PaymentInformationToVersion5(
        SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = OFF;");

        try
        {
            using SqliteTransaction transaction = connection.BeginTransaction();

            ExecuteNonQuery(
                connection,
                MigrationCompressAchPaymentInformationToVersion5Sql,
                transaction);

            SetUserVersion(
                connection,
                transaction,
                CurrentSchemaVersion);

            ValidateForeignKeys(connection, transaction);
            transaction.Commit();
        }
        finally
        {
            ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        }
    }

    private static void ValidateSchemaShape(SqliteConnection connection)
    {
        string[] requiredIntegerPrimaryKeyTables =
        [
            "Accounts",
            "DateValues",
            "MoneyValues",
            "TransactionTypes",
            "DepositDetails",
            "ImportFormats",
            "ImportFiles",
            "Transactions",
            "DepositTransactions",
            "CreditCardMerchants",
            "CreditCardCategories",
            "CreditCardTransactions",
            "AchOriginators",
            "AchEntryDescriptions",
            "AchSecCodes",
            "AchTraceNumbers",
            "AchIndividualIdentifiers",
            "AchIndividualNames",
            "AchPaymentInformationFormats",
            "AchPaymentRelatedInformation",
            "AchBankReferenceKinds",
            "AchBankReferences",
            "AchTransactions",
            "TransferDirections",
            "TransferCounterparties",
            "AccountTransfers",
            "ChaseCardPayments",
            "DebitCardTransactions",
            "AtmTransactions",
            "FeeTransactions",
            "RealTimePayments",
            "UnparsedDepositDescriptions",
            "ImportRows"
        ];

        foreach (string table in requiredIntegerPrimaryKeyTables)
        {
            List<(string Name, string Type, int PrimaryKeyOrder)> columns =
                ReadTableColumns(connection, table);

            List<(string Name, string Type, int PrimaryKeyOrder)> primaryKey =
                columns.Where(column => column.PrimaryKeyOrder > 0).ToList();

            if (primaryKey.Count != 1 ||
                !string.Equals(
                    primaryKey[0].Type,
                    "INTEGER",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Table {table} must have exactly one INTEGER PRIMARY KEY. " +
                    "The on-disk database is not the expected SoloPractice schema.");
            }
        }

        // Every actual FK column must itself be INTEGER and must reference the
        // single INTEGER PRIMARY KEY of its parent table.
        foreach (string table in requiredIntegerPrimaryKeyTables)
        {
            Dictionary<string, string> childTypes = ReadTableColumns(connection, table)
                .ToDictionary(
                    column => column.Name,
                    column => column.Type,
                    StringComparer.Ordinal);

            string safeTable = table.Replace("\"", "\"\"");
            var foreignKeys = new List<(string ParentTable, string ChildColumn, string ParentColumn)>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA foreign_key_list(\"{safeTable}\");";

                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    foreignKeys.Add((
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4)));
                }
            }

            foreach (var foreignKey in foreignKeys)
            {
                if (!childTypes.TryGetValue(
                        foreignKey.ChildColumn,
                        out string? childType) ||
                    !string.Equals(
                        childType,
                        "INTEGER",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Foreign key {table}.{foreignKey.ChildColumn} must be INTEGER.");
                }

                List<(string Name, string Type, int PrimaryKeyOrder)> parentColumns =
                    ReadTableColumns(connection, foreignKey.ParentTable);

                var parentKey = parentColumns.SingleOrDefault(
                    column => string.Equals(
                        column.Name,
                        foreignKey.ParentColumn,
                        StringComparison.Ordinal));

                if (parentKey.PrimaryKeyOrder != 1 ||
                    !string.Equals(
                        parentKey.Type,
                        "INTEGER",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Foreign key {table}.{foreignKey.ChildColumn} must reference an " +
                        $"INTEGER PRIMARY KEY, but it references " +
                        $"{foreignKey.ParentTable}.{foreignKey.ParentColumn}.");
                }
            }
        }

        foreach (string table in requiredIntegerPrimaryKeyTables)
        {
            if (string.Equals(table, "DateValues", StringComparison.Ordinal))
                continue;

            foreach (var column in ReadTableColumns(connection, table))
            {
                if (column.Name.Contains(
                        "UnixSeconds",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Raw Unix-seconds column {table}.{column.Name} is not allowed. " +
                        "Store an INTEGER foreign key to DateValues(Id) instead.");
                }
            }
        }

        string[] forbiddenLegacyColumns =
        [
            "Transactions.AccountLast4",
            "Transactions.PostingDateUnixSeconds",
            "Transactions.AmountCents",
            "Transactions.TypeCode",
            "CreditCardTransactions.TransactionDateUnixSeconds",
            "CreditCardTransactions.MerchantName",
            "CreditCardTransactions.CategoryName",
            "DepositTransactions.DetailsCode",
            "DepositTransactions.BalanceCents",
            "ChaseCardPayments.TargetCardLast4",
            "AchTransactions.EffectiveEntryDateUnixSeconds",
            "ImportFiles.AccountLast4",
            "ImportFiles.FormatName",
            "ImportFiles.DownloadDateUnixSeconds",
            "ImportRows.ImportFileSha256"
        ];

        foreach (string forbidden in forbiddenLegacyColumns)
        {
            string[] parts = forbidden.Split('.', 2);
            if (ColumnExists(connection, null, parts[0], parts[1]))
            {
                throw new InvalidDataException(
                    $"Legacy natural-key column {forbidden} still exists after migration.");
            }
        }
    }

    private static List<(string Name, string Type, int PrimaryKeyOrder)> ReadTableColumns(
        SqliteConnection connection,
        string tableName)
    {
        string safeTableName = tableName.Replace("\"", "\"\"");

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{safeTableName}\");";

        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<(string Name, string Type, int PrimaryKeyOrder)>();

        while (reader.Read())
        {
            result.Add((
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(5)));
        }

        return result;
    }

    private static bool TableExists(
        SqliteConnection connection,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS
            (
                SELECT 1
                FROM sqlite_schema
                WHERE type = 'table'
                  AND name = $name
            );
            """;
        command.Parameters.AddWithValue("$name", tableName);

        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        string columnName)
    {
        string safeTableName = tableName.Replace("\"", "\"\"");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info(\"{safeTableName}\");";

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (string.Equals(
                    reader.GetString(1),
                    columnName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetUserVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static void ValidateForeignKeys(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        string sql,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
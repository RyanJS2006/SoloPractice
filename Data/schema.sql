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
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
    AmountCents   INTEGER NOT NULL,
    TypeId        INTEGER NOT NULL,

    FOREIGN KEY (AccountId)
        REFERENCES Accounts(Id),
    FOREIGN KEY (PostingDateId)
        REFERENCES DateValues(Id),
    FOREIGN KEY (TypeId)
        REFERENCES TransactionTypes(Id)
) STRICT;

CREATE TABLE IF NOT EXISTS DepositTransactions
(
    TransactionId   INTEGER PRIMARY KEY,
    DetailsId       INTEGER NOT NULL,
    BalanceCents    INTEGER,
    CheckOrSlipNumber TEXT,

    FOREIGN KEY (TransactionId)
        REFERENCES Transactions(Id)
        ON DELETE CASCADE,
    FOREIGN KEY (DetailsId)
        REFERENCES DepositDetails(Id)
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

CREATE TABLE IF NOT EXISTS AchOdfiIds
(
    Id    INTEGER PRIMARY KEY,
    Value INTEGER NOT NULL UNIQUE
        CHECK (Value BETWEEN 0 AND 99999999)
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
         'TRN*1*', '*1411289245*0000877 26' || char(92), 0),
    (2,  'TRN_1411289245_000087726_ALT',
         'TRN*1*', '*1411289245*000087726 ' || char(92), 0),
    (3,  'TRN_1411648670',
         'TRN*1*', '*1411648670' || char(92), 0),
    (4,  'TRN_1066033492',
         'TRN*1*', '*1066033492' || char(92), 0),
    (5,  'TRN_1361236610_CP_DERIVED',
         'TRN*1*', '', 1),
    (6,  'TRN_1341858379',
         'TRN*1*', '*1341858379' || char(92), 0),
    (7,  'TRN_1364004756_4004756',
         'TRN*1*', '*1364004756*36 4004756~                                      0', 0),
    (8,  'TRN_1391995276_UMR01',
         'TRN*1*', '*139 1995276*0000UMR01' || char(92), 0),
    (9,  'TRN_1860507074_UHCEX',
         'TRN*1*', '*1860507074*0 000UHCEX' || char(92), 0),
    (10, 'TRN_1591031071_HCCLAIMPMT',
         'TRN*1*', '*1591031071~                                                    HCCLAIMPMT', 0),
    (11, 'TXP_337743360SOLE_012_20261231_T',
         'TXP*337743360SOLE*012*20261231*T*', char(92), 0),
    (12, 'RAW',
         '', '', 0)
ON CONFLICT (Id) DO UPDATE
SET
    Name = excluded.Name,
    Prefix = excluded.Prefix,
    Suffix = excluded.Suffix,
    ReconstructionKind = excluded.ReconstructionKind;

CREATE TABLE IF NOT EXISTS AchBankReferenceKinds
(
    Id   INTEGER PRIMARY KEY,
    Name TEXT NOT NULL UNIQUE
) STRICT;

CREATE TABLE IF NOT EXISTS AchTransactions
(
    TransactionId                  INTEGER PRIMARY KEY,
    OriginatorId                   INTEGER NOT NULL,
    CompanyDescriptiveDate         INTEGER,
    CompanyDescriptiveDateOverride TEXT,
    EntryDescriptionId             INTEGER NOT NULL,
    SecCodeId                      INTEGER NOT NULL,
    TraceOdfiId                    INTEGER,
    TraceSequence                  INTEGER,
    TraceNumberOverride            TEXT,
    EffectiveEntryDateId           INTEGER,
    IndividualIdentifier           TEXT,
    IndividualName                 TEXT,
    PaymentInformationFormatId     INTEGER,
    PaymentInformationPayload      TEXT,
    BankReferenceKindId            INTEGER,
    HasBankReference               INTEGER NOT NULL DEFAULT 0
        CHECK (HasBankReference IN (0, 1)),
    BankReferenceOverride          TEXT,

    FOREIGN KEY (TransactionId)
        REFERENCES Transactions(Id)
        ON DELETE CASCADE,
    FOREIGN KEY (OriginatorId)
        REFERENCES AchOriginators(Id),
    FOREIGN KEY (EntryDescriptionId)
        REFERENCES AchEntryDescriptions(Id),
    FOREIGN KEY (SecCodeId)
        REFERENCES AchSecCodes(Id),
    FOREIGN KEY (TraceOdfiId)
        REFERENCES AchOdfiIds(Id),
    FOREIGN KEY (EffectiveEntryDateId)
        REFERENCES DateValues(Id),
    FOREIGN KEY (PaymentInformationFormatId)
        REFERENCES AchPaymentInformationFormats(Id),
    FOREIGN KEY (BankReferenceKindId)
        REFERENCES AchBankReferenceKinds(Id),

    CHECK (
        (CompanyDescriptiveDate IS NULL) !=
        (CompanyDescriptiveDateOverride IS NULL)
        OR (CompanyDescriptiveDate IS NULL AND CompanyDescriptiveDateOverride IS NULL)
    ),
    CHECK (CompanyDescriptiveDate IS NULL OR CompanyDescriptiveDate BETWEEN 0 AND 999999),
    CHECK (
        ((TraceOdfiId IS NULL) = (TraceSequence IS NULL))
        AND NOT (TraceOdfiId IS NOT NULL AND TraceNumberOverride IS NOT NULL)
    ),
    CHECK (TraceSequence IS NULL OR TraceSequence BETWEEN 0 AND 9999999),
    CHECK (
        (PaymentInformationFormatId IS NULL) =
        (PaymentInformationPayload IS NULL)
    ),
    CHECK (HasBankReference = 1 OR BankReferenceOverride IS NULL)
) STRICT;

CREATE VIEW IF NOT EXISTS AchTransactionsExpanded AS
SELECT
    a.TransactionId,
    a.OriginatorId,
    originator.CompanyId,
    originator.CompanyName,
    CASE
        WHEN a.CompanyDescriptiveDateOverride IS NOT NULL
            THEN a.CompanyDescriptiveDateOverride
        WHEN a.CompanyDescriptiveDate IS NOT NULL
            THEN printf('%06d', a.CompanyDescriptiveDate)
    END AS CompanyDescriptiveDate,
    a.EntryDescriptionId,
    entryDescription.Description AS EntryDescription,
    a.SecCodeId,
    sec.Code AS SecCode,
    CASE
        WHEN a.TraceNumberOverride IS NOT NULL THEN a.TraceNumberOverride
        WHEN a.TraceOdfiId IS NOT NULL
            THEN printf('%08d%07d', odfi.Value, a.TraceSequence)
    END AS TraceNumber,
    a.EffectiveEntryDateId,
    effectiveDate.UnixSeconds AS EffectiveEntryUnixSeconds,
    a.IndividualIdentifier AS IndividualId,
    a.IndividualName,
    CASE paymentFormat.ReconstructionKind
        WHEN 1 THEN
            paymentFormat.Prefix
            || a.PaymentInformationPayload
            || '*1361236610*CP '
            || strftime(
                '%Y%m%d',
                date(
                    printf('%04d-01-01', 2000 + CAST(substr(a.PaymentInformationPayload, 2, 2) AS INTEGER)),
                    printf('+%d days', CAST(substr(a.PaymentInformationPayload, 4, 3) AS INTEGER) - 1)
                )
            )
            || substr(a.PaymentInformationPayload, 7)
            || '0-1376879510' || char(92)
        ELSE paymentFormat.Prefix || a.PaymentInformationPayload || paymentFormat.Suffix
    END AS PaymentRelatedInformation,
    a.BankReferenceKindId,
    bankKind.Name AS BankReferenceKind,
    CASE
        WHEN a.HasBankReference = 0 THEN NULL
        WHEN a.BankReferenceOverride IS NOT NULL THEN a.BankReferenceOverride
        WHEN a.TraceSequence IS NOT NULL
            THEN printf(
                '%03d%07dTC',
                CAST(strftime('%j', postingDate.UnixSeconds, 'unixepoch') AS INTEGER),
                a.TraceSequence)
    END AS BankReference
FROM AchTransactions a
JOIN Transactions transactionRow ON transactionRow.Id = a.TransactionId
JOIN DateValues postingDate ON postingDate.Id = transactionRow.PostingDateId
JOIN AchOriginators originator ON originator.Id = a.OriginatorId
JOIN AchEntryDescriptions entryDescription ON entryDescription.Id = a.EntryDescriptionId
JOIN AchSecCodes sec ON sec.Id = a.SecCodeId
LEFT JOIN AchOdfiIds odfi ON odfi.Id = a.TraceOdfiId
LEFT JOIN DateValues effectiveDate ON effectiveDate.Id = a.EffectiveEntryDateId
LEFT JOIN AchPaymentInformationFormats paymentFormat
  ON paymentFormat.Id = a.PaymentInformationFormatId
LEFT JOIN AchBankReferenceKinds bankKind ON bankKind.Id = a.BankReferenceKindId;

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
    TransactionId                 INTEGER PRIMARY KEY,
    DirectionId                   INTEGER NOT NULL,
    IsRealtime                    INTEGER NOT NULL
        CHECK (IsRealtime IN (0, 1)),
    CounterpartyId                INTEGER NOT NULL,
    ChaseTransactionNumber        INTEGER,
    ChaseTransactionNumberOverride TEXT,
    HasChaseReference             INTEGER NOT NULL
        CHECK (HasChaseReference IN (0, 1)),
    ChaseReferenceOverride        TEXT,

    FOREIGN KEY (TransactionId)
        REFERENCES Transactions(Id)
        ON DELETE CASCADE,
    FOREIGN KEY (DirectionId)
        REFERENCES TransferDirections(Id),
    FOREIGN KEY (CounterpartyId)
        REFERENCES TransferCounterparties(Id),
    CHECK (
        (ChaseTransactionNumber IS NULL) !=
        (ChaseTransactionNumberOverride IS NULL)
    ),
    CHECK (ChaseTransactionNumber IS NULL OR ChaseTransactionNumber BETWEEN 0 AND 99999999999),
    CHECK (HasChaseReference = 1 OR ChaseReferenceOverride IS NULL)
) STRICT;

CREATE VIEW IF NOT EXISTS AccountTransfersExpanded AS
SELECT
    t.TransactionId,
    t.DirectionId,
    t.IsRealtime,
    t.CounterpartyId,
    COALESCE(t.ChaseTransactionNumberOverride, printf('%011d', t.ChaseTransactionNumber))
        AS ChaseTransactionNumber,
    CASE
        WHEN t.HasChaseReference = 0 THEN NULL
        WHEN t.ChaseReferenceOverride IS NOT NULL THEN t.ChaseReferenceOverride
        ELSE '9' || substr(printf('%011d', t.ChaseTransactionNumber), -9) || 'RX'
    END AS ChaseReference
FROM AccountTransfers t;

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
    ImportFileId    INTEGER NOT NULL,
    SourceRowNumber INTEGER NOT NULL
        CHECK (SourceRowNumber >= 2),
    TransactionId   INTEGER NOT NULL,

    PRIMARY KEY (ImportFileId, SourceRowNumber),
    FOREIGN KEY (ImportFileId)
        REFERENCES ImportFiles(Id)
        ON DELETE CASCADE,
    FOREIGN KEY (TransactionId)
        REFERENCES Transactions(Id)
) WITHOUT ROWID, STRICT;

CREATE INDEX IF NOT EXISTS IX_Transactions_Dedup
    ON Transactions(AccountId, PostingDateId, AmountCents, TypeId);

CREATE INDEX IF NOT EXISTS IX_ImportRows_Transaction
    ON ImportRows(TransactionId);

-- The accounting layer is deliberately separate from the immutable Chase
-- import layer above.  It is the canonical, editable representation of the
-- rows shown in generated accounting workbooks.
CREATE TABLE IF NOT EXISTS AccountingCategories
(
    Id             INTEGER PRIMARY KEY,
    Name           TEXT NOT NULL COLLATE NOCASE UNIQUE,
    DisplayOrder   INTEGER NOT NULL DEFAULT 0,
    IsActive       INTEGER NOT NULL DEFAULT 1 CHECK (IsActive IN (0, 1)),
    NormalSide     TEXT CHECK (NormalSide IN ('DEBIT', 'CREDIT', 'NEUTRAL')),
    StatementGroup TEXT
) STRICT;

INSERT INTO AccountingCategories
    (Name, DisplayOrder, IsActive, NormalSide, StatementGroup)
VALUES
    ('Counselling Fee', 10, 1, 'CREDIT', 'Revenue'),
    ('Other Revenue', 20, 1, 'CREDIT', 'Revenue'),
    ('Refunds / Reimbursements', 25, 1, 'CREDIT', 'Revenue'),
    ('Transfers In', 30, 1, 'CREDIT', 'Transfer'),
    ('Transfers Out', 40, 1, 'DEBIT', 'Transfer'),
    ('Credit Card Payment', 45, 1, 'NEUTRAL', 'Transfer'),
    ('Owners Draw', 50, 1, 'DEBIT', 'Equity'),
    ('Payroll Taxes', 60, 1, 'DEBIT', 'Expense'),
    ('Rebates', 70, 1, 'DEBIT', 'Expense'),
    ('Rent', 80, 1, 'DEBIT', 'Expense'),
    ('Auto Expense', 90, 1, 'DEBIT', 'Expense'),
    ('Meals & Entertainment', 100, 1, 'DEBIT', 'Expense'),
    ('Insurance - Liability', 110, 1, 'DEBIT', 'Expense'),
    ('Insurance - Work Comp', 120, 1, 'DEBIT', 'Expense'),
    ('Interest Expense', 130, 1, 'DEBIT', 'Expense'),
    ('Legal Expense', 140, 1, 'DEBIT', 'Expense'),
    ('Office Expense', 160, 1, 'DEBIT', 'Expense'),
    ('Telephone', 170, 1, 'DEBIT', 'Expense'),
    ('Misc. Expense', 180, 1, 'DEBIT', 'Expense'),
    ('Accounting Fee', 200, 1, 'DEBIT', 'Expense'),
    ('Advertising', 210, 1, 'DEBIT', 'Expense'),
    ('Software Expense', 240, 1, 'DEBIT', 'Expense'),
    ('Continuing Ed', 260, 1, 'DEBIT', 'Expense'),
    ('LLC Fee', 270, 1, 'DEBIT', 'Expense'),
    ('Professional Association Fee', 280, 1, 'DEBIT', 'Expense'),
    ('Professional Licenses Fee', 290, 1, 'DEBIT', 'Expense'),
    ('WiFi Fee', 300, 1, 'DEBIT', 'Expense')
ON CONFLICT (Name) DO NOTHING;

CREATE TABLE IF NOT EXISTS AccountingTextValues
(
    Id    INTEGER PRIMARY KEY,
    Kind  TEXT NOT NULL CHECK (Kind IN ('DESCRIPTION', 'EXPLANATION')),
    Value TEXT NOT NULL,
    UNIQUE (Kind, Value)
) STRICT;

CREATE TABLE IF NOT EXISTS AccountingDateValues
(
    Id          INTEGER PRIMARY KEY,
    UnixSeconds INTEGER NOT NULL UNIQUE CHECK (UnixSeconds % 86400 = 0)
) STRICT;

CREATE TABLE IF NOT EXISTS AccountingMoneyValues
(
    Id    INTEGER PRIMARY KEY,
    Cents INTEGER NOT NULL UNIQUE
) STRICT;

CREATE TABLE IF NOT EXISTS AccountingCheckNumbers
(
    Id     INTEGER PRIMARY KEY,
    Number TEXT NOT NULL UNIQUE
) STRICT;

CREATE TABLE IF NOT EXISTS TimestampValues
(
    Id          INTEGER PRIMARY KEY,
    UnixSeconds INTEGER NOT NULL UNIQUE
) STRICT;

CREATE TABLE IF NOT EXISTS AccountingEntries
(
    Id                INTEGER PRIMARY KEY,
    AccountId         INTEGER NOT NULL,
    EntryDateId       INTEGER NOT NULL,
    AmountId          INTEGER NOT NULL,
    DescriptionTextId INTEGER NOT NULL,
    ExplanationTextId INTEGER,
    CategoryId        INTEGER,
    CheckNumberId     INTEGER,
    DisplayOrder      INTEGER NOT NULL,
    IsOpeningBalance  INTEGER NOT NULL DEFAULT 0 CHECK (IsOpeningBalance IN (0, 1)),
    IsManual          INTEGER NOT NULL DEFAULT 0 CHECK (IsManual IN (0, 1)),
    NeedsReview       INTEGER NOT NULL DEFAULT 0 CHECK (NeedsReview IN (0, 1)),
    IsSuppressed      INTEGER NOT NULL DEFAULT 0 CHECK (IsSuppressed IN (0, 1)),
    CreatedTimestampId  INTEGER NOT NULL,
    ModifiedTimestampId INTEGER NOT NULL,

    FOREIGN KEY (AccountId) REFERENCES Accounts(Id),
    FOREIGN KEY (EntryDateId) REFERENCES AccountingDateValues(Id),
    FOREIGN KEY (AmountId) REFERENCES AccountingMoneyValues(Id),
    FOREIGN KEY (DescriptionTextId) REFERENCES AccountingTextValues(Id),
    FOREIGN KEY (ExplanationTextId) REFERENCES AccountingTextValues(Id),
    FOREIGN KEY (CategoryId) REFERENCES AccountingCategories(Id),
    FOREIGN KEY (CheckNumberId) REFERENCES AccountingCheckNumbers(Id),
    FOREIGN KEY (CreatedTimestampId) REFERENCES TimestampValues(Id),
    FOREIGN KEY (ModifiedTimestampId) REFERENCES TimestampValues(Id)
) STRICT;

CREATE TABLE IF NOT EXISTS AccountingEntryTransactions
(
    AccountingEntryId INTEGER NOT NULL,
    TransactionId     INTEGER NOT NULL,
    PRIMARY KEY (AccountingEntryId, TransactionId),
    FOREIGN KEY (AccountingEntryId)
        REFERENCES AccountingEntries(Id)
        ON DELETE CASCADE,
    FOREIGN KEY (TransactionId)
        REFERENCES Transactions(Id)
) WITHOUT ROWID, STRICT;

CREATE INDEX IF NOT EXISTS IX_AccountingEntries_AccountDateOrder
    ON AccountingEntries(AccountId, EntryDateId, DisplayOrder);
CREATE UNIQUE INDEX IF NOT EXISTS UX_AccountingEntries_OpeningBalance
    ON AccountingEntries(AccountId, EntryDateId)
    WHERE IsOpeningBalance = 1;
CREATE INDEX IF NOT EXISTS IX_AccountingEntryTransactions_Transaction
    ON AccountingEntryTransactions(TransactionId);

PRAGMA optimize;

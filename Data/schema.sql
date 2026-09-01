PRAGMA foreign_keys = ON;
PRAGMA application_id = 1397705807; -- "SOLO"
PRAGMA user_version = 2;

CREATE TABLE Accounts (Id INTEGER PRIMARY KEY, Last4 INTEGER NOT NULL UNIQUE CHECK (Last4 BETWEEN 0 AND 9999), Name TEXT NOT NULL UNIQUE) STRICT;
INSERT INTO Accounts (Id, Last4, Name) VALUES (1,9350,'Savings'),(2,8936,'Checkings'),(3,8027,'Chase Visa');
CREATE TABLE TransactionTypes (Id INTEGER PRIMARY KEY, Code TEXT NOT NULL UNIQUE) STRICT;
CREATE TABLE ImportFormats (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE) STRICT;
CREATE TABLE DepositDetails (Id INTEGER PRIMARY KEY, Code TEXT NOT NULL UNIQUE CHECK (length(Code)>0)) STRICT;
INSERT INTO DepositDetails (Id, Code) VALUES (1,'CHECK'),(2,'DSLIP'),(3,'CREDIT'),(4,'DEBIT');

CREATE TABLE ImportFiles
(
    Id INTEGER PRIMARY KEY,
    FileSha256 BLOB NOT NULL UNIQUE CHECK (length(FileSha256)=32),
    AccountId INTEGER NOT NULL REFERENCES Accounts(Id),
    FormatId INTEGER NOT NULL REFERENCES ImportFormats(Id),
    DownloadDay INTEGER NOT NULL,
    ImportedAtUnixSeconds INTEGER NOT NULL
) STRICT;
CREATE TABLE ImportSourceData
(
    ImportFileId INTEGER PRIMARY KEY REFERENCES ImportFiles(Id) ON DELETE CASCADE,
    GzipData BLOB NOT NULL CHECK (length(GzipData)>0)
) STRICT;
CREATE TABLE Transactions
(
    Id INTEGER PRIMARY KEY,
    AccountId INTEGER NOT NULL REFERENCES Accounts(Id),
    PostingDay INTEGER NOT NULL,
    AmountCents INTEGER NOT NULL,
    TypeId INTEGER NOT NULL REFERENCES TransactionTypes(Id)
) STRICT;
CREATE TABLE DepositTransactions
(
    TransactionId INTEGER PRIMARY KEY REFERENCES Transactions(Id) ON DELETE CASCADE,
    DetailsOverrideId INTEGER REFERENCES DepositDetails(Id),
    BalanceCents INTEGER,
    CheckOrSlipNumber TEXT
) STRICT;

CREATE TABLE MerchantDescriptors (Id INTEGER PRIMARY KEY, Descriptor TEXT NOT NULL UNIQUE) STRICT;
CREATE TABLE CreditCardCategories (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE) STRICT;
CREATE TABLE CreditCardTransactions
(
    TransactionId INTEGER PRIMARY KEY REFERENCES Transactions(Id) ON DELETE CASCADE,
    TransactionDay INTEGER NOT NULL,
    MerchantId INTEGER NOT NULL REFERENCES MerchantDescriptors(Id),
    CategoryId INTEGER REFERENCES CreditCardCategories(Id),
    Memo TEXT
) STRICT;

CREATE TABLE AchCompanies (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE) STRICT;
CREATE TABLE AchOriginators
(
    Id INTEGER PRIMARY KEY,
    CompanyIdentifier TEXT NOT NULL UNIQUE,
    CompanyId INTEGER NOT NULL REFERENCES AchCompanies(Id)
) STRICT;
CREATE TABLE AchEntryDescriptions (Id INTEGER PRIMARY KEY, Description TEXT NOT NULL UNIQUE) STRICT;
CREATE TABLE AchSecCodes (Id INTEGER PRIMARY KEY, Code TEXT NOT NULL UNIQUE) STRICT;
CREATE TABLE AchBankReferenceKinds (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE) STRICT;
CREATE TABLE AchProfiles
(
    Id INTEGER PRIMARY KEY,
    OriginatorId INTEGER NOT NULL REFERENCES AchOriginators(Id),
    EntryDescriptionId INTEGER NOT NULL REFERENCES AchEntryDescriptions(Id),
    SecCodeId INTEGER NOT NULL REFERENCES AchSecCodes(Id),
    BankReferenceKindId INTEGER REFERENCES AchBankReferenceKinds(Id)
) STRICT;
CREATE UNIQUE INDEX UX_AchProfiles_Identity ON AchProfiles(OriginatorId,EntryDescriptionId,SecCodeId,coalesce(BankReferenceKindId,0));
CREATE TABLE AchTransactions
(
    TransactionId INTEGER PRIMARY KEY REFERENCES Transactions(Id) ON DELETE CASCADE,
    ProfileId INTEGER NOT NULL REFERENCES AchProfiles(Id),
    CompanyDescriptiveDate TEXT,
    TraceNumber TEXT,
    EffectiveEntryDay INTEGER,
    IndividualIdentifier TEXT,
    IndividualName TEXT,
    RawPaymentRelatedInformation TEXT,
    BankReference TEXT,
    CHECK (BankReference IS NOT NULL OR (TraceNumber IS NULL AND EffectiveEntryDay IS NULL))
) STRICT;
CREATE TABLE AchTrnAddenda
(
    TransactionId INTEGER PRIMARY KEY REFERENCES AchTransactions(TransactionId) ON DELETE CASCADE,
    TraceType INTEGER NOT NULL CHECK (TraceType=1),
    Reference TEXT NOT NULL,
    OriginatorIdentifier TEXT NOT NULL,
    AdditionalText TEXT,
    Terminator TEXT NOT NULL CHECK (Terminator=char(92))
) STRICT;
CREATE TABLE AchTaxPaymentAddenda
(
    TransactionId INTEGER PRIMARY KEY REFERENCES AchTransactions(TransactionId) ON DELETE CASCADE,
    TaxpayerId TEXT NOT NULL,
    TaxType TEXT NOT NULL,
    TaxPeriod TEXT NOT NULL,
    AmountType TEXT NOT NULL,
    AmountText TEXT NOT NULL,
    Terminator TEXT NOT NULL CHECK (Terminator=char(92))
) STRICT;

CREATE TABLE TransferDirections (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE CHECK (Name IN ('TO','FROM'))) STRICT;
INSERT INTO TransferDirections (Id,Name) VALUES (1,'TO'),(2,'FROM');
CREATE TABLE FinancialInstitutions (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE) STRICT;
CREATE TABLE TransferCounterparties
(
    Id INTEGER PRIMARY KEY,
    InstitutionId INTEGER NOT NULL REFERENCES FinancialInstitutions(Id),
    AccountLabel TEXT NOT NULL,
    InternalAccountId INTEGER REFERENCES Accounts(Id),
    ExternalLast4 INTEGER CHECK (ExternalLast4 BETWEEN 0 AND 9999),
    CHECK ((InternalAccountId IS NULL) <> (ExternalLast4 IS NULL))
) STRICT;
CREATE UNIQUE INDEX UX_TransferCounterparties_Identity ON TransferCounterparties(InstitutionId,AccountLabel,coalesce(InternalAccountId,0),coalesce(ExternalLast4,-1));
CREATE TABLE AccountTransfers
(
    TransactionId INTEGER PRIMARY KEY REFERENCES Transactions(Id) ON DELETE CASCADE,
    DirectionId INTEGER NOT NULL REFERENCES TransferDirections(Id),
    IsRealtime INTEGER NOT NULL CHECK (IsRealtime IN (0,1)),
    CounterpartyId INTEGER NOT NULL REFERENCES TransferCounterparties(Id),
    ChaseTransactionNumber TEXT NOT NULL,
    ChaseReference TEXT
) STRICT;
CREATE TABLE ChaseCardPayments
(
    TransactionId INTEGER PRIMARY KEY REFERENCES Transactions(Id) ON DELETE CASCADE,
    TargetAccountId INTEGER NOT NULL REFERENCES Accounts(Id)
) STRICT;
CREATE TABLE DebitCardTransactions
(
    TransactionId INTEGER PRIMARY KEY REFERENCES Transactions(Id) ON DELETE CASCADE,
    MerchantId INTEGER NOT NULL REFERENCES MerchantDescriptors(Id)
) STRICT;
CREATE TABLE AtmTransactions
(
    TransactionId INTEGER PRIMARY KEY REFERENCES Transactions(Id) ON DELETE CASCADE,
    ActionId INTEGER NOT NULL CHECK (ActionId IN (1,2)),
    TerminalId TEXT,
    Location TEXT
) STRICT;
CREATE TABLE FeeTransactions (TransactionId INTEGER PRIMARY KEY REFERENCES Transactions(Id) ON DELETE CASCADE, Description TEXT NOT NULL) STRICT;
CREATE TABLE RealTimePaymentSenders (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE) STRICT;
CREATE TABLE RealTimePayments
(
    TransactionId INTEGER PRIMARY KEY REFERENCES Transactions(Id) ON DELETE CASCADE,
    AbaRoutingNumber INTEGER NOT NULL CHECK (AbaRoutingNumber BETWEEN 0 AND 999999999),
    SenderId INTEGER NOT NULL REFERENCES RealTimePaymentSenders(Id),
    Reference TEXT NOT NULL,
    OriginatorId INTEGER REFERENCES AchOriginators(Id),
    RawOriginatorIdentifier TEXT,
    PaymentCode TEXT NOT NULL,
    Tin TEXT,
    Npi TEXT,
    ReceiverName TEXT,
    EntryDescriptionId INTEGER REFERENCES AchEntryDescriptions(Id),
    RawPurpose TEXT,
    InstructionId TEXT NOT NULL,
    ReceivedSecondOfDay INTEGER NOT NULL CHECK (ReceivedSecondOfDay BETWEEN 0 AND 86399),
    BankReference TEXT NOT NULL,
    CHECK ((OriginatorId IS NULL) <> (RawOriginatorIdentifier IS NULL)),
    CHECK ((EntryDescriptionId IS NULL AND RawPurpose IS NULL) OR
           ((EntryDescriptionId IS NULL) <> (RawPurpose IS NULL)))
) STRICT;
CREATE TABLE UnparsedDepositDescriptions
(
    TransactionId INTEGER PRIMARY KEY REFERENCES Transactions(Id) ON DELETE CASCADE,
    Description TEXT NOT NULL
) STRICT;
CREATE TABLE ImportRows
(
    ImportFileId INTEGER NOT NULL REFERENCES ImportFiles(Id) ON DELETE CASCADE,
    SourceRowNumber INTEGER NOT NULL CHECK (SourceRowNumber>=2),
    TransactionId INTEGER NOT NULL REFERENCES Transactions(Id),
    PRIMARY KEY (ImportFileId,SourceRowNumber)
) WITHOUT ROWID, STRICT;

CREATE INDEX IX_Transactions_Dedupe ON Transactions(AccountId,PostingDay,AmountCents,TypeId);
CREATE INDEX IX_ImportRows_Transaction ON ImportRows(TransactionId);
CREATE INDEX IX_AchTransactions_Profile ON AchTransactions(ProfileId);

CREATE VIEW vTransactions AS
SELECT t.Id,printf('%04d',a.Last4) AccountLast4,a.Name AccountName,date(t.PostingDay*86400,'unixepoch') PostingDate,t.PostingDay,t.AmountCents,tt.Code TypeCode
FROM Transactions t JOIN Accounts a ON a.Id=t.AccountId JOIN TransactionTypes tt ON tt.Id=t.TypeId;
CREATE VIEW vDepositTransactions AS
SELECT v.*,coalesce(dd.Code,CASE WHEN v.TypeCode='CHECK_PAID' THEN 'CHECK' WHEN v.TypeCode='CHECK_DEPOSIT' THEN 'DSLIP' WHEN v.AmountCents>0 THEN 'CREDIT' ELSE 'DEBIT' END) DetailsCode,d.BalanceCents,d.CheckOrSlipNumber
FROM vTransactions v JOIN DepositTransactions d ON d.TransactionId=v.Id LEFT JOIN DepositDetails dd ON dd.Id=d.DetailsOverrideId;
CREATE VIEW vCreditCardTransactions AS
SELECT v.*,date(c.TransactionDay*86400,'unixepoch') TransactionDate,m.Descriptor MerchantDescriptor,cc.Name CategoryName,c.Memo
FROM vTransactions v JOIN CreditCardTransactions c ON c.TransactionId=v.Id JOIN MerchantDescriptors m ON m.Id=c.MerchantId LEFT JOIN CreditCardCategories cc ON cc.Id=c.CategoryId;
CREATE VIEW vAchTransactions AS
SELECT d.*,co.Name CompanyName,o.CompanyIdentifier,a.CompanyDescriptiveDate,ed.Description EntryDescription,sc.Code SecCode,a.TraceNumber,
 CASE WHEN a.EffectiveEntryDay IS NULL THEN NULL ELSE date(a.EffectiveEntryDay*86400,'unixepoch') END EffectiveEntryDate,
 a.IndividualIdentifier,a.IndividualName,
 coalesce(a.RawPaymentRelatedInformation,CASE WHEN tr.TransactionId IS NOT NULL THEN 'TRN*'||tr.TraceType||'*'||tr.Reference||'*'||tr.OriginatorIdentifier||CASE WHEN tr.AdditionalText IS NULL THEN '' ELSE '*'||tr.AdditionalText END||tr.Terminator WHEN tx.TransactionId IS NOT NULL THEN 'TXP*'||tx.TaxpayerId||'*'||tx.TaxType||'*'||tx.TaxPeriod||'*'||tx.AmountType||'*'||tx.AmountText||tx.Terminator END) PaymentRelatedInformation,
 brk.Name BankReferenceKind,a.BankReference
FROM vDepositTransactions d JOIN AchTransactions a ON a.TransactionId=d.Id JOIN AchProfiles p ON p.Id=a.ProfileId JOIN AchOriginators o ON o.Id=p.OriginatorId JOIN AchCompanies co ON co.Id=o.CompanyId JOIN AchEntryDescriptions ed ON ed.Id=p.EntryDescriptionId JOIN AchSecCodes sc ON sc.Id=p.SecCodeId LEFT JOIN AchBankReferenceKinds brk ON brk.Id=p.BankReferenceKindId LEFT JOIN AchTrnAddenda tr ON tr.TransactionId=a.TransactionId LEFT JOIN AchTaxPaymentAddenda tx ON tx.TransactionId=a.TransactionId;
CREATE VIEW vAccountTransfers AS
SELECT d.*,dir.Name Direction,x.IsRealtime,fi.Name Institution,c.AccountLabel,printf('%04d',coalesce(ia.Last4,c.ExternalLast4)) CounterpartyLast4,ia.Name InternalAccountName,x.ChaseTransactionNumber,x.ChaseReference
FROM vDepositTransactions d JOIN AccountTransfers x ON x.TransactionId=d.Id JOIN TransferDirections dir ON dir.Id=x.DirectionId JOIN TransferCounterparties c ON c.Id=x.CounterpartyId JOIN FinancialInstitutions fi ON fi.Id=c.InstitutionId LEFT JOIN Accounts ia ON ia.Id=c.InternalAccountId;

# SQLite optimization report

## Baseline

Audited read-only from `C:\Users\Ryan\0_Inbox\Documents\SoloPractice\SoloPractice.db` before edits.

- `PRAGMA integrity_check`: `ok`
- `PRAGMA foreign_key_check`: 0 rows
- Page size: 4,096 bytes
- Page count: 231
- File size: 946,176 bytes
- Transactions: 1,259 (1,221 deposit; 38 credit card; 997 ACH)
- `user_version`: 0
- `application_id`: 0

Baseline `dbstat` objects larger than one page:

| Object | Bytes | Pages |
|---|---:|---:|
| `sqlite_autoindex_ImportRows_1` | 65,536 | 16 |
| `ImportRows` | 61,440 | 15 |
| `sqlite_autoindex_AchPaymentRelatedInformation_1` | 49,152 | 12 |
| `AchPaymentRelatedInformation` | 45,056 | 11 |
| `Transactions` | 45,056 | 11 |
| `AchTransactions` | 36,864 | 9 |
| `IX_Transactions_Account_PostingDate` | 36,864 | 9 |
| `sqlite_autoindex_AchTraceNumbers_1` | 32,768 | 8 |
| `AchTraceNumbers` | 28,672 | 7 |
| `DepositTransactions` | 28,672 | 7 |
| `IX_Transactions_Type` | 28,672 | 7 |
| `sqlite_autoindex_AchBankReferences_1` | 28,672 | 7 |
| `AchBankReferences` | 24,576 | 6 |
| `MoneyValues` | 24,576 | 6 |
| `AchIndividualIdentifiers` | 20,480 | 5 |
| `IX_ImportRows_Transaction` | 20,480 | 5 |
| `sqlite_autoindex_AchIndividualIdentifiers_1` | 20,480 | 5 |
| `sqlite_schema` | 20,480 | 5 |
| seven ACH child indexes (`BankReference`, `EntryDescription`, `IndividualIdentifier`, `IndividualName`, `Originator`, `PaymentInformation`, `Trace`) | 16,384 each | 4 each |
| `AchIndividualNames` | 12,288 | 3 |
| `DateValues` | 12,288 | 3 |
| `IX_AchTransactions_BankReferenceKind` | 12,288 | 3 |
| `IX_AchTransactions_SecCode` | 12,288 | 3 |
| `sqlite_autoindex_AchIndividualNames_1` | 12,288 | 3 |

All other baseline tables and indexes occupied one 4,096-byte page each: `AccountTransfers`, `Accounts`, `AchBankReferenceKinds`, `AchEntryDescriptions`, `AchOriginators`, `AchSecCodes`, `AtmTransactions`, `ChaseCardPayments`, `CreditCardCategories`, `CreditCardMerchants`, `CreditCardTransactions`, `DebitCardTransactions`, `DepositDetails`, `FeeTransactions`, `ImportFiles`, `ImportFormats`, `RealTimePayments`, `TransactionTypes`, `TransferCounterparties`, `TransferDirections`, `UnparsedDepositDescriptions`, both transfer indexes, and their SQLite autoindexes.

The complete per-text-column baseline is reproducible with:

```powershell
wsl python3 tools/audit_sqlite.py /mnt/c/Users/Ryan/0_Inbox/Documents/SoloPractice/SoloPractice.db
```

## Optimized result

Fresh import of the same three current Chase downloads, followed by `PRAGMA optimize` and `VACUUM`:

- `PRAGMA integrity_check`: `ok`
- `PRAGMA foreign_key_check`: 0 rows
- Page size: 4,096 bytes (unchanged)
- Page count: 125
- File size: 512,000 bytes (exactly 500 KiB)
- Reduction: 434,176 bytes / 45.89%
- `user_version`: 2
- `application_id`: 1,397,705,807 (`SOLO`)
- Transactions: 1,259 (1,221 deposit; 38 credit card; 997 ACH)
- Import files/rows: 3 / 1,259
- Unparsed descriptions: 0
- ACH profiles: 20
- Deposit detail overrides: 0
- RTP raw originator/purpose fallbacks: 0
- Compressed exact source data: 49,152 bytes of allocated pages
- Timed fresh three-file import: 583 ms (including post-import `PRAGMA optimize`)
- Exact three-file re-import check: <1 ms reported by the millisecond timer

An additional byte-different overlapping fixture produced 4 import files and 1,261 provenance rows while retaining exactly 1,259 canonical transactions. Its vacuumed database remained 512,000 bytes.

Optimized `dbstat`:

| Object | Bytes | Pages |
|---|---:|---:|
| `AchTransactions` | 86,016 | 21 |
| `ImportSourceData` | 49,152 | 12 |
| `AchTrnAddenda` | 36,864 | 9 |
| `IX_Transactions_Dedupe` | 28,672 | 7 |
| `Transactions` | 28,672 | 7 |
| `DepositTransactions` | 20,480 | 5 |
| `IX_ImportRows_Transaction` | 20,480 | 5 |
| `ImportRows` | 20,480 | 5 |
| `sqlite_schema` | 20,480 | 5 |
| `IX_AchTransactions_Profile` | 16,384 | 4 |

Every other optimized object occupies one 4,096-byte page: `AccountTransfers`, `Accounts`, `AchBankReferenceKinds`, `AchCompanies`, `AchEntryDescriptions`, `AchOriginators`, `AchProfiles`, `AchSecCodes`, `AchTaxPaymentAddenda`, `AtmTransactions`, `ChaseCardPayments`, `CreditCardCategories`, `CreditCardTransactions`, `DebitCardTransactions`, `DepositDetails`, `FeeTransactions`, `FinancialInstitutions`, `ImportFiles`, `ImportFormats`, `MerchantDescriptors`, `RealTimePaymentSenders`, `RealTimePayments`, `TransactionTypes`, `TransferCounterparties`, `TransferDirections`, `UnparsedDepositDescriptions`, `UX_AchProfiles_Identity`, `UX_TransferCounterparties_Identity`, the necessary UNIQUE autoindexes, and `sqlite_stat1`.

The new file includes the exact gzip-compressed source bytes. Excluding those 12 archive pages, semantic/index storage is 462,848 bytes.

## Semantic and parser validation

`tools/compare_semantics.py` compares multisets rather than relying on transaction IDs. It passed for common transactions, deposits, credit cards, all 997 ACH rows, all 105 transfers, 24 card payments, 3 debit-card rows, 2 ATM rows, 1 fee, all 4 RTP rows, and unparsed fallbacks.

Recognized backslash-terminated addenda normalized into 599 `TRN` and 2 `TXP` subtype rows. Twenty-four `TRN*` values use a materially different tilde/padded suffix and remain exact raw fallbacks; no bytes are trimmed or discarded. All 625 values round-trip exactly in `vAchTransactions`.

The integration runner also decompresses every `ImportSourceData` blob and verifies its SHA-256 against `ImportFiles.FileSha256`.

## Query plans

The old ACH duplicate plan used only `(AccountLast4, PostingDateUnixSeconds)` and filtered amount/type later. Every optimized duplicate family (credit card, ACH, transfer, card payment, debit card, ATM, fee, RTP, unparsed, and base-only) reports:

```text
SEARCH t USING COVERING INDEX IX_Transactions_Dedupe
  (AccountId=? AND PostingDay=? AND AmountCents=? AND TypeId=?)
```

Subtype access is by `INTEGER PRIMARY KEY`; ACH additionally uses the covering `IX_AchTransactions_Profile (ProfileId, rowid)`. Reverse provenance uses the covering `IX_ImportRows_Transaction (TransactionId)` index. Account/day-range queries use the leftmost `(AccountId, PostingDay)` prefix.

The importer still verifies every canonical subtype field after narrowing candidates; no hash-only financial deduplication was introduced.

## Migration / recreate

Schema v2 is intentionally a clean rebuild. Keep the old database as a backup, preserve the original Chase CSV files, move/rename the old `SoloPractice.db`, then start SoloPractice and re-import the CSVs. The application refuses to open a populated v0/v1 database rather than attempting a lossy in-place migration. Newly imported files are archived byte-for-byte inside `ImportSourceData`.

For a repeatable validation run:

```powershell
dotnet run --project Tests\SoloPractice.IntegrationTests -- <new-db-path> <csv1> <csv2> <csv3> "Tests\Fixtures\Chase9350_Activity_20260901 (3).csv"
```

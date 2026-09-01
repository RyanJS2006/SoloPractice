# ![](https://github.com/RyanJS2006/SoloPractice/blob/961d01c9afaf5ebb3b92e0def37c499e928029a1/Images/SoloPractice.png)

**SoloPractice** is a C# accounting utility designed to turn transaction data downloaded from Chase into a structured, persistent database that can be used to automate otherwise repetitive accounting work.

The long-term goal is to handle the routine parts of bookkeeping automatically while still leaving anything requiring human judgment—such as descriptions, explanations, and corrections—easy to review and edit in a normal spreadsheet.

> **Status:** SoloPractice is currently under active development. Chase CSV importing and the core database are implemented; spreadsheet generation and document-management features are still planned.

## Overview

The intended workflow is:

```text
Chase CSV downloads
        │
        ▼
   SoloPractice
        │
        ▼
 SQLite database
        │
        ├──► Accounting worksheet
        ├──► Receipt organization
        └──► Insurance / tax document organization
```

Rather than treating each downloaded CSV as an isolated spreadsheet, SoloPractice builds a persistent database containing transactions from multiple Chase accounts and downloads.

Overlapping downloads can therefore be imported without creating duplicate transactions, and information contained in Chase's descriptions can be separated into structured fields that are easier to query and automate against later.

## Current Features

### Chase CSV Importing

SoloPractice currently supports importing Chase transaction-history CSV files directly from the CLI.

A CSV can be:

* dragged into the terminal;
* pasted as a file path;
* typed manually; or
* dragged directly onto the main menu.

The importer automatically determines the Chase CSV format and validates the file before committing it to the database.

Currently supported Chase exports include:

* credit card activity;
* checking/deposit account activity.

After an import, SoloPractice reports:

```text
File
Account
Format
Rows read
New transactions
Existing transactions
Unparsed descriptions
```

Press `Esc` at any input screen to return to the previous menu.

### Duplicate Protection

SoloPractice is designed to safely handle overlapping Chase downloads.

Each imported file is identified using its **SHA-256 hash**. Importing the exact same download twice is therefore detected immediately.

Transactions are also matched against transactions already stored in the database. This allows two different Chase downloads containing overlapping date ranges to reference the same underlying transactions instead of duplicating them.

The database separately records which source CSV row produced each transaction, preserving the relationship between imported files and the normalized transaction data.

### Structured Transaction Parsing

Deposit-account descriptions often contain significantly more information than a simple merchant name.

SoloPractice attempts to recognize and separate several Chase transaction formats, including:

* ACH credits and debits;
* account transfers;
* Chase credit-card payments;
* paid checks;
* remote check deposits;
* debit-card transactions;
* ATM withdrawals and cash deposits;
* fees;
* real-time payments.

For example, information embedded inside an ACH description can be separated into fields such as the company, SEC code, trace number, individual identifier, effective-entry date, and bank reference.

This allows information that Chase normally stores inside one large description string to become independently queryable.

If SoloPractice encounters a deposit-description format it does not recognize, the import is **not discarded**. The complete original description is retained as an unparsed description so that unsupported Chase formats do not result in lost transaction data.

## Database

SoloPractice uses **SQLite** for its local database.

The database is automatically created in the user's Documents directory:

```text
Documents/
└── SoloPractice/
    └── SoloPractice.db
```

The schema separates common transaction information from type-specific information.

Conceptually:

```text
                    Accounts
                       │
                       ▼
ImportFiles ─────► Transactions ◄───── ImportRows
                       │
             ┌─────────┴─────────┐
             ▼                   ▼
    DepositTransactions   CreditCardTransactions
             │
     ┌───────┼────────┬──────────┬───────── ...
     ▼       ▼        ▼          ▼
    ACH   Transfers   ATM   Debit Card
```

Common information such as account, posting date, amount, and transaction type lives in the main `Transactions` table.

Additional tables contain information specific to the particular transaction type.

The schema also normalizes frequently reused values such as:

* accounts;
* dates;
* monetary values;
* transaction types;
* credit-card merchants;
* credit-card categories;
* ACH originators;
* ACH SEC codes;
* ACH trace numbers;
* transfer counterparties.

SQLite foreign-key enforcement is enabled whenever SoloPractice opens the database.

## Data Preservation

One of the main design goals of SoloPractice is to avoid throwing away information from Chase's source data.

The importer therefore keeps enough information to associate a normalized transaction with the CSV file and source row it originally came from.

Unknown description formats are retained verbatim rather than being partially parsed or ignored.

This makes the database useful not only for generating accounting worksheets, but also as a long-term structured representation of the original Chase transaction history.

## Planned Features

The CLI already contains entries for several features that are not implemented yet.

### Accounting Worksheets

Planned support includes:

* generating an accounting worksheet from the database;
* updating an existing worksheet with newly imported transactions;
* opening the worksheet in the user's spreadsheet editor;
* allowing descriptions and explanations to be manually edited;
* synchronizing relevant edits back into SoloPractice.

The default worksheet location is intended to be:

```text
Documents/
└── SoloPractice/
    └── <year> Accounting Worksheet.xlsx
```

### Receipt Scans

Planned tools will help organize scanned receipts and associate them with accounting records.

### Insurance and Tax Documents

SoloPractice is also intended to provide a consistent way to organize insurance-company statements, tax forms, and related accounting documents.

## Command-Line Interface

The current main menu is:

```text
1. Import Chase Bank Statement CSV
2. Generate/Update/Open Accounting Spreadsheet
3. Upload Receipt Scans
4. Upload Insurance Company Statements and Tax Forms
5. About

[Esc] Exit
```

At present, **option 1 is implemented**. Options 2–5 are placeholders while the rest of the application is being developed.

The interface also redraws itself when the terminal is resized and adapts the SoloPractice title to narrower terminal windows.

## Technology

SoloPractice is written in **C#** and currently uses:

* **SQLite** for persistent local storage;
* **Microsoft.Data.Sqlite** for database access;
* **CsvHelper** for parsing Chase CSV exports.

The application is intentionally built around a lightweight command-line interface rather than a large desktop GUI.

## Building

Clone the repository:

```bash
git clone https://github.com/RyanJS2006/SoloPractice.git
cd SoloPractice
```

Then restore and build the project with the .NET SDK:

```bash
dotnet restore
dotnet build
```

Run it with:

```bash
dotnet run
```

You can also open the project in Visual Studio and run it from there.

## Chase CSV Naming

The importer currently expects Chase activity downloads to retain Chase's normal filename structure:

```text
Chase####_Activity_YYYYMMDD.csv
```

Duplicate-number suffixes produced by the operating system are also accepted, for example:

```text
Chase####_Activity_YYYYMMDD (1).csv
```

The account identifier and download date are derived from this filename.

## Disclaimer

SoloPractice is an independent personal project and is **not affiliated with, endorsed by, or supported by JPMorgan Chase & Co.**

Financial data handled by the program is stored locally. As with any software processing financial records, maintain backups of both the original Chase downloads and the SoloPractice database.

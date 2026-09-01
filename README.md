# ![](Images/SoloPractice.png)

SoloPractice is a small accounting utility I made to help automate and streamline bookkeeping for a small business.

It imports transaction history downloaded from Chase into a local SQLite database, with the goal of automatically handling most of the repetitive work involved in maintaining an accounting worksheet.

## Download

### [Download the latest release](https://github.com/RyanJS2006/SoloPractice/releases/latest)

[View all releases](https://github.com/RyanJS2006/SoloPractice/releases)

> SoloPractice is currently under development. Some features shown in the program are not implemented yet.

## Features

- Import Chase credit card and bank account CSV files
- Store transactions in a local SQLite database
- Detect duplicate downloads and overlapping transactions
- Parse transaction information into structured database fields
- Preserve unrecognized Chase descriptions without losing data
- Drag-and-drop CSV files directly into the CLI

### Planned

- Generate and update accounting worksheets
- Open worksheets for manual descriptions and explanations
- Synchronize spreadsheet edits back into the database
- Organize receipt scans
- Organize insurance statements and tax documents

## Usage

1. Download and extract the latest release.
2. Run `SoloPractice`.
3. Select **Import Chase Bank Statement CSV**.
4. Drag a Chase `.csv` file into the window.

Imported data is stored locally in:

```text
Documents/SoloPractice/
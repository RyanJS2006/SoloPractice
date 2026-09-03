using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using SoloPractice.Data;
using SoloPractice.Utilities;
using System.Diagnostics;
using System.Globalization;

namespace SoloPractice.Services;

internal sealed record AccountingWorkbookResult(
    string WorkbookPath,
    int CheckingRows,
    int SavingsRows,
    int CreditCardRows,
    int ReviewRows,
    bool ReplacedExistingWorkbook);

internal sealed record AccountingWorkbookSyncResult(
    int RowsInserted,
    int RowsUpdated,
    int CategoriesAdded,
    int UnresolvedRows);

internal sealed class AccountingWorkbookValidationException : Exception
{
    public AccountingWorkbookValidationException(string sheet, int row, string message)
        : base($"{sheet}, row {row}: {message}")
    {
        Sheet = sheet;
        Row = row;
    }

    public string Sheet { get; }
    public int Row { get; }
}

internal static class AccountingWorkbookService
{
    private const int HeaderRow = 2;
    private const int FirstDataRow = 3;
    private const int EntryIdColumn = 8;
    private const int SourceIdsColumn = 9;
    private const string SummaryMarker = "__SOLOPRACTICE_CATEGORY_SUMMARY__";

    private static readonly (string Last4, string SheetSuffix, string Title, bool IsCard)[] Accounts =
    [
        (AccountingClassifier.CheckingAccount, "Checking", "CHECKING ACCOUNT: 8936", false),
        (AccountingClassifier.SavingsAccount, "Savings", "SAVINGS ACCOUNT: 9350", false),
        (AccountingClassifier.CreditCardAccount, "Chase Visa", "Chase Visa", true)
    ];

    public static string GetWorkbookPath(int year) =>
        AppPaths.GetWorkbookPath(year);

    public static AccountingWorkbookResult Generate(
        int year,
        string? workbookPath = null,
        bool openAfterSaving = false)
    {
        AppPaths.EnsureAccountingYearDirectoriesExist(year);
        workbookPath ??= GetWorkbookPath(year);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(workbookPath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        IReadOnlyList<AccountingEntry> entries = AccountingLedgerService.ReadEntries(year);
        IReadOnlyList<AccountingCategory> categories = AccountingLedgerService.ReadCategories();
        IReadOnlyDictionary<(string AccountLast4, string Category), long> totals =
            AccountingLedgerService.ReadCategoryTotals(year);
        using var workbook = new XLWorkbook();
        IXLWorksheet helper = BuildHelperSheet(workbook, categories);

        foreach (var account in Accounts)
        {
            List<AccountingEntry> accountEntries = entries
                .Where(entry => entry.AccountLast4 == account.Last4)
                .ToList();
            AccountingEntry openingEntry = accountEntries.Single(entry => entry.IsOpeningBalance);
            BuildAccountSheet(
                workbook,
                helper,
                year,
                account.Last4,
                account.SheetSuffix,
                account.Title,
                account.IsCard,
                openingEntry,
                accountEntries.Where(entry => !entry.IsOpeningBalance).ToList(),
                categories,
                totals);
        }

        BuildTrialBalances(workbook, year, entries, categories);
        helper.Visibility = XLWorksheetVisibility.VeryHidden;

        bool replaced = File.Exists(workbookPath);
        SaveCrashSafely(workbook, workbookPath, replaced);

        if (openAfterSaving)
            OpenWorkbook(workbookPath);

        return new AccountingWorkbookResult(
            workbookPath,
            entries.Count(entry => entry.AccountLast4 == AccountingClassifier.CheckingAccount && !entry.IsOpeningBalance),
            entries.Count(entry => entry.AccountLast4 == AccountingClassifier.SavingsAccount && !entry.IsOpeningBalance),
            entries.Count(entry => entry.AccountLast4 == AccountingClassifier.CreditCardAccount && !entry.IsOpeningBalance),
            entries.Count(entry => entry.NeedsReview),
            replaced);
    }

    public static AccountingWorkbookSyncResult ImportWorkbookEdits(
        int year,
        string workbookPath)
    {
        List<WorkbookRow> rows = ReadWorkbookRows(year, workbookPath);

        using SqliteConnection connection = Database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        int insertedRows = 0;
        int updatedRows = 0;
        int categoriesAdded = 0;

        try
        {
            foreach (WorkbookRow row in rows)
            {
                long accountId = ReadAccountId(connection, transaction, row.AccountLast4);
                long dateId = AccountingLookup.GetOrCreateDate(connection, transaction, row.Date);
                long moneyId = AccountingLookup.GetOrCreateMoney(connection, transaction, row.AmountCents);
                long descriptionId = AccountingLookup.GetOrCreateText(connection, transaction, "DESCRIPTION", row.Description);
                long? explanationId = string.IsNullOrWhiteSpace(row.Explanation)
                    ? null
                    : AccountingLookup.GetOrCreateText(connection, transaction, "EXPLANATION", row.Explanation);
                long? categoryId = null;
                if (!string.IsNullOrWhiteSpace(row.Category))
                {
                    categoryId = AccountingLookup.GetOrCreateCategory(
                        connection,
                        transaction,
                        row.Category,
                        out bool categoryInserted);
                    if (categoryInserted)
                        categoriesAdded++;

                    EnsureCategoryNormalSide(
                        connection,
                        transaction,
                        categoryId.Value,
                        row.AmountCents < 0 ? "DEBIT" : "CREDIT");
                }

                long? checkNumberId = string.IsNullOrWhiteSpace(row.CheckNumber)
                    ? null
                    : AccountingLookup.GetOrCreateCheckNumber(connection, transaction, row.CheckNumber);
                bool needsReview = categoryId is null ||
                    (!string.IsNullOrWhiteSpace(row.Explanation) &&
                     row.Explanation.StartsWith("REVIEW:", StringComparison.OrdinalIgnoreCase));
                long timestampId = AccountingLookup.GetOrCreateTimestamp(
                    connection,
                    transaction,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                if (row.EntryId.HasValue)
                {
                    ExistingEntry existing = ReadExistingEntry(
                        connection,
                        transaction,
                        row.EntryId.Value,
                        row.SheetName,
                        row.RowNumber);

                    if (existing.AccountId != accountId || existing.OriginalYear != year)
                    {
                        throw new AccountingWorkbookValidationException(
                            row.SheetName,
                            row.RowNumber,
                            "Accounting Entry ID does not belong to this account and tax year.");
                    }

                    bool changed =
                        existing.DateId != dateId ||
                        existing.AmountId != moneyId ||
                        existing.DescriptionId != descriptionId ||
                        existing.ExplanationId != explanationId ||
                        existing.CategoryId != categoryId ||
                        existing.CheckNumberId != checkNumberId ||
                        existing.NeedsReview != needsReview;

                    if (changed)
                    {
                        using SqliteCommand update = connection.CreateCommand();
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE AccountingEntries
                            SET EntryDateId = $date,
                                AmountId = $amount,
                                DescriptionTextId = $description,
                                ExplanationTextId = $explanation,
                                CategoryId = $category,
                                CheckNumberId = $check,
                                NeedsReview = $review,
                                ModifiedTimestampId = $timestamp
                            WHERE Id = $id;
                            """;
                        AddEntryValueParameters(
                            update,
                            row.EntryId.Value,
                            dateId,
                            moneyId,
                            descriptionId,
                            explanationId,
                            categoryId,
                            checkNumberId,
                            needsReview,
                            timestampId);
                        update.ExecuteNonQuery();
                        updatedRows++;
                    }
                }
                else
                {
                    int displayOrder = ReadNextDisplayOrder(connection, transaction, accountId, year);
                    using SqliteCommand insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO AccountingEntries
                        (
                            AccountId, EntryDateId, AmountId, DescriptionTextId,
                            ExplanationTextId, CategoryId, CheckNumberId, DisplayOrder,
                            IsManual, NeedsReview, IsSuppressed,
                            CreatedTimestampId, ModifiedTimestampId
                        )
                        VALUES
                        (
                            $account, $date, $amount, $description,
                            $explanation, $category, $check, $order,
                            1, $review, 0, $timestamp, $timestamp
                        );
                        """;
                    insert.Parameters.AddWithValue("$account", accountId);
                    insert.Parameters.AddWithValue("$order", displayOrder);
                    AddEntryValueParameters(
                        insert,
                        null,
                        dateId,
                        moneyId,
                        descriptionId,
                        explanationId,
                        categoryId,
                        checkNumberId,
                        needsReview,
                        timestampId);
                    insert.ExecuteNonQuery();
                    insertedRows++;
                }
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        int unresolved = AccountingLedgerService.ReadEntries(year).Count(entry => entry.NeedsReview);
        return new AccountingWorkbookSyncResult(
            insertedRows,
            updatedRows,
            categoriesAdded,
            unresolved);
    }

    public static bool IsDatabaseBackedWorkbook(int year, string workbookPath)
    {
        try
        {
            using var workbook = new XLWorkbook(workbookPath);
            string sheetName = $"{year} Checking";
            IXLWorksheet? sheet = workbook.Worksheets.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, sheetName, StringComparison.OrdinalIgnoreCase));
            if (sheet is null)
                return false;

            // Accept both the new compact layout (ID in H) and the immediately
            // previous database-backed layout (ID in I) so saved edits can be
            // imported once before the workbook is regenerated in the new style.
            return string.Equals(
                       sheet.Cell(HeaderRow, EntryIdColumn).GetString().Trim(),
                       "Accounting Entry ID",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       sheet.Cell(HeaderRow, 9).GetString().Trim(),
                       "Accounting Entry ID",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new IOException(
                "The accounting workbook could not be read. Save it in Excel and close it if necessary, then try again.",
                exception);
        }
    }

    public static AccountingWorkbookSyncResult ImportLegacyWorkbookEdits(
        int year,
        string workbookPath)
    {
        List<LegacyWorkbookRow> rows = ReadLegacyWorkbookRows(year, workbookPath);
        using SqliteConnection connection = Database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        int updated = 0;
        int categoriesAdded = 0;

        try
        {
            foreach (LegacyWorkbookRow row in rows)
            {
                long entryId = FindEntryForSourceTransactions(
                    connection,
                    transaction,
                    row.TransactionIds,
                    row.SheetName,
                    row.RowNumber);
                ExistingEntry existing = ReadExistingEntry(
                    connection,
                    transaction,
                    entryId,
                    row.SheetName,
                    row.RowNumber);
                long expectedAccount = ReadAccountId(connection, transaction, row.AccountLast4);
                if (existing.AccountId != expectedAccount || existing.OriginalYear != year)
                {
                    throw new AccountingWorkbookValidationException(
                        row.SheetName,
                        row.RowNumber,
                        "Legacy source IDs resolve to an entry outside this account and tax year.");
                }

                long descriptionId = AccountingLookup.GetOrCreateText(
                    connection,
                    transaction,
                    "DESCRIPTION",
                    row.Description);
                long? explanationId = string.IsNullOrWhiteSpace(row.Explanation)
                    ? null
                    : AccountingLookup.GetOrCreateText(connection, transaction, "EXPLANATION", row.Explanation);
                long? categoryId = null;
                if (!string.IsNullOrWhiteSpace(row.Category))
                {
                    categoryId = AccountingLookup.GetOrCreateCategory(
                        connection,
                        transaction,
                        row.Category,
                        out bool categoryInserted);
                    if (categoryInserted)
                        categoriesAdded++;
                }

                bool needsReview = categoryId is null ||
                    row.Explanation.StartsWith("REVIEW:", StringComparison.OrdinalIgnoreCase);
                if (existing.DescriptionId == descriptionId &&
                    existing.ExplanationId == explanationId &&
                    existing.CategoryId == categoryId &&
                    existing.NeedsReview == needsReview)
                {
                    continue;
                }

                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE AccountingEntries
                    SET DescriptionTextId = $description,
                        ExplanationTextId = $explanation,
                        CategoryId = $category,
                        NeedsReview = $review,
                        ModifiedTimestampId = $timestamp
                    WHERE Id = $id;
                    """;
                update.Parameters.AddWithValue("$description", descriptionId);
                update.Parameters.AddWithValue("$explanation", (object?)explanationId ?? DBNull.Value);
                update.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
                update.Parameters.AddWithValue("$review", needsReview ? 1 : 0);
                update.Parameters.AddWithValue(
                    "$timestamp",
                    AccountingLookup.GetOrCreateTimestamp(
                        connection,
                        transaction,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                update.Parameters.AddWithValue("$id", entryId);
                update.ExecuteNonQuery();
                updated++;
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        int unresolved = AccountingLedgerService.ReadEntries(year).Count(entry => entry.NeedsReview);
        return new AccountingWorkbookSyncResult(0, updated, categoriesAdded, unresolved);
    }

    public static void OpenWorkbook(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static List<LegacyWorkbookRow> ReadLegacyWorkbookRows(
        int year,
        string workbookPath)
    {
        using var workbook = new XLWorkbook(workbookPath);
        var result = new List<LegacyWorkbookRow>();

        foreach (var account in Accounts)
        {
            string sheetName = $"{year} {account.SheetSuffix}";
            IXLWorksheet? ws = workbook.Worksheets.FirstOrDefault(sheet =>
                string.Equals(sheet.Name, sheetName, StringComparison.OrdinalIgnoreCase));
            if (ws is null)
                continue;

            int sourceColumn = FindHeaderColumn(ws, "Source Transaction IDs");
            int descriptionColumn = FindHeaderColumn(ws, "Description");
            int explanationColumn = FindHeaderColumn(ws, "Explanation");
            if (sourceColumn < 1 || descriptionColumn < 1)
                continue;

            var categoryColumns = new Dictionary<int, string>();
            int lastColumn = ws.LastColumnUsed()?.ColumnNumber() ?? sourceColumn;
            for (int column = 1; column <= lastColumn; column++)
            {
                string header = ws.Cell(HeaderRow, column).GetString().Trim();
                if (string.IsNullOrWhiteSpace(header) ||
                    header is "Date" or "Description" or "Chk #" or "Dr." or "Cr." or
                    "Charge" or "Payment" or "Balance" or "Explanation" or "Source Transaction IDs")
                {
                    continue;
                }
                categoryColumns[column] = header;
            }

            int lastRow = ws.LastRowUsed()?.RowNumber() ?? HeaderRow;
            for (int rowNumber = FirstDataRow; rowNumber <= lastRow; rowNumber++)
            {
                string sourceText = ws.Cell(rowNumber, sourceColumn).GetString().Trim();
                if (string.IsNullOrWhiteSpace(sourceText))
                    continue;

                List<long> transactionIds = [];
                foreach (string part in sourceText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
                    {
                        throw new AccountingWorkbookValidationException(
                            sheetName,
                            rowNumber,
                            "Legacy Source Transaction IDs are malformed.");
                    }
                    transactionIds.Add(id);
                }

                List<string> populatedCategories = [];
                foreach ((int column, string category) in categoryColumns)
                {
                    if (ws.Cell(rowNumber, column).TryGetValue<decimal>(out decimal amount) && amount != 0)
                        populatedCategories.Add(category);
                }
                if (populatedCategories.Count > 1)
                {
                    throw new AccountingWorkbookValidationException(
                        sheetName,
                        rowNumber,
                        "The legacy row assigns money to multiple categories; the compact ledger requires one category per row.");
                }

                string description = ws.Cell(rowNumber, descriptionColumn).GetString().Trim();
                if (string.IsNullOrWhiteSpace(description))
                    throw new AccountingWorkbookValidationException(sheetName, rowNumber, "Description is required.");
                result.Add(new LegacyWorkbookRow(
                    sheetName,
                    rowNumber,
                    account.Last4,
                    transactionIds,
                    description,
                    explanationColumn > 0 ? ws.Cell(rowNumber, explanationColumn).GetString().Trim() : string.Empty,
                    populatedCategories.SingleOrDefault()));
            }
        }

        return result;
    }

    private static int FindHeaderColumn(IXLWorksheet ws, string header)
    {
        IXLCell? cell = ws.Row(HeaderRow).CellsUsed().FirstOrDefault(candidate =>
            string.Equals(candidate.GetString().Trim(), header, StringComparison.OrdinalIgnoreCase));
        return cell?.Address.ColumnNumber ?? -1;
    }

    private static long FindEntryForSourceTransactions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> transactionIds,
        string sheet,
        int row)
    {
        if (transactionIds.Count == 0)
            throw new AccountingWorkbookValidationException(sheet, row, "No source transaction IDs were supplied.");

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameterNames = new List<string>();
        for (int i = 0; i < transactionIds.Count; i++)
        {
            string name = $"$source{i}";
            parameterNames.Add(name);
            command.Parameters.AddWithValue(name, transactionIds[i]);
        }
        command.CommandText =
            $"SELECT DISTINCT AccountingEntryId FROM AccountingEntryTransactions " +
            $"WHERE TransactionId IN ({string.Join(",", parameterNames)});";
        var ids = new List<long>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        if (ids.Count != 1)
            throw new AccountingWorkbookValidationException(sheet, row, "Legacy source IDs do not resolve to exactly one accounting entry.");
        return ids[0];
    }

    private static IXLWorksheet BuildHelperSheet(
        XLWorkbook workbook,
        IReadOnlyList<AccountingCategory> categories)
    {
        IXLWorksheet ws = workbook.Worksheets.Add("_SoloPractice");
        ws.Cell(1, 1).Value = "Accounting Categories";
        for (int i = 0; i < categories.Count; i++)
            ws.Cell(i + 2, 1).Value = categories[i].Name;

        if (categories.Count > 0)
            workbook.DefinedNames.Add("AccountingCategoryList", ws.Range(2, 1, categories.Count + 1, 1));

        return ws;
    }


    private static void BuildAccountSheet(
        XLWorkbook workbook,
        IXLWorksheet helper,
        int year,
        string accountLast4,
        string sheetSuffix,
        string title,
        bool isCard,
        AccountingEntry openingEntry,
        IReadOnlyList<AccountingEntry> entries,
        IReadOnlyList<AccountingCategory> categories,
        IReadOnlyDictionary<(string AccountLast4, string Category), long> totals)
    {
        IXLWorksheet ws = workbook.Worksheets.Add($"{year} {sheetSuffix}");

        // Compact title band above the ledger.
        ws.Range(1, 1, 1, 2).Merge();
        ws.Cell(1, 1).Value = $"Tax Year: {year}";
        ws.Range(1, 3, 1, 7).Merge();
        ws.Cell(1, 3).Value = title;

        string[] headers =
        [
            "Date",
            "Description",
            "Chk #",
            "Amount",
            "Balance",
            "Category",
            "Explanation",
            "Accounting Entry ID",
            "Source Transaction IDs"
        ];

        for (int i = 0; i < headers.Length; i++)
            ws.Cell(HeaderRow, i + 1).Value = headers[i];

        int rowNumber = FirstDataRow;

        // The opening balance is kept in the same ledger, but it has no activity amount.
        ws.Cell(rowNumber, 1).Value = new DateTime(year, 1, 1);
        ws.Cell(rowNumber, 2).Value = "Balance forward";
        ws.Cell(rowNumber, 5).Value = CentsToDecimal(openingEntry.AmountCents);
        ws.Cell(rowNumber, EntryIdColumn).Value = openingEntry.Id;
        rowNumber++;

        var reviewRows = new List<int>();

        foreach (AccountingEntry entry in entries)
        {
            ws.Cell(rowNumber, 1).Value = entry.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(rowNumber, 2).Value = entry.Description;

            if (!string.IsNullOrWhiteSpace(entry.CheckNumber))
                ws.Cell(rowNumber, 3).Value = entry.CheckNumber;

            // Store the database's signed amount directly:
            // negative = debit/charge, positive = credit/payment.
            ws.Cell(rowNumber, 4).Value = CentsToDecimal(entry.AmountCents);
            ws.Cell(rowNumber, 5).FormulaA1 = $"E{rowNumber - 1}+D{rowNumber}";

            if (!string.IsNullOrWhiteSpace(entry.Category))
                ws.Cell(rowNumber, 6).Value = entry.Category;

            if (!string.IsNullOrWhiteSpace(entry.Explanation))
                ws.Cell(rowNumber, 7).Value = entry.Explanation;

            ws.Cell(rowNumber, EntryIdColumn).Value = entry.Id;
            ws.Cell(rowNumber, SourceIdsColumn).Value = entry.SourceTransactionIds;

            if (entry.NeedsReview)
                reviewRows.Add(rowNumber);

            rowNumber++;
        }

        int lastDataRow = Math.Max(FirstDataRow, rowNumber - 1);

        if (categories.Count > 0)
        {
            IXLDataValidation validation = ws.Range(FirstDataRow + 1, 6, 10000, 6)
                .CreateDataValidation();
            validation.List("AccountingCategoryList");
            validation.IgnoreBlanks = true;
            validation.ShowErrorMessage = false;
            validation.InCellDropdown = true;
        }

        // Put the category summary below the ledger rather than widening the sheet.
        int summaryHeaderRow = lastDataRow + 3;
        BuildCategorySummary(
            ws,
            summaryHeaderRow,
            accountLast4,
            entries,
            categories,
            totals);

        // Hidden marker gives the round-trip parser a reliable place to stop.
        ws.Cell(summaryHeaderRow, SourceIdsColumn).Value = SummaryMarker;

        ApplyAccountFormatting(
            ws,
            year,
            accountLast4,
            lastDataRow,
            summaryHeaderRow,
            reviewRows);
    }

    private static void BuildCategorySummary(
        IXLWorksheet ws,
        int headerRow,
        string accountLast4,
        IReadOnlyList<AccountingEntry> entries,
        IReadOnlyList<AccountingCategory> categories,
        IReadOnlyDictionary<(string AccountLast4, string Category), long> totals)
    {
        ws.Cell(headerRow, 2).Value = "Accounting Category";
        ws.Cell(headerRow, 3).Value = "Total";

        List<AccountingCategory> debitCategories = categories
            .Where(category => string.Equals(
                category.NormalSide,
                "DEBIT",
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<AccountingCategory> creditCategories = categories
            .Where(category => string.Equals(
                category.NormalSide,
                "CREDIT",
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A user-created category normally learns its side during sync. If an older
        // database still contains a category with no side, place it based on actual
        // activity for this account. With no activity, keep it in Dr. rather than
        // hiding it; the summary must show every active category.
        foreach (AccountingCategory category in categories.Where(category =>
                     !string.Equals(category.NormalSide, "DEBIT", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(category.NormalSide, "CREDIT", StringComparison.OrdinalIgnoreCase)))
        {
            long signedActivity = entries
                .Where(entry => string.Equals(
                    entry.Category,
                    category.Name,
                    StringComparison.OrdinalIgnoreCase))
                .Sum(entry => entry.AmountCents);

            if (signedActivity > 0)
                creditCategories.Add(category);
            else
                debitCategories.Add(category);
        }

        int row = headerRow + 1;

        long debitActivityCents = entries
            .Where(entry => entry.AmountCents < 0)
            .Sum(entry => Math.Abs(entry.AmountCents));

        long creditActivityCents = entries
            .Where(entry => entry.AmountCents > 0)
            .Sum(entry => entry.AmountCents);

        row = WriteCategorySummaryGroup(
            ws,
            row,
            "Dr.",
            debitCategories,
            accountLast4,
            totals,
            debitActivityCents,
            XLColor.FromHtml("#C0504D"),
            XLColor.FromHtml("#F2DCDB"),
            XLColor.FromHtml("#E6B8B7"));

        row = WriteCategorySummaryGroup(
            ws,
            row,
            "Cr.",
            creditCategories,
            accountLast4,
            totals,
            creditActivityCents,
            XLColor.FromHtml("#4F81BD"),
            XLColor.FromHtml("#DCE6F1"),
            XLColor.FromHtml("#B8CCE4"));

        ws.Range(headerRow, 2, headerRow, 3).Style.Font.Bold = true;
        ws.Range(headerRow, 2, headerRow, 3).Style.Fill.BackgroundColor =
            XLColor.FromHtml("#F2F2F2");
        ws.Range(headerRow, 2, headerRow, 3).Style.Border.BottomBorder =
            XLBorderStyleValues.Medium;
        ws.Range(headerRow, 2, headerRow, 3).Style.Alignment.Horizontal =
            XLAlignmentHorizontalValues.Left;

        ws.Column(1).Width = 8;
        ws.Column(2).Width = Math.Max(ws.Column(2).Width, 38);
        ws.Column(3).Width = Math.Max(ws.Column(3).Width, 16);
    }

    private static int WriteCategorySummaryGroup(
        IXLWorksheet ws,
        int startRow,
        string sideLabel,
        IReadOnlyList<AccountingCategory> categories,
        string accountLast4,
        IReadOnlyDictionary<(string AccountLast4, string Category), long> totals,
        long activityTotalCents,
        XLColor strongColor,
        XLColor lightFill,
        XLColor alternateFill)
    {
        int firstRow = startRow;
        int row = startRow;

        foreach (AccountingCategory category in categories)
        {
            ws.Cell(row, 2).Value = category.Name;

            long cents = totals.TryGetValue(
                (accountLast4, category.Name),
                out long categoryCents)
                    ? categoryCents
                    : 0;

            ws.Cell(row, 3).Value = CentsToDecimal(cents);

            XLColor fill = ((row - firstRow) & 1) == 0
                ? lightFill
                : alternateFill;

            ws.Range(row, 2, row, 3).Style.Fill.BackgroundColor = fill;
            row++;
        }

        int totalRow = row;
        ws.Cell(totalRow, 2).Value = $"Total ({sideLabel.TrimEnd('.')})";
        ws.Cell(totalRow, 3).Value = CentsToDecimal(activityTotalCents);

        ws.Range(totalRow, 2, totalRow, 3).Style.Fill.BackgroundColor = strongColor;
        ws.Range(totalRow, 2, totalRow, 3).Style.Font.FontColor = XLColor.White;
        ws.Range(totalRow, 2, totalRow, 3).Style.Font.Bold = true;
        ws.Range(totalRow, 2, totalRow, 3).Style.Border.TopBorder =
            XLBorderStyleValues.Medium;

        // One solid side label, matching the requested Dr./Cr. block.
        ws.Range(firstRow, 1, totalRow, 1).Merge();
        ws.Cell(firstRow, 1).Value = sideLabel;
        ws.Cell(firstRow, 1).Style.Fill.BackgroundColor = strongColor;
        ws.Cell(firstRow, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(firstRow, 1).Style.Font.Bold = true;
        ws.Cell(firstRow, 1).Style.Font.FontSize = 12;
        ws.Cell(firstRow, 1).Style.Alignment.Horizontal =
            XLAlignmentHorizontalValues.Center;
        ws.Cell(firstRow, 1).Style.Alignment.Vertical =
            XLAlignmentVerticalValues.Center;

        // Strong outer frame, a clear divider after the Dr./Cr. block, and
        // restrained row separators through only Category/Total.
        ws.Range(firstRow, 1, totalRow, 3).Style.Border.OutsideBorder =
            XLBorderStyleValues.Medium;
        ws.Range(firstRow, 1, totalRow, 1).Style.Border.RightBorder =
            XLBorderStyleValues.Medium;
        ws.Range(firstRow, 2, totalRow, 2).Style.Border.RightBorder =
            XLBorderStyleValues.Thin;

        for (int separatorRow = firstRow; separatorRow < totalRow; separatorRow++)
        {
            ws.Range(separatorRow, 2, separatorRow, 3).Style.Border.BottomBorder =
                XLBorderStyleValues.Thin;
        }

        ws.Range(firstRow, 3, totalRow, 3).Style.NumberFormat.Format =
            "$#,##0.00;[Red]-$#,##0.00";

        return totalRow + 1;
    }

    private static void ApplyAccountFormatting(
        IXLWorksheet ws,
        int year,
        string accountLast4,
        int lastDataRow,
        int summaryHeaderRow,
        IReadOnlyList<int> reviewRows)
    {
        ws.SheetView.FreezeRows(HeaderRow);
        ws.SheetView.FreezeColumns(2);

        // Title band.
        ws.Range(1, 1, 1, 7).Style.Fill.BackgroundColor =
            XLColor.FromHtml("#D9EAF7");
        ws.Range(1, 1, 1, 7).Style.Font.Bold = true;
        ws.Range(1, 1, 1, 7).Style.Font.FontSize = 12;
        ws.Range(1, 1, 1, 7).Style.Alignment.Vertical =
            XLAlignmentVerticalValues.Center;
        ws.Cell(1, 3).Style.Alignment.Horizontal =
            XLAlignmentHorizontalValues.Center;
        ws.Row(1).Height = 22;

        // Use an Excel table for filtering/banding, then add a slightly stronger
        // frame so the ledger reads as one intentional block.
        IXLTable table = ws.Range(HeaderRow, 1, lastDataRow, 7)
            .CreateTable($"AccountingEntries_{accountLast4}_{year}");
        table.Theme = XLTableTheme.TableStyleMedium2;
        table.ShowAutoFilter = true;
        table.ShowRowStripes = true;

        ApplyGridBorders(
            ws,
            HeaderRow,
            1,
            lastDataRow,
            7,
            XLBorderStyleValues.Medium,
            XLBorderStyleValues.Thin);

        ws.Range(FirstDataRow, 1, lastDataRow, 1).Style.NumberFormat.Format =
            "mm/dd/yyyy";

        // Signed amount and balance: negatives display red.
        ws.Range(FirstDataRow, 4, lastDataRow, 5).Style.NumberFormat.Format =
            "$#,##0.00;[Red]-$#,##0.00";

        ws.Column(1).Width = 12;
        ws.Column(2).Width = 52;
        ws.Column(3).Width = 11;
        ws.Column(4).Width = 15;
        ws.Column(5).Width = 15;
        ws.Column(6).Width = 30;
        ws.Column(7).Width = 50;

        ws.Column(EntryIdColumn).Hide();
        ws.Column(SourceIdsColumn).Hide();

        ws.Range(FirstDataRow, 2, lastDataRow, 2).Style.Alignment.WrapText = true;
        ws.Range(FirstDataRow, 7, lastDataRow, 7).Style.Alignment.WrapText = true;
        ws.Range(HeaderRow, 1, lastDataRow, 7).Style.Alignment.Vertical =
            XLAlignmentVerticalValues.Center;

        // Preserve the yellow "needs review" cue on top of the table styling.
        foreach (int reviewRow in reviewRows)
        {
            ws.Range(reviewRow, 2, reviewRow, 7).Style.Fill.BackgroundColor =
                XLColor.FromHtml("#FFF2CC");
        }

        // A little breathing room before the summary.
        ws.Row(summaryHeaderRow - 2).Height = 8;
        ws.Row(summaryHeaderRow - 1).Height = 8;

        // Give the category-summary header the same framed feel as the ledger.
        ws.Range(summaryHeaderRow, 2, summaryHeaderRow, 3).Style.Border.TopBorder =
            XLBorderStyleValues.Medium;
        ws.Range(summaryHeaderRow, 2, summaryHeaderRow, 3).Style.Border.BottomBorder =
            XLBorderStyleValues.Medium;
        ws.Range(summaryHeaderRow, 2, summaryHeaderRow, 2).Style.Border.LeftBorder =
            XLBorderStyleValues.Medium;
        ws.Range(summaryHeaderRow, 3, summaryHeaderRow, 3).Style.Border.RightBorder =
            XLBorderStyleValues.Medium;
        ws.Range(summaryHeaderRow, 2, summaryHeaderRow, 2).Style.Border.RightBorder =
            XLBorderStyleValues.Thin;
    }

    private static void ApplyGridBorders(
        IXLWorksheet ws,
        int firstRow,
        int firstColumn,
        int lastRow,
        int lastColumn,
        XLBorderStyleValues outerBorder,
        XLBorderStyleValues innerBorder)
    {
        if (lastRow < firstRow || lastColumn < firstColumn)
            return;

        ws.Range(firstRow, firstColumn, lastRow, lastColumn)
            .Style.Border.OutsideBorder = outerBorder;

        for (int row = firstRow; row < lastRow; row++)
        {
            ws.Range(row, firstColumn, row, lastColumn)
                .Style.Border.BottomBorder = innerBorder;
        }

        for (int column = firstColumn; column < lastColumn; column++)
        {
            ws.Range(firstRow, column, lastRow, column)
                .Style.Border.RightBorder = innerBorder;
        }
    }

    private static void BuildTrialBalances(
        XLWorkbook workbook,
        int year,
        IReadOnlyList<AccountingEntry> entries,
        IReadOnlyList<AccountingCategory> categories)
    {
        IXLWorksheet ws = workbook.Worksheets.Add($"{year} Trial Balances");

        XLColor titleColor = XLColor.FromHtml("#1F4E78");
        XLColor subtitleColor = XLColor.FromHtml("#D9EAF7");
        XLColor debitStrong = XLColor.FromHtml("#C0504D");
        XLColor debitLight = XLColor.FromHtml("#F2DCDB");
        XLColor creditStrong = XLColor.FromHtml("#4F81BD");
        XLColor creditLight = XLColor.FromHtml("#DCE6F1");
        XLColor neutralLight = XLColor.FromHtml("#F2F2F2");

        // Workbook title.
        ws.Range(1, 1, 1, 9).Merge();
        ws.Cell(1, 1).Value = "Yolanda Solecki LCPC LLC";
        ws.Range(1, 1, 1, 9).Style.Fill.BackgroundColor = titleColor;
        ws.Range(1, 1, 1, 9).Style.Font.FontColor = XLColor.White;
        ws.Range(1, 1, 1, 9).Style.Font.Bold = true;
        ws.Range(1, 1, 1, 9).Style.Font.FontSize = 16;
        ws.Range(1, 1, 1, 9).Style.Alignment.Horizontal =
            XLAlignmentHorizontalValues.Center;
        ws.Range(1, 1, 1, 9).Style.Alignment.Vertical =
            XLAlignmentVerticalValues.Center;
        ws.Row(1).Height = 28;

        ws.Range(2, 1, 2, 9).Merge();
        ws.Cell(2, 1).Value =
            $"Tax Year {year} • Year-to-Date Combined Trial Balance • Database Calculated";
        ws.Range(2, 1, 2, 9).Style.Fill.BackgroundColor = subtitleColor;
        ws.Range(2, 1, 2, 9).Style.Font.Bold = true;
        ws.Range(2, 1, 2, 9).Style.Alignment.Horizontal =
            XLAlignmentHorizontalValues.Center;
        ws.Row(2).Height = 21;

        // Account-balance overview.
        const int accountHeaderRow = 4;
        ws.Cell(accountHeaderRow, 1).Value = "Account";
        ws.Cell(accountHeaderRow, 2).Value = "Opening Balance";
        ws.Cell(accountHeaderRow, 3).Value = "Year Activity";
        ws.Cell(accountHeaderRow, 4).Value = "Ending Balance";

        ws.Range(accountHeaderRow, 1, accountHeaderRow, 4)
            .Style.Fill.BackgroundColor = titleColor;
        ws.Range(accountHeaderRow, 1, accountHeaderRow, 4)
            .Style.Font.FontColor = XLColor.White;
        ws.Range(accountHeaderRow, 1, accountHeaderRow, 4).Style.Font.Bold = true;
        ws.Range(accountHeaderRow, 1, accountHeaderRow, 4)
            .Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        int accountRow = accountHeaderRow + 1;
        foreach (var account in Accounts)
        {
            long activity = entries
                .Where(entry =>
                    entry.AccountLast4 == account.Last4 &&
                    !entry.IsOpeningBalance)
                .Sum(entry => entry.AmountCents);

            long opening = entries.Single(entry =>
                entry.AccountLast4 == account.Last4 &&
                entry.IsOpeningBalance).AmountCents;

            ws.Cell(accountRow, 1).Value = account.Title;
            ws.Cell(accountRow, 2).Value = CentsToDecimal(opening);
            ws.Cell(accountRow, 3).Value = CentsToDecimal(activity);
            ws.Cell(accountRow, 4).Value = CentsToDecimal(opening + activity);

            if (((accountRow - accountHeaderRow) & 1) == 0)
            {
                ws.Range(accountRow, 1, accountRow, 4)
                    .Style.Fill.BackgroundColor = neutralLight;
            }

            accountRow++;
        }

        int accountLastRow = accountRow - 1;
        ApplyGridBorders(
            ws,
            accountHeaderRow,
            1,
            accountLastRow,
            4,
            XLBorderStyleValues.Medium,
            XLBorderStyleValues.Thin);

        ws.Range(accountHeaderRow + 1, 2, accountLastRow, 4)
            .Style.NumberFormat.Format = "$#,##0.00;[Red]-$#,##0.00";
        ws.Range(accountHeaderRow + 1, 1, accountLastRow, 1).Style.Font.Bold = true;

        // Category trial balance.
        const int tableRow = 10;
        string[] headers =
        [
            "Accounting Category",
            "Checking Dr.", "Checking Cr.",
            "Savings Dr.", "Savings Cr.",
            "Chase Visa Dr.", "Chase Visa Cr.",
            "Combined Dr.", "Combined Cr."
        ];

        for (int i = 0; i < headers.Length; i++)
            ws.Cell(tableRow, i + 1).Value = headers[i];

        ws.Cell(tableRow, 1).Style.Fill.BackgroundColor = titleColor;
        ws.Cell(tableRow, 1).Style.Font.FontColor = XLColor.White;

        int[] debitColumns = [2, 4, 6, 8];
        int[] creditColumns = [3, 5, 7, 9];

        foreach (int column in debitColumns)
        {
            ws.Cell(tableRow, column).Style.Fill.BackgroundColor = debitStrong;
            ws.Cell(tableRow, column).Style.Font.FontColor = XLColor.White;
        }

        foreach (int column in creditColumns)
        {
            ws.Cell(tableRow, column).Style.Fill.BackgroundColor = creditStrong;
            ws.Cell(tableRow, column).Style.Font.FontColor = XLColor.White;
        }

        ws.Range(tableRow, 1, tableRow, 9).Style.Font.Bold = true;
        ws.Range(tableRow, 1, tableRow, 9)
            .Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Range(tableRow, 1, tableRow, 9)
            .Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Range(tableRow, 1, tableRow, 9).Style.Alignment.WrapText = true;
        ws.Row(tableRow).Height = 30;

        int rowNumber = tableRow + 1;

        // Show every active category, including zero-dollar categories, so the
        // trial balance has the same stable category vocabulary as account sheets.
        foreach (AccountingCategory category in categories)
        {
            List<AccountingEntry> categoryEntries = entries
                .Where(entry =>
                    !entry.IsOpeningBalance &&
                    string.Equals(
                        entry.Category,
                        category.Name,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            ws.Cell(rowNumber, 1).Value = category.Name;

            decimal combinedDebit = 0;
            decimal combinedCredit = 0;

            for (int accountIndex = 0; accountIndex < Accounts.Length; accountIndex++)
            {
                List<AccountingEntry> accountEntries = categoryEntries
                    .Where(entry =>
                        entry.AccountLast4 == Accounts[accountIndex].Last4)
                    .ToList();

                long debit = 0;
                long credit = 0;

                foreach (AccountingEntry entry in accountEntries)
                {
                    bool isDebit = category.NormalSide switch
                    {
                        "DEBIT" => true,
                        "CREDIT" => false,
                        _ => entry.AmountCents < 0
                    };

                    if (isDebit)
                        debit += Math.Abs(entry.AmountCents);
                    else
                        credit += Math.Abs(entry.AmountCents);
                }

                decimal debitValue = CentsToDecimal(debit);
                decimal creditValue = CentsToDecimal(credit);

                ws.Cell(rowNumber, 2 + accountIndex * 2).Value = debitValue;
                ws.Cell(rowNumber, 3 + accountIndex * 2).Value = creditValue;

                combinedDebit += debitValue;
                combinedCredit += creditValue;
            }

            ws.Cell(rowNumber, 8).Value = combinedDebit;
            ws.Cell(rowNumber, 9).Value = combinedCredit;

            if (((rowNumber - (tableRow + 1)) & 1) == 1)
            {
                ws.Cell(rowNumber, 1).Style.Fill.BackgroundColor = neutralLight;
            }

            foreach (int column in debitColumns)
                ws.Cell(rowNumber, column).Style.Fill.BackgroundColor = debitLight;

            foreach (int column in creditColumns)
                ws.Cell(rowNumber, column).Style.Fill.BackgroundColor = creditLight;

            rowNumber++;
        }

        int totalRow = rowNumber;
        ws.Cell(totalRow, 1).Value = "Totals";

        for (int column = 2; column <= 9; column++)
        {
            decimal total = categories.Count == 0
                ? 0
                : ws.Range(tableRow + 1, column, totalRow - 1, column)
                    .Cells()
                    .Sum(cell => cell.GetValue<decimal>());

            ws.Cell(totalRow, column).Value = total;
        }

        ws.Cell(totalRow, 1).Style.Fill.BackgroundColor = titleColor;
        ws.Cell(totalRow, 1).Style.Font.FontColor = XLColor.White;
        ws.Cell(totalRow, 1).Style.Font.Bold = true;

        foreach (int column in debitColumns)
        {
            ws.Cell(totalRow, column).Style.Fill.BackgroundColor = debitStrong;
            ws.Cell(totalRow, column).Style.Font.FontColor = XLColor.White;
            ws.Cell(totalRow, column).Style.Font.Bold = true;
        }

        foreach (int column in creditColumns)
        {
            ws.Cell(totalRow, column).Style.Fill.BackgroundColor = creditStrong;
            ws.Cell(totalRow, column).Style.Font.FontColor = XLColor.White;
            ws.Cell(totalRow, column).Style.Font.Bold = true;
        }

        ApplyGridBorders(
            ws,
            tableRow,
            1,
            totalRow,
            9,
            XLBorderStyleValues.Medium,
            XLBorderStyleValues.Thin);

        // Strong dividers make each account's Dr./Cr. pair visually distinct.
        foreach (int column in new[] { 1, 3, 5, 7 })
        {
            ws.Range(tableRow, column, totalRow, column)
                .Style.Border.RightBorder = XLBorderStyleValues.Medium;
        }

        ws.Range(tableRow + 1, 2, totalRow, 9)
            .Style.NumberFormat.Format = "$#,##0.00;[Red]-$#,##0.00";

        ws.Column(1).Width = 42;
        ws.Columns(2, 9).Width = 16;
        ws.Range(tableRow + 1, 1, totalRow, 1).Style.Alignment.WrapText = true;

        // Category filtering remains useful on a long trial balance.
        if (totalRow > tableRow + 1)
            ws.Range(tableRow, 1, totalRow - 1, 9).SetAutoFilter();

        ws.SheetView.FreezeRows(tableRow);
        ws.SheetView.FreezeColumns(1);
    }


    private static List<WorkbookRow> ReadWorkbookRows(int year, string workbookPath)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(workbookPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new IOException(
                "The accounting workbook could not be read. Save it in Excel and close it if necessary, then try syncing again.",
                exception);
        }

        using (workbook)
        {
            var result = new List<WorkbookRow>();

            foreach (var account in Accounts)
            {
                string sheetName = $"{year} {account.SheetSuffix}";
                IXLWorksheet? ws = workbook.Worksheets.FirstOrDefault(sheet =>
                    string.Equals(
                        sheet.Name,
                        sheetName,
                        StringComparison.OrdinalIgnoreCase));

                if (ws is null)
                    throw new InvalidDataException(
                        $"Workbook is missing required sheet '{sheetName}'.");

                bool compactLayout = IsCompactAmountLayout(ws);

                int entryIdColumn = compactLayout ? EntryIdColumn : 9;
                int sourceIdsColumn = compactLayout ? SourceIdsColumn : 10;
                int categoryColumn = compactLayout ? 6 : 7;
                int explanationColumn = compactLayout ? 7 : 8;

                ValidateHeaders(ws, sheetName, account.IsCard, compactLayout);

                int lastRow = ws.LastRowUsed()?.RowNumber() ?? HeaderRow;

                for (int rowNumber = FirstDataRow; rowNumber <= lastRow; rowNumber++)
                {
                    if (compactLayout &&
                        string.Equals(
                            ws.Cell(rowNumber, sourceIdsColumn).GetString().Trim(),
                            SummaryMarker,
                            StringComparison.Ordinal))
                    {
                        break;
                    }

                    string description = ws.Cell(rowNumber, 2).GetString().Trim();

                    // The old database-backed workbook had a standalone totals row.
                    // It is intentionally removed in the new compact layout.
                    if (description.Equals(
                            "Balance forward",
                            StringComparison.OrdinalIgnoreCase) ||
                        description.Equals(
                            "Totals:",
                            StringComparison.OrdinalIgnoreCase) ||
                        ws.Cell(rowNumber, 1).GetString().Trim().Equals(
                            "Totals:",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long? entryId = ParseOptionalEntryId(
                        ws.Cell(rowNumber, entryIdColumn),
                        sheetName,
                        rowNumber);

                    string checkNumber = ws.Cell(rowNumber, 3).GetString().Trim();
                    string category = ws.Cell(rowNumber, categoryColumn).GetString().Trim();
                    string explanation = ws.Cell(rowNumber, explanationColumn).GetString().Trim();

                    decimal signedAmount;

                    if (compactLayout)
                    {
                        signedAmount = ParseOptionalMoney(
                            ws.Cell(rowNumber, 4),
                            sheetName,
                            rowNumber,
                            "Amount");
                    }
                    else
                    {
                        decimal debit = ParseOptionalMoney(
                            ws.Cell(rowNumber, 4),
                            sheetName,
                            rowNumber,
                            account.IsCard ? "Charge" : "Dr.");

                        decimal credit = ParseOptionalMoney(
                            ws.Cell(rowNumber, 5),
                            sheetName,
                            rowNumber,
                            account.IsCard ? "Payment" : "Cr.");

                        if (debit != 0 && credit != 0)
                        {
                            throw new AccountingWorkbookValidationException(
                                sheetName,
                                rowNumber,
                                "Both debit/charge and credit/payment are nonzero.");
                        }

                        if (debit < 0 || credit < 0)
                        {
                            throw new AccountingWorkbookValidationException(
                                sheetName,
                                rowNumber,
                                "Debit/credit amount columns must contain nonnegative values.");
                        }

                        signedAmount = debit != 0 ? -debit : credit;
                    }

                    bool meaningful =
                        entryId.HasValue ||
                        !string.IsNullOrWhiteSpace(description) ||
                        !string.IsNullOrWhiteSpace(checkNumber) ||
                        !string.IsNullOrWhiteSpace(category) ||
                        !string.IsNullOrWhiteSpace(explanation) ||
                        signedAmount != 0 ||
                        !ws.Cell(rowNumber, 1).IsEmpty();

                    if (!meaningful)
                        continue;

                    if (signedAmount == 0)
                    {
                        throw new AccountingWorkbookValidationException(
                            sheetName,
                            rowNumber,
                            compactLayout
                                ? "Amount is required and cannot be zero."
                                : "One debit/credit amount is required.");
                    }

                    if (string.IsNullOrWhiteSpace(description))
                    {
                        throw new AccountingWorkbookValidationException(
                            sheetName,
                            rowNumber,
                            "Description is required.");
                    }

                    DateOnly date = ParseDate(
                        ws.Cell(rowNumber, 1),
                        sheetName,
                        rowNumber);

                    if (date.Year != year)
                    {
                        throw new AccountingWorkbookValidationException(
                            sheetName,
                            rowNumber,
                            $"Date must be in tax year {year}.");
                    }

                    long amountCents = DecimalToCents(
                        signedAmount,
                        sheetName,
                        rowNumber);

                    result.Add(new WorkbookRow(
                        sheetName,
                        rowNumber,
                        account.Last4,
                        entryId,
                        date,
                        description,
                        checkNumber,
                        amountCents,
                        category,
                        explanation));
                }
            }

            return result;
        }
    }

    private static bool IsCompactAmountLayout(IXLWorksheet ws)
    {
        return string.Equals(
            ws.Cell(HeaderRow, 4).GetString().Trim(),
            "Amount",
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateHeaders(
        IXLWorksheet ws,
        string sheetName,
        bool isCard,
        bool compactLayout)
    {
        string[] expected = compactLayout
            ?
            [
                "Date",
                "Description",
                "Chk #",
                "Amount",
                "Balance",
                "Category",
                "Explanation",
                "Accounting Entry ID"
            ]
            :
            [
                "Date",
                "Description",
                "Chk #",
                isCard ? "Charge" : "Dr.",
                isCard ? "Payment" : "Cr.",
                "Balance",
                "Category",
                "Explanation",
                "Accounting Entry ID"
            ];

        for (int i = 0; i < expected.Length; i++)
        {
            if (!string.Equals(
                    ws.Cell(HeaderRow, i + 1).GetString().Trim(),
                    expected[i],
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Sheet '{sheetName}' has an unexpected or missing '{expected[i]}' column.");
            }
        }
    }

    private static long? ParseOptionalEntryId(IXLCell cell, string sheet, int row)
    {
        if (cell.IsEmpty())
            return null;
        if (cell.TryGetValue<long>(out long value) && value > 0)
            return value;
        throw new AccountingWorkbookValidationException(sheet, row, "Accounting Entry ID is invalid.");
    }

    private static decimal ParseOptionalMoney(IXLCell cell, string sheet, int row, string name)
    {
        if (cell.IsEmpty())
            return 0;
        if (cell.TryGetValue<decimal>(out decimal value))
            return value;
        throw new AccountingWorkbookValidationException(sheet, row, $"{name} is not a valid amount.");
    }

    private static DateOnly ParseDate(IXLCell cell, string sheet, int row)
    {
        if (cell.TryGetValue<DateTime>(out DateTime dateTime))
            return DateOnly.FromDateTime(dateTime);
        string text = cell.GetString().Trim();
        if (DateOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateOnly date) ||
            DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return date;
        throw new AccountingWorkbookValidationException(sheet, row, "Date is missing or malformed.");
    }

    private static long DecimalToCents(decimal value, string sheet, int row)
    {
        decimal cents = value * 100m;
        if (decimal.Truncate(cents) != cents)
            throw new AccountingWorkbookValidationException(sheet, row, "Amount has more than two decimal places.");
        return checked(decimal.ToInt64(cents));
    }

    private static void EnsureCategoryNormalSide(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long categoryId,
        string normalSide)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AccountingCategories
            SET NormalSide = $side
            WHERE Id = $id
              AND NormalSide IS NULL;
            """;
        command.Parameters.AddWithValue("$side", normalSide);
        command.Parameters.AddWithValue("$id", categoryId);
        command.ExecuteNonQuery();
    }

    private static long ReadAccountId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string last4)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id FROM Accounts WHERE Last4 = $last4;";
        command.Parameters.AddWithValue("$last4", last4);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static ExistingEntry ReadExistingEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        string sheet,
        int row)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.AccountId, e.EntryDateId, e.AmountId, e.DescriptionTextId,
                   e.ExplanationTextId, e.CategoryId, e.CheckNumberId,
                   e.NeedsReview,
                   CAST(strftime('%Y', d.UnixSeconds, 'unixepoch') AS INTEGER)
            FROM AccountingEntries e
            JOIN AccountingDateValues d ON d.Id = e.EntryDateId
            WHERE e.Id = $id AND e.IsSuppressed = 0;
            """;
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            throw new AccountingWorkbookValidationException(sheet, row, $"Accounting Entry ID {id} does not exist or is suppressed.");
        return new ExistingEntry(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            GetNullableInt64(reader, 4), GetNullableInt64(reader, 5), GetNullableInt64(reader, 6),
            reader.GetInt64(7) != 0, reader.GetInt32(8));
    }

    private static int ReadNextDisplayOrder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        int year)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(e.DisplayOrder), 0) + 1
            FROM AccountingEntries e
            JOIN AccountingDateValues d ON d.Id = e.EntryDateId
            WHERE e.AccountId = $account
              AND d.UnixSeconds >= $start
              AND d.UnixSeconds < $end;
            """;
        command.Parameters.AddWithValue("$account", accountId);
        AccountingLedgerService.AddYearParameters(command, year);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AddEntryValueParameters(
        SqliteCommand command,
        long? id,
        long dateId,
        long amountId,
        long descriptionId,
        long? explanationId,
        long? categoryId,
        long? checkNumberId,
        bool needsReview,
        long timestampId)
    {
        if (id.HasValue)
            command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$date", dateId);
        command.Parameters.AddWithValue("$amount", amountId);
        command.Parameters.AddWithValue("$description", descriptionId);
        command.Parameters.AddWithValue("$explanation", (object?)explanationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$check", (object?)checkNumberId ?? DBNull.Value);
        command.Parameters.AddWithValue("$review", needsReview ? 1 : 0);
        command.Parameters.AddWithValue("$timestamp", timestampId);
    }

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static void SaveCrashSafely(XLWorkbook workbook, string path, bool replacing)
    {
        string tempPath = path + ".tmp.xlsx";
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            workbook.SaveAs(tempPath);
            if (replacing)
            {
                try
                {
                    File.Replace(
                        tempPath,
                        path,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(tempPath, path, overwrite: true);
                }
            }
            else
                File.Move(tempPath, path);
        }
        catch (IOException exception)
        {
            throw new IOException(
                "The workbook could not be replaced. Save and close it in Excel, then try again; the existing workbook was not overwritten.",
                exception);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static decimal CentsToDecimal(long cents) => cents / 100m;

    private sealed record WorkbookRow(
        string SheetName,
        int RowNumber,
        string AccountLast4,
        long? EntryId,
        DateOnly Date,
        string Description,
        string CheckNumber,
        long AmountCents,
        string Category,
        string Explanation);

    private sealed record LegacyWorkbookRow(
        string SheetName,
        int RowNumber,
        string AccountLast4,
        IReadOnlyList<long> TransactionIds,
        string Description,
        string Explanation,
        string? Category);

    private sealed record ExistingEntry(
        long AccountId,
        long DateId,
        long AmountId,
        long DescriptionId,
        long? ExplanationId,
        long? CategoryId,
        long? CheckNumberId,
        bool NeedsReview,
        int OriginalYear);
}

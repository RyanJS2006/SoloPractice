using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using SoloPractice.Data;
using SoloPractice.Utilities;
using System.Diagnostics;
using System.Globalization;

namespace SoloPractice.Services;

internal sealed record AccountingWorksheetResult(
    string WorkbookPath,
    int CheckingRows,
    int SavingsRows,
    int CreditCardRows,
    int ReviewRows,
    bool ReplacedExistingWorkbook);

internal static class AccountingWorksheetGenerator
{
    private const string CheckingAccount = "8936";
    private const string SavingsAccount = "9350";
    private const string CreditCardAccount = "8027";

    private static readonly string[] CheckingCategories =
    [
        "Counselling Fee",
        "Other Revenue",
        "Transfers in",
        "Owners Draw",
        "Payroll Taxes",
        "Rebates",
        "Rent",
        "Auto Expense",
        "Meals & Entertainment",
        "Insurance - Liability",
        "Insurance - Work Comp",
        "Interest Expense",
        "Legal Expense",
        "Licenses & Dues",
        "Office Expense",
        "Telephone",
        "Misc. Expense",
        "Credit Card Payment, Transfers out and non-deductible exp"
    ];

    private static readonly string[] SavingsCategories =
    [
        "Counselling Fee",
        "Other Revenue",
        "Transfers In",
        "Transfers Out",
        "Owners Draw",
        "Payroll Taxes",
        "Accounting Fee",
        "Advertising Expense",
        "Auto Expense",
        "Meals & Entertainment",
        "Insurance - Liability",
        "Insurance - Work Comp",
        "Interest Expense",
        "Legal Expense",
        "Licenses & Dues",
        "Office Expense",
        "Telephone",
        "Misc. Expense"
    ];

    private static readonly string[] CreditCardCategories =
    [
        "Credit Card Pmt",
        "Other Revenue",
        "Refunds / Reimbursements",
        "Software Expense",
        "Advertising",
        "Office Expense",
        "Continuing Ed",
        "Auto Expense",
        "Meals & Entertainment",
        "Interest Expense",
        "License Fees",
        "Owner Draw",
        "Misc. Expense"
    ];

    public static AccountingWorksheetResult GenerateOrUpdate(
        int year,
        bool openAfterSaving = true)
    {
        if (year < 2000 || year >= 9999)
            throw new ArgumentOutOfRangeException(nameof(year));

        AppPaths.EnsureDirectoriesExist();

        string workbookPath = Path.Combine(
            AppPaths.ApplicationDirectory,
            $"{year} Accounting Worksheet.xlsx");

        bool replacingExisting = File.Exists(workbookPath);

        Dictionary<string, ManualOverride> overrides =
            replacingExisting
                ? ReadManualOverrides(workbookPath)
                : new Dictionary<string, ManualOverride>(StringComparer.Ordinal);

        using SqliteConnection connection = Database.OpenConnection();

        List<DepositSourceRow> checkingSource =
            ReadDepositRows(connection, CheckingAccount, year);

        List<DepositSourceRow> savingsSource =
            ReadDepositRows(connection, SavingsAccount, year);

        List<CreditCardSourceRow> creditCardSource =
            ReadCreditCardRows(connection, CreditCardAccount, year);

        long checkingOpening = FindOpeningBalance(checkingSource, 0);
        long savingsOpening = FindOpeningBalance(savingsSource, 0);
        long creditCardOpening = 0;

        List<AccountingRow> checkingRows =
            BuildCheckingRows(checkingSource);

        List<AccountingRow> savingsRows =
            BuildSavingsRows(savingsSource);

        List<AccountingRow> creditCardRows =
            BuildCreditCardRows(creditCardSource);

        int reviewRows =
            CountUnresolvedReviewRows(
                $"{year} Checking",
                checkingRows,
                overrides) +
            CountUnresolvedReviewRows(
                $"{year} Savings",
                savingsRows,
                overrides) +
            CountUnresolvedReviewRows(
                $"{year} Chase Visa",
                creditCardRows,
                overrides);

        using var workbook = new XLWorkbook();

        SheetSummary checkingSummary = BuildDepositSheet(
            workbook,
            $"{year} Checking",
            $"CHECKING ACCOUNT: {CheckingAccount}",
            year,
            checkingOpening,
            checkingRows,
            CheckingCategories,
            creditCategoryCount: 3,
            overrides);

        SheetSummary savingsSummary = BuildDepositSheet(
            workbook,
            $"{year} Savings",
            $"SAVINGS ACCOUNT: {SavingsAccount}",
            year,
            savingsOpening,
            savingsRows,
            SavingsCategories,
            creditCategoryCount: 3,
            overrides);

        SheetSummary creditCardSummary = BuildCreditCardSheet(
            workbook,
            $"{year} Chase Visa",
            "Chase Visa",
            year,
            creditCardOpening,
            creditCardRows,
            CreditCardCategories,
            creditCategoryCount: 3,
            overrides);

        BuildTrialBalanceSheet(
            workbook,
            year,
            checkingSummary,
            savingsSummary,
            creditCardSummary);

        string tempPath = workbookPath + ".tmp.xlsx";
        string backupPath = workbookPath + ".bak";

        if (File.Exists(tempPath))
            File.Delete(tempPath);

        workbook.SaveAs(tempPath);

        if (replacingExisting)
            File.Copy(workbookPath, backupPath, overwrite: true);

        File.Move(tempPath, workbookPath, overwrite: true);

        if (openAfterSaving)
            OpenWorkbook(workbookPath);

        return new AccountingWorksheetResult(
            workbookPath,
            checkingRows.Count,
            savingsRows.Count,
            creditCardRows.Count,
            reviewRows,
            replacingExisting);
    }

    private static List<DepositSourceRow> ReadDepositRows(
        SqliteConnection connection,
        string accountLast4,
        int year)
    {
        long startUnix = ToUnixMidnight(new DateOnly(year, 1, 1));
        long endUnix = ToUnixMidnight(new DateOnly(year + 1, 1, 1));

        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH LatestImport AS
            (
                SELECT f.Id
                FROM ImportFiles f
                JOIN Accounts fa
                  ON fa.Id = f.AccountId
                JOIN DateValues fd
                  ON fd.Id = f.DownloadDateId
                WHERE fa.Last4 = $account
                ORDER BY fd.UnixSeconds DESC, f.Id DESC
                LIMIT 1
            ),
            LatestRows AS
            (
                SELECT ir.TransactionId,
                       ir.SourceRowNumber
                FROM ImportRows ir
                WHERE ir.ImportFileId = (SELECT Id FROM LatestImport)
            )
            SELECT
                t.Id,
                pd.UnixSeconds AS PostingUnixSeconds,
                mv.Cents AS AmountCents,
                tt.Code AS TypeCode,
                bal.Cents AS BalanceCents,
                d.CheckOrSlipNumber,
                ao.CompanyName AS AchCompanyName,
                aed.Description AS AchEntryDescription,
                dir.Name AS TransferDirection,
                tc.Institution AS TransferInstitution,
                tc.AccountLabel AS TransferAccountLabel,
                tc.Last4 AS TransferLast4,
                target.Last4 AS TargetCardLast4,
                dc.MerchantDescriptor AS DebitMerchant,
                atm.Action AS AtmAction,
                atm.Location AS AtmLocation,
                fee.Description AS FeeDescription,
                rtp.Sender AS RealTimeSender,
                rtp.Purpose AS RealTimePurpose,
                unparsed.Description AS UnparsedDescription,
                lr.SourceRowNumber
            FROM Transactions t
            JOIN Accounts acc
              ON acc.Id = t.AccountId
            JOIN DateValues pd
              ON pd.Id = t.PostingDateId
            JOIN MoneyValues mv
              ON mv.Id = t.AmountId
            JOIN TransactionTypes tt
              ON tt.Id = t.TypeId
            JOIN DepositTransactions d
              ON d.TransactionId = t.Id
            LEFT JOIN MoneyValues bal
              ON bal.Id = d.BalanceAmountId
            LEFT JOIN AchTransactions ach
              ON ach.TransactionId = t.Id
            LEFT JOIN AchOriginators ao
              ON ao.Id = ach.OriginatorId
            LEFT JOIN AchEntryDescriptions aed
              ON aed.Id = ach.EntryDescriptionId
            LEFT JOIN AccountTransfers x
              ON x.TransactionId = t.Id
            LEFT JOIN TransferDirections dir
              ON dir.Id = x.DirectionId
            LEFT JOIN TransferCounterparties tc
              ON tc.Id = x.CounterpartyId
            LEFT JOIN ChaseCardPayments cp
              ON cp.TransactionId = t.Id
            LEFT JOIN Accounts target
              ON target.Id = cp.TargetAccountId
            LEFT JOIN DebitCardTransactions dc
              ON dc.TransactionId = t.Id
            LEFT JOIN AtmTransactions atm
              ON atm.TransactionId = t.Id
            LEFT JOIN FeeTransactions fee
              ON fee.TransactionId = t.Id
            LEFT JOIN RealTimePayments rtp
              ON rtp.TransactionId = t.Id
            LEFT JOIN UnparsedDepositDescriptions unparsed
              ON unparsed.TransactionId = t.Id
            LEFT JOIN LatestRows lr
              ON lr.TransactionId = t.Id
            WHERE acc.Last4 = $account
              AND pd.UnixSeconds >= $start
              AND pd.UnixSeconds < $end
            ORDER BY
                pd.UnixSeconds ASC,
                COALESCE(lr.SourceRowNumber, 0) DESC,
                t.Id DESC;
            """;

        command.Parameters.AddWithValue("$account", accountLast4);
        command.Parameters.AddWithValue("$start", startUnix);
        command.Parameters.AddWithValue("$end", endUnix);

        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<DepositSourceRow>();

        while (reader.Read())
        {
            result.Add(new DepositSourceRow(
                TransactionId: reader.GetInt64(0),
                PostingDate: FromUnixMidnight(reader.GetInt64(1)),
                AmountCents: reader.GetInt64(2),
                TypeCode: reader.GetString(3),
                BalanceCents: GetNullableInt64(reader, 4),
                CheckOrSlipNumber: GetNullableString(reader, 5),
                AchCompanyName: GetNullableString(reader, 6),
                AchEntryDescription: GetNullableString(reader, 7),
                TransferDirection: GetNullableString(reader, 8),
                TransferInstitution: GetNullableString(reader, 9),
                TransferAccountLabel: GetNullableString(reader, 10),
                TransferLast4: GetNullableString(reader, 11),
                TargetCardLast4: GetNullableString(reader, 12),
                DebitMerchant: GetNullableString(reader, 13),
                AtmAction: GetNullableString(reader, 14),
                AtmLocation: GetNullableString(reader, 15),
                FeeDescription: GetNullableString(reader, 16),
                RealTimeSender: GetNullableString(reader, 17),
                RealTimePurpose: GetNullableString(reader, 18),
                UnparsedDescription: GetNullableString(reader, 19),
                SourceRowNumber: reader.IsDBNull(20) ? 0 : reader.GetInt32(20)));
        }

        return result;
    }

    private static List<CreditCardSourceRow> ReadCreditCardRows(
        SqliteConnection connection,
        string accountLast4,
        int year)
    {
        long startUnix = ToUnixMidnight(new DateOnly(year, 1, 1));
        long endUnix = ToUnixMidnight(new DateOnly(year + 1, 1, 1));

        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH LatestImport AS
            (
                SELECT f.Id
                FROM ImportFiles f
                JOIN Accounts fa
                  ON fa.Id = f.AccountId
                JOIN DateValues fd
                  ON fd.Id = f.DownloadDateId
                WHERE fa.Last4 = $account
                ORDER BY fd.UnixSeconds DESC, f.Id DESC
                LIMIT 1
            ),
            LatestRows AS
            (
                SELECT ir.TransactionId,
                       ir.SourceRowNumber
                FROM ImportRows ir
                WHERE ir.ImportFileId = (SELECT Id FROM LatestImport)
            )
            SELECT
                t.Id,
                pd.UnixSeconds AS PostingUnixSeconds,
                td.UnixSeconds AS TransactionUnixSeconds,
                mv.Cents AS AmountCents,
                tt.Code AS TypeCode,
                merchant.Name AS MerchantName,
                category.Name AS ChaseCategory,
                cc.Memo,
                lr.SourceRowNumber
            FROM Transactions t
            JOIN Accounts acc
              ON acc.Id = t.AccountId
            JOIN DateValues pd
              ON pd.Id = t.PostingDateId
            JOIN MoneyValues mv
              ON mv.Id = t.AmountId
            JOIN TransactionTypes tt
              ON tt.Id = t.TypeId
            JOIN CreditCardTransactions cc
              ON cc.TransactionId = t.Id
            JOIN DateValues td
              ON td.Id = cc.TransactionDateId
            JOIN CreditCardMerchants merchant
              ON merchant.Id = cc.MerchantId
            LEFT JOIN CreditCardCategories category
              ON category.Id = cc.CategoryId
            LEFT JOIN LatestRows lr
              ON lr.TransactionId = t.Id
            WHERE acc.Last4 = $account
              AND td.UnixSeconds >= $start
              AND td.UnixSeconds < $end
            ORDER BY
                td.UnixSeconds ASC,
                COALESCE(lr.SourceRowNumber, 0) DESC,
                t.Id DESC;
            """;

        command.Parameters.AddWithValue("$account", accountLast4);
        command.Parameters.AddWithValue("$start", startUnix);
        command.Parameters.AddWithValue("$end", endUnix);

        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<CreditCardSourceRow>();

        while (reader.Read())
        {
            result.Add(new CreditCardSourceRow(
                TransactionId: reader.GetInt64(0),
                PostingDate: FromUnixMidnight(reader.GetInt64(1)),
                TransactionDate: FromUnixMidnight(reader.GetInt64(2)),
                AmountCents: reader.GetInt64(3),
                TypeCode: reader.GetString(4),
                MerchantName: reader.GetString(5),
                ChaseCategory: GetNullableString(reader, 6),
                Memo: GetNullableString(reader, 7),
                SourceRowNumber: reader.IsDBNull(8) ? 0 : reader.GetInt32(8)));
        }

        return result;
    }

    private static long FindOpeningBalance(
        IReadOnlyList<DepositSourceRow> rows,
        long fallbackCents)
    {
        if (rows.Count == 0)
            return fallbackCents;

        DateOnly firstDate = rows[0].PostingDate;

        DepositSourceRow? firstWithBalance = rows
            .Where(row => row.PostingDate == firstDate && row.BalanceCents.HasValue)
            .OrderByDescending(row => row.SourceRowNumber)
            .ThenByDescending(row => row.TransactionId)
            .FirstOrDefault();

        if (firstWithBalance is null || !firstWithBalance.BalanceCents.HasValue)
            return fallbackCents;

        return firstWithBalance.BalanceCents.Value - firstWithBalance.AmountCents;
    }

    private static List<AccountingRow> BuildCheckingRows(
        IReadOnlyList<DepositSourceRow> source)
    {
        var grouped = new Dictionary<string, MutableAccountingRow>(StringComparer.Ordinal);
        int ordinal = 0;

        foreach (DepositSourceRow row in source)
        {
            ClassifiedTransaction classified = ClassifyChecking(row);

            string groupKey = classified.Aggregate
                ? $"{row.PostingDate:yyyy-MM-dd}|{classified.GroupKey}"
                : $"{row.PostingDate:yyyy-MM-dd}|{classified.GroupKey}|{row.TransactionId}";

            if (!grouped.TryGetValue(groupKey, out MutableAccountingRow? accounting))
            {
                accounting = new MutableAccountingRow(
                    row.PostingDate,
                    classified.Description,
                    row.CheckOrSlipNumber,
                    classified.Category,
                    classified.ReviewReason,
                    ordinal++);

                grouped.Add(groupKey, accounting);
            }

            accounting.SourceTransactionIds.Add(row.TransactionId);
            accounting.AmountCents += row.AmountCents;
        }

        return grouped.Values
            .OrderBy(row => row.Date)
            .ThenBy(row => row.Ordinal)
            .Select(row => row.ToImmutable())
            .ToList();
    }

    private static List<AccountingRow> BuildSavingsRows(
        IReadOnlyList<DepositSourceRow> source)
    {
        var result = new List<AccountingRow>();

        foreach (DepositSourceRow row in source)
        {
            ClassifiedTransaction classified = ClassifySavings(row);

            result.Add(new AccountingRow(
                SourceKey: row.TransactionId.ToString(CultureInfo.InvariantCulture),
                Date: row.PostingDate,
                Description: classified.Description,
                CheckNumber: row.CheckOrSlipNumber,
                AmountCents: row.AmountCents,
                Category: classified.Category,
                ReviewReason: classified.ReviewReason));
        }

        return result;
    }

    private static List<AccountingRow> BuildCreditCardRows(
        IReadOnlyList<CreditCardSourceRow> source)
    {
        var result = new List<AccountingRow>();

        foreach (CreditCardSourceRow row in source)
        {
            ClassifiedTransaction classified = ClassifyCreditCard(row);

            result.Add(new AccountingRow(
                SourceKey: row.TransactionId.ToString(CultureInfo.InvariantCulture),
                Date: row.TransactionDate,
                Description: classified.Description,
                CheckNumber: null,
                AmountCents: row.AmountCents,
                Category: classified.Category,
                ReviewReason: classified.ReviewReason));
        }

        return result;
    }

    private static ClassifiedTransaction ClassifyChecking(
        DepositSourceRow row)
    {
        string type = row.TypeCode.ToUpperInvariant();
        string company = (row.AchCompanyName ?? string.Empty).Trim();
        string companyUpper = company.ToUpperInvariant();
        string entryUpper = (row.AchEntryDescription ?? string.Empty).ToUpperInvariant();

        if (type == "ACH_CREDIT")
        {
            if (companyUpper.Contains("STRIPE"))
            {
                return Auto(
                    "checking:stripe",
                    "Stripe payment processing through Simple Practice - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            if (companyUpper.Contains("BCBS"))
            {
                return Auto(
                    "checking:bcbs",
                    "BCBS payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            if (companyUpper.Contains("UNITEDHEALTHCARE"))
            {
                return Auto(
                    "checking:united",
                    "UBH payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            if (companyUpper.Contains("AETNA"))
            {
                return Auto(
                    "checking:aetna",
                    "Aetna payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            if (companyUpper.Contains("CIGNA"))
            {
                return Auto(
                    "checking:cigna",
                    "Cigna payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            if (companyUpper == "UMR")
            {
                return Auto(
                    "checking:umr",
                    "UMR payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            if (companyUpper.Contains("VENTANEX"))
            {
                return Auto(
                    "checking:guidehealth",
                    "GuideHealth Behavioral payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            if (companyUpper.Contains("HNB - ECHO"))
            {
                // The existing accounting workbook identifies the early $69.75
                // ECHO deposits as Magellan and the later ECHO claim payments as
                // Meritain. Keep those known mappings instead of exposing ECHO's
                // payment-processor name to the accountant.
                if (row.AmountCents == 6_975)
                {
                    return Auto(
                        "checking:magellan",
                        "HNB?Magellan - Counseling fee",
                        "Counselling Fee",
                        aggregate: true);
                }

                if (row.AmountCents == 3 && entryUpper.Contains("ACH XFR"))
                {
                    return Auto(
                        "checking:guidehealth-ping",
                        "GuideHealth Behavioral payment - Counseling fee",
                        "Counselling Fee",
                        aggregate: true);
                }

                return Auto(
                    "checking:meritain-echo",
                    "Meritain Health payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            if (entryUpper.Contains("HCCLAIMPMT") ||
                entryUpper.Contains("EOP") ||
                entryUpper.Contains("CLAIM"))
            {
                return Auto(
                    $"checking:claim:{companyUpper}",
                    $"{FriendlyCompanyName(company)} payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            return Review(
                $"checking:ach-credit:{companyUpper}:{entryUpper}",
                string.IsNullOrWhiteSpace(company)
                    ? "ACH credit"
                    : $"{company} - {row.AchEntryDescription}",
                "Unrecognized ACH credit; choose the revenue category.",
                aggregate: false);
        }

        if (type == "MISC_CREDIT")
        {
            string sender = row.RealTimeSender ?? string.Empty;

            if (sender.Contains("UnitedHealthcare", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    row.RealTimePurpose,
                    "HCCLAIMPMT",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Auto(
                    "checking:united-realtime",
                    "UBH payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            return Review(
                $"checking:misc-credit:{sender}",
                string.IsNullOrWhiteSpace(sender)
                    ? "Miscellaneous credit"
                    : sender,
                "Unrecognized miscellaneous credit; verify the revenue category.",
                aggregate: false);
        }

        if (type == "ACCT_XFER")
        {
            bool to = string.Equals(
                row.TransferDirection,
                "TO",
                StringComparison.OrdinalIgnoreCase);

            bool from = string.Equals(
                row.TransferDirection,
                "FROM",
                StringComparison.OrdinalIgnoreCase);

            if (to && (row.TransferLast4 == "0231" || row.TransferLast4 == "8153"))
            {
                return Auto(
                    "checking:owners-draw",
                    "Owners Draw",
                    "Owners Draw",
                    aggregate: true);
            }

            if (to && row.TransferLast4 == SavingsAccount)
            {
                return Auto(
                    "checking:transfer-to-savings",
                    $"Transfer to Savings *{SavingsAccount}",
                    "Credit Card Payment, Transfers out and non-deductible exp",
                    aggregate: true);
            }

            if (from && row.TransferLast4 == SavingsAccount)
            {
                return Auto(
                    "checking:transfer-from-savings",
                    $"Transfer from Savings *{SavingsAccount}",
                    "Transfers in",
                    aggregate: true);
            }

            string description =
                $"Transfer {row.TransferDirection} " +
                $"{row.TransferInstitution} {row.TransferAccountLabel} *{row.TransferLast4}";

            return Review(
                $"checking:transfer:{row.TransferDirection}:{row.TransferLast4}",
                description.Trim(),
                "Unrecognized transfer counterparty; verify whether it is an owner draw or business transfer.",
                aggregate: true);
        }

        if (type == "LOAN_PMT" && row.TargetCardLast4 == CreditCardAccount)
        {
            return Auto(
                "checking:chase-card-payment",
                "Payment to Chase card",
                "Credit Card Payment, Transfers out and non-deductible exp",
                aggregate: true);
        }

        if (type == "ACH_DEBIT")
        {
            if (companyUpper == "IRS")
            {
                return Auto(
                    "checking:irs-tax",
                    "federal tax automatic withdrawal",
                    "Payroll Taxes",
                    aggregate: true);
            }

            if (companyUpper.Contains("IL DEPT OF REVEN"))
            {
                return Auto(
                    "checking:illinois-tax",
                    "Illinois Dept of Revenue-automatic deduction",
                    "Payroll Taxes",
                    aggregate: true);
            }

            return Review(
                $"checking:ach-debit:{companyUpper}:{entryUpper}",
                string.IsNullOrWhiteSpace(company)
                    ? "ACH debit"
                    : $"{company} - {row.AchEntryDescription}",
                "Unrecognized ACH debit; verify the expense category.",
                aggregate: false);
        }

        if (type == "CHECK_PAID")
        {
            if (row.AmountCents == -40_500)
            {
                return Auto(
                    $"checking:rent-check:{row.TransactionId}",
                    "Rent - Collings LaGrange Mall, LLC",
                    "Rent",
                    aggregate: false);
            }

            if (row.AmountCents == -5_135)
            {
                return Auto(
                    $"checking:wifi-check:{row.TransactionId}",
                    "Kris Maynard, LCSW- reimbursement for Xfinity wifi",
                    "Office Expense",
                    aggregate: false);
            }

            if (row.CheckOrSlipNumber == "1368" && row.AmountCents == -48_000)
            {
                return Auto(
                    $"checking:illinois-check:{row.TransactionId}",
                    "Illinois Dept of Revenue",
                    "Payroll Taxes",
                    aggregate: false);
            }

            return Review(
                $"checking:check:{row.TransactionId}",
                string.IsNullOrWhiteSpace(row.CheckOrSlipNumber)
                    ? "Check payment"
                    : $"Check {row.CheckOrSlipNumber}",
                "Chase does not provide the payee for this check; enter the description/category manually.",
                aggregate: false);
        }

        if (type == "CHECK_DEPOSIT")
        {
            // Chase's deposit download does not carry the payer name, but the
            // existing 2026 worksheet repeatedly identifies these exact claim
            // amounts as the same insurers. These rules recover those known rows.
            if (row.AmountCents == 5_902 || row.AmountCents == 9_605)
            {
                return Auto(
                    "checking:meritain-check-deposit",
                    "Meritain Health payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            if (row.AmountCents == 10_476)
            {
                return Auto(
                    "checking:guidehealth-check-deposit",
                    "GuideHealth Behavioral payment - Counseling fee",
                    "Counselling Fee",
                    aggregate: true);
            }

            return Review(
                $"checking:check-deposit:{row.TransactionId}",
                string.IsNullOrWhiteSpace(row.CheckOrSlipNumber)
                    ? "Check deposit"
                    : $"Check deposit #{row.CheckOrSlipNumber}",
                "Chase does not identify the check payer in this download; verify the revenue description/category.",
                aggregate: false);
        }

        if (type == "DEBIT_CARD")
        {
            string merchant = row.DebitMerchant ?? string.Empty;

            if (merchant.Contains("MATRIX", StringComparison.OrdinalIgnoreCase))
            {
                return Auto(
                    $"checking:matrix:{row.TransactionId}",
                    "CEUs workshops - MATRIX CEUMATRIX.COM",
                    "Licenses & Dues",
                    aggregate: false);
            }

            return Review(
                $"checking:debit-card:{row.TransactionId}",
                string.IsNullOrWhiteSpace(merchant) ? "Debit card purchase" : merchant,
                "Unrecognized debit-card purchase; verify the expense category.",
                aggregate: false);
        }

        if (type == "ATM" &&
            string.Equals(row.AtmAction, "WITHDRAWAL", StringComparison.OrdinalIgnoreCase))
        {
            return Auto(
                $"checking:atm-owner-draw:{row.TransactionId}",
                "Owners Draw",
                "Owners Draw",
                aggregate: false);
        }

        if (type == "FEE_TRANSACTION")
        {
            return Auto(
                $"checking:fee:{row.TransactionId}",
                row.FeeDescription ?? "Bank fee",
                "Misc. Expense",
                aggregate: false);
        }

        string fallbackDescription =
            row.UnparsedDescription ??
            row.DebitMerchant ??
            row.FeeDescription ??
            row.TypeCode;

        return Review(
            $"checking:unknown:{row.TransactionId}",
            fallbackDescription,
            $"No accounting rule exists yet for Chase transaction type {row.TypeCode}.",
            aggregate: false);
    }

    private static ClassifiedTransaction ClassifySavings(
        DepositSourceRow row)
    {
        if (row.TypeCode.Equals("ACCT_XFER", StringComparison.OrdinalIgnoreCase))
        {
            bool to = string.Equals(
                row.TransferDirection,
                "TO",
                StringComparison.OrdinalIgnoreCase);

            bool from = string.Equals(
                row.TransferDirection,
                "FROM",
                StringComparison.OrdinalIgnoreCase);

            if (from && row.TransferLast4 == CheckingAccount)
            {
                return Auto(
                    "savings:from-checking",
                    $"Transfer from Checking *{CheckingAccount}",
                    "Transfers In",
                    aggregate: true);
            }

            if (to && row.TransferLast4 == CheckingAccount)
            {
                return Auto(
                    "savings:to-checking",
                    $"Transfer to Checking *{CheckingAccount}",
                    "Transfers Out",
                    aggregate: true);
            }

            if (to && (row.TransferLast4 == "0231" || row.TransferLast4 == "8153"))
            {
                return Auto(
                    "savings:owners-draw",
                    "Owners Draw",
                    "Owners Draw",
                    aggregate: true);
            }
        }

        return Review(
            $"savings:unknown:{row.TransactionId}",
            row.UnparsedDescription ?? row.TypeCode,
            "No savings-account accounting rule exists for this transaction yet.",
            aggregate: false);
    }

    private static ClassifiedTransaction ClassifyCreditCard(
        CreditCardSourceRow row)
    {
        string merchant = row.MerchantName.Trim();
        string upper = merchant.ToUpperInvariant();

        if (row.TypeCode.Equals("Payment", StringComparison.OrdinalIgnoreCase))
        {
            return Auto(
                "card:payment",
                "payment",
                "Credit Card Pmt",
                aggregate: false);
        }

        if (upper.Contains("PSYCHOLOGY TODAY"))
        {
            return Auto(
                "card:psychology-today",
                "Psychology Today advertisement",
                "Advertising",
                aggregate: false);
        }

        if (upper.Contains("SIMPLEPRACTICE"))
        {
            return Auto(
                "card:simplepractice",
                "SimplePractice - electronic record keeping, claim filing, Stripe payment site",
                "Software Expense",
                aggregate: false);
        }

        if (upper.Contains("MICROSOFT") && upper.Contains("365"))
        {
            return Auto(
                "card:microsoft365",
                "Microsoft 365",
                "Software Expense",
                aggregate: false);
        }

        if (upper.Contains("AMAZON"))
        {
            return Auto(
                "card:amazon",
                "Amazon",
                "Office Expense",
                aggregate: false);
        }

        if (upper.Contains("ILLINOIS COUNSELING ASSOC"))
        {
            return Auto(
                "card:illinois-counseling",
                "Illinois Counseling Association - workshop",
                "Continuing Ed",
                aggregate: false);
        }

        if (upper.Contains("E CARE BHI"))
        {
            return Auto(
                "card:e-care-bhi",
                "E Care BHI - workshop",
                "Continuing Ed",
                aggregate: false);
        }

        if (upper.Contains("IFS INSTITUTE"))
        {
            return Auto(
                "card:ifs",
                "IFS institute - general application fee for future workshops",
                "Continuing Ed",
                aggregate: false);
        }

        if (upper.Contains("ESET"))
        {
            return Auto(
                "card:eset",
                "ESET antivirus for computer",
                "Office Expense",
                aggregate: false);
        }

        if (upper.Contains("IAODAPCA"))
        {
            return new ClassifiedTransaction(
                "card:iaodapca",
                "IAODAPCA.ORG - CADC certification fee",
                "License Fees",
                "Verify this category. The older workbook placed this merchant under Interest Expense, but the description looks like a licensing/certification fee.",
                Aggregate: false);
        }

        string chaseCategory = row.ChaseCategory ?? string.Empty;

        if (chaseCategory.Equals("Education", StringComparison.OrdinalIgnoreCase))
        {
            return Auto(
                $"card:education:{upper}",
                merchant,
                "Continuing Ed",
                aggregate: false);
        }

        if (chaseCategory.Equals("Office & Shipping", StringComparison.OrdinalIgnoreCase) ||
            chaseCategory.Equals("Merchandise & Inventory", StringComparison.OrdinalIgnoreCase))
        {
            return Auto(
                $"card:office:{upper}",
                merchant,
                "Office Expense",
                aggregate: false);
        }

        return Review(
            $"card:unknown:{row.TransactionId}",
            merchant,
            $"No accounting rule exists yet for merchant '{merchant}' / Chase category '{row.ChaseCategory}'.",
            aggregate: false);
    }

    private static SheetSummary BuildDepositSheet(
        XLWorkbook workbook,
        string sheetName,
        string accountTitle,
        int year,
        long openingBalanceCents,
        IReadOnlyList<AccountingRow> rows,
        IReadOnlyList<string> categories,
        int creditCategoryCount,
        IReadOnlyDictionary<string, ManualOverride> overrides)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(sheetName);

        const int dateColumn = 1;
        const int descriptionColumn = 2;
        const int debitColumn = 4;
        const int creditColumn = 5;
        const int balanceColumn = 6;
        const int categoryStartColumn = 8;

        int explanationColumn = categoryStartColumn + categories.Count;
        int sourceIdColumn = explanationColumn + 1;
        int lastVisibleColumn = explanationColumn;

        ws.Cell(1, 1).Value = $"Tax Year: {year}";
        ws.Cell(1, 4).Value = accountTitle;
        ws.Cell(1, categoryStartColumn).Value = "Accounting Categories";

        string[] fixedHeaders =
        [
            "Date",
            "Description",
            "Chk #",
            "Dr.",
            "Cr.",
            "Balance"
        ];

        for (int i = 0; i < fixedHeaders.Length; i++)
            ws.Cell(2, i + 1).Value = fixedHeaders[i];

        for (int i = 0; i < categories.Count; i++)
            ws.Cell(2, categoryStartColumn + i).Value = categories[i];

        ws.Cell(2, explanationColumn).Value = "Explanation";
        ws.Cell(2, sourceIdColumn).Value = "Source Transaction IDs";

        ws.Cell(3, dateColumn).Value = new DateTime(year, 1, 1);
        ws.Cell(3, descriptionColumn).Value = "Balance forward";
        ws.Cell(3, balanceColumn).Value = CentsToDecimal(openingBalanceCents);

        int rowNumber = 4;

        foreach (AccountingRow row in rows)
        {
            WriteAccountingRow(
                ws,
                rowNumber,
                row,
                categories,
                categoryStartColumn,
                explanationColumn,
                sourceIdColumn,
                overrides);

            if (row.AmountCents < 0)
                ws.Cell(rowNumber, debitColumn).Value = CentsToDecimal(-row.AmountCents);
            else
                ws.Cell(rowNumber, creditColumn).Value = CentsToDecimal(row.AmountCents);

            ws.Cell(rowNumber, balanceColumn).FormulaA1 =
                $"F{rowNumber - 1}-D{rowNumber}+E{rowNumber}";

            rowNumber++;
        }

        int lastDataRow = rowNumber - 1;
        int totalsRow = rowNumber;
        int checkRow = totalsRow + 1;

        ws.Cell(totalsRow, 1).Value = "Totals:";
        ws.Cell(totalsRow, debitColumn).FormulaA1 =
            $"SUM(D3:D{lastDataRow})";
        ws.Cell(totalsRow, creditColumn).FormulaA1 =
            $"SUM(E3:E{lastDataRow})";
        ws.Cell(totalsRow, balanceColumn).FormulaA1 =
            $"F3-D{totalsRow}+E{totalsRow}";

        var categoryTotalCells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < categories.Count; i++)
        {
            int column = categoryStartColumn + i;
            string letter = ColumnLetter(column);

            ws.Cell(totalsRow, column).FormulaA1 =
                $"SUM({letter}3:{letter}{lastDataRow})";

            categoryTotalCells[categories[i]] =
                QuoteSheetCell(sheetName, letter, totalsRow);
        }

        string firstCreditCategoryLetter =
            ColumnLetter(categoryStartColumn);
        string lastCreditCategoryLetter =
            ColumnLetter(
                categoryStartColumn + creditCategoryCount - 1);
        string firstDebitCategoryLetter =
            ColumnLetter(
                categoryStartColumn + creditCategoryCount);
        string lastDebitCategoryLetter =
            ColumnLetter(
                categoryStartColumn + categories.Count - 1);

        ws.Cell(checkRow, 2).Value = "Credit category check";
        ws.Cell(checkRow, 3).FormulaA1 =
            $"IF(ABS(E{totalsRow}-SUM({firstCreditCategoryLetter}{totalsRow}:{lastCreditCategoryLetter}{totalsRow}))<0.005,\"OK\",\"CR NOT IN BALANCE\")";

        ws.Cell(checkRow, 5).Value = "Debit category check";
        ws.Cell(checkRow, 6).FormulaA1 =
            $"IF(ABS(D{totalsRow}-SUM({firstDebitCategoryLetter}{totalsRow}:{lastDebitCategoryLetter}{totalsRow}))<0.005,\"OK\",\"DR NOT IN BALANCE\")";

        ApplySheetFormatting(
            ws,
            lastDataRow,
            totalsRow,
            checkRow,
            lastVisibleColumn,
            sourceIdColumn,
            categoryStartColumn,
            explanationColumn);

        return new SheetSummary(
            SheetName: sheetName,
            EndingBalanceCell: QuoteSheetCell(sheetName, "F", totalsRow),
            CategoryTotalCells: categoryTotalCells,
            TotalsRow: totalsRow);
    }

    private static SheetSummary BuildCreditCardSheet(
        XLWorkbook workbook,
        string sheetName,
        string accountTitle,
        int year,
        long openingBalanceCents,
        IReadOnlyList<AccountingRow> rows,
        IReadOnlyList<string> categories,
        int creditCategoryCount,
        IReadOnlyDictionary<string, ManualOverride> overrides)
    {
        IXLWorksheet ws = workbook.Worksheets.Add(sheetName);

        const int dateColumn = 1;
        const int descriptionColumn = 2;
        const int chargeColumn = 4;
        const int paymentColumn = 5;
        const int balanceColumn = 6;
        const int categoryStartColumn = 8;

        int explanationColumn = categoryStartColumn + categories.Count;
        int sourceIdColumn = explanationColumn + 1;
        int lastVisibleColumn = explanationColumn;

        ws.Cell(1, 1).Value = $"Tax Year: {year}";
        ws.Cell(1, 4).Value = accountTitle;
        ws.Cell(1, categoryStartColumn).Value = "Accounting Categories";

        string[] fixedHeaders =
        [
            "Date",
            "Description",
            "Chk #",
            "Charge",
            "Payment",
            "Balance"
        ];

        for (int i = 0; i < fixedHeaders.Length; i++)
            ws.Cell(2, i + 1).Value = fixedHeaders[i];

        for (int i = 0; i < categories.Count; i++)
            ws.Cell(2, categoryStartColumn + i).Value = categories[i];

        ws.Cell(2, explanationColumn).Value = "Explanation";
        ws.Cell(2, sourceIdColumn).Value = "Source Transaction IDs";

        ws.Cell(3, dateColumn).Value = new DateTime(year, 1, 1);
        ws.Cell(3, descriptionColumn).Value = "Balance forward";
        ws.Cell(3, balanceColumn).Value = CentsToDecimal(openingBalanceCents);

        int rowNumber = 4;

        foreach (AccountingRow row in rows)
        {
            WriteAccountingRow(
                ws,
                rowNumber,
                row,
                categories,
                categoryStartColumn,
                explanationColumn,
                sourceIdColumn,
                overrides);

            if (row.AmountCents < 0)
                ws.Cell(rowNumber, chargeColumn).Value = CentsToDecimal(-row.AmountCents);
            else
                ws.Cell(rowNumber, paymentColumn).Value = CentsToDecimal(row.AmountCents);

            ws.Cell(rowNumber, balanceColumn).FormulaA1 =
                $"F{rowNumber - 1}+E{rowNumber}-D{rowNumber}";

            rowNumber++;
        }

        int lastDataRow = rowNumber - 1;
        int totalsRow = rowNumber;
        int checkRow = totalsRow + 1;

        ws.Cell(totalsRow, 1).Value = "Totals:";
        ws.Cell(totalsRow, chargeColumn).FormulaA1 =
            $"SUM(D3:D{lastDataRow})";
        ws.Cell(totalsRow, paymentColumn).FormulaA1 =
            $"SUM(E3:E{lastDataRow})";
        ws.Cell(totalsRow, balanceColumn).FormulaA1 =
            $"F3+E{totalsRow}-D{totalsRow}";

        var categoryTotalCells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < categories.Count; i++)
        {
            int column = categoryStartColumn + i;
            string letter = ColumnLetter(column);

            ws.Cell(totalsRow, column).FormulaA1 =
                $"SUM({letter}3:{letter}{lastDataRow})";

            categoryTotalCells[categories[i]] =
                QuoteSheetCell(sheetName, letter, totalsRow);
        }

        string firstCreditCategoryLetter =
            ColumnLetter(categoryStartColumn);
        string lastCreditCategoryLetter =
            ColumnLetter(
                categoryStartColumn + creditCategoryCount - 1);
        string firstDebitCategoryLetter =
            ColumnLetter(
                categoryStartColumn + creditCategoryCount);
        string lastDebitCategoryLetter =
            ColumnLetter(
                categoryStartColumn + categories.Count - 1);

        ws.Cell(checkRow, 2).Value = "Credit category check";
        ws.Cell(checkRow, 3).FormulaA1 =
            $"IF(ABS(E{totalsRow}-SUM({firstCreditCategoryLetter}{totalsRow}:{lastCreditCategoryLetter}{totalsRow}))<0.005,\"OK\",\"CR NOT IN BALANCE\")";

        ws.Cell(checkRow, 5).Value = "Debit category check";
        ws.Cell(checkRow, 6).FormulaA1 =
            $"IF(ABS(D{totalsRow}-SUM({firstDebitCategoryLetter}{totalsRow}:{lastDebitCategoryLetter}{totalsRow}))<0.005,\"OK\",\"DR NOT IN BALANCE\")";

        ApplySheetFormatting(
            ws,
            lastDataRow,
            totalsRow,
            checkRow,
            lastVisibleColumn,
            sourceIdColumn,
            categoryStartColumn,
            explanationColumn);

        return new SheetSummary(
            SheetName: sheetName,
            EndingBalanceCell: QuoteSheetCell(sheetName, "F", totalsRow),
            CategoryTotalCells: categoryTotalCells,
            TotalsRow: totalsRow);
    }

    private static void WriteAccountingRow(
        IXLWorksheet ws,
        int rowNumber,
        AccountingRow row,
        IReadOnlyList<string> categories,
        int categoryStartColumn,
        int explanationColumn,
        int sourceIdColumn,
        IReadOnlyDictionary<string, ManualOverride> overrides)
    {
        ws.Cell(rowNumber, 1).Value = row.Date.ToDateTime(TimeOnly.MinValue);
        ws.Cell(rowNumber, 2).Value = row.Description;

        if (!string.IsNullOrWhiteSpace(row.CheckNumber))
            ws.Cell(rowNumber, 3).Value = row.CheckNumber;

        ws.Cell(rowNumber, sourceIdColumn).Value = row.SourceKey;

        decimal accountingAmount = CentsToDecimal(Math.Abs(row.AmountCents));

        if (overrides.TryGetValue(
                MakeOverrideKey(ws.Name, row.SourceKey),
                out ManualOverride? manual) &&
            HasMeaningfulManualOverride(row, manual))
        {
            if (!string.IsNullOrWhiteSpace(manual.Description))
                ws.Cell(rowNumber, 2).Value = manual.Description;

            // An intentionally blank explanation is meaningful for a review row:
            // clearing the generated REVIEW text is how the user can mark it done.
            ws.Cell(rowNumber, explanationColumn).Value = manual.Explanation;

            foreach ((string category, decimal amount) in manual.CategoryAmounts)
            {
                int categoryIndex = IndexOfCategory(categories, category);

                if (categoryIndex >= 0)
                {
                    ws.Cell(rowNumber, categoryStartColumn + categoryIndex).Value = amount;
                }
            }

            if (row.NeedsReview && !IsReviewResolved(row, manual))
                HighlightReviewRow(ws, rowNumber, explanationColumn);

            return;
        }

        if (!string.IsNullOrWhiteSpace(row.Category))
        {
            int categoryIndex = IndexOfCategory(categories, row.Category);

            if (categoryIndex >= 0)
            {
                ws.Cell(rowNumber, categoryStartColumn + categoryIndex).Value = accountingAmount;
            }
        }

        if (row.NeedsReview)
        {
            ws.Cell(rowNumber, explanationColumn).Value = AutoReviewExplanation(row);
            HighlightReviewRow(ws, rowNumber, explanationColumn);
        }
    }

    private static int CountUnresolvedReviewRows(
        string sheetName,
        IReadOnlyList<AccountingRow> rows,
        IReadOnlyDictionary<string, ManualOverride> overrides)
    {
        int count = 0;

        foreach (AccountingRow row in rows)
        {
            if (!row.NeedsReview)
                continue;

            if (!overrides.TryGetValue(
                    MakeOverrideKey(sheetName, row.SourceKey),
                    out ManualOverride? manual) ||
                !IsReviewResolved(row, manual))
            {
                count++;
            }
        }

        return count;
    }

    private static bool HasMeaningfulManualOverride(
        AccountingRow row,
        ManualOverride manual)
    {
        if (!string.IsNullOrWhiteSpace(manual.Description) &&
            !string.Equals(
                manual.Description,
                row.Description,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(
                manual.Explanation ?? string.Empty,
                AutoReviewExplanation(row),
                StringComparison.Ordinal))
        {
            return true;
        }

        decimal expectedAmount = CentsToDecimal(Math.Abs(row.AmountCents));

        if (string.IsNullOrWhiteSpace(row.Category))
            return manual.CategoryAmounts.Count > 0;

        if (manual.CategoryAmounts.Count != 1 ||
            !manual.CategoryAmounts.TryGetValue(row.Category, out decimal amount))
        {
            return true;
        }

        return Math.Abs(amount - expectedAmount) >= 0.005m;
    }

    private static bool IsReviewResolved(
        AccountingRow row,
        ManualOverride manual)
    {
        if (!row.NeedsReview)
            return true;

        // Keep a review open until the user changes/clears the generated REVIEW
        // note and assigns the full amount to one or more accounting categories.
        if (string.Equals(
                manual.Explanation ?? string.Empty,
                AutoReviewExplanation(row),
                StringComparison.Ordinal))
        {
            return false;
        }

        decimal assigned = manual.CategoryAmounts.Values.Sum();
        decimal expected = CentsToDecimal(Math.Abs(row.AmountCents));

        return Math.Abs(assigned - expected) < 0.005m;
    }

    private static string AutoReviewExplanation(AccountingRow row)
    {
        return row.NeedsReview
            ? "REVIEW: " + row.ReviewReason
            : string.Empty;
    }

    private static void HighlightReviewRow(
        IXLWorksheet ws,
        int rowNumber,
        int explanationColumn)
    {
        ws.Cell(rowNumber, 2).Style.Fill.BackgroundColor = XLColor.LightYellow;
        ws.Cell(rowNumber, explanationColumn).Style.Fill.BackgroundColor = XLColor.LightYellow;
    }

    private static void ApplySheetFormatting(
        IXLWorksheet ws,
        int lastDataRow,
        int totalsRow,
        int checkRow,
        int lastVisibleColumn,
        int sourceIdColumn,
        int categoryStartColumn,
        int explanationColumn)
    {
        ws.SheetView.FreezeRows(2);
        ws.SheetView.FreezeColumns(2);

        ws.Range(1, 1, 1, lastVisibleColumn).Style.Font.Bold = true;
        ws.Range(1, 1, 1, lastVisibleColumn).Style.Fill.BackgroundColor =
            XLColor.FromHtml("#D9EAF7");

        ws.Range(2, 1, 2, lastVisibleColumn).Style.Font.Bold = true;
        ws.Range(2, 1, 2, lastVisibleColumn).Style.Fill.BackgroundColor =
            XLColor.FromHtml("#EDEDED");
        ws.Range(2, 1, 2, lastVisibleColumn).Style.Alignment.WrapText = true;
        ws.Range(2, 1, 2, lastVisibleColumn).Style.Alignment.Horizontal =
            XLAlignmentHorizontalValues.Center;

        ws.Range(3, 1, lastDataRow, 1).Style.NumberFormat.Format = "mm/dd/yyyy";
        ws.Range(3, 4, totalsRow, lastVisibleColumn).Style.NumberFormat.Format =
            "$#,##0.00;[Red]-$#,##0.00";

        ws.Range(totalsRow, 1, totalsRow, lastVisibleColumn).Style.Font.Bold = true;
        ws.Range(totalsRow, 1, totalsRow, lastVisibleColumn).Style.Border.TopBorder =
            XLBorderStyleValues.Thin;

        ws.Range(checkRow, 1, checkRow, lastVisibleColumn).Style.Font.Italic = true;
        ws.Range(checkRow, 1, checkRow, lastVisibleColumn).Style.Font.FontColor =
            XLColor.DarkGray;

        ws.Column(1).Width = 12;
        ws.Column(2).Width = 52;
        ws.Column(3).Width = 11;
        ws.Column(4).Width = 13;
        ws.Column(5).Width = 13;
        ws.Column(6).Width = 14;
        ws.Column(7).Width = 3;

        for (int column = categoryStartColumn; column < explanationColumn; column++)
            ws.Column(column).Width = 16;

        ws.Column(explanationColumn).Width = 50;
        ws.Column(sourceIdColumn).Hide();

        ws.Range(3, 2, lastDataRow, 2).Style.Alignment.WrapText = true;
        ws.Range(3, explanationColumn, lastDataRow, explanationColumn)
            .Style.Alignment.WrapText = true;

        ws.Range(2, 1, lastDataRow, lastVisibleColumn).SetAutoFilter();
    }

    private static void BuildTrialBalanceSheet(
        XLWorkbook workbook,
        int year,
        SheetSummary checking,
        SheetSummary savings,
        SheetSummary card)
    {
        IXLWorksheet ws = workbook.Worksheets.Add($"{year} Trial Balances");

        ws.Cell("B1").Value = "Yolanda Solecki LCPC LLC";
        ws.Cell("B2").Value = "Year-to-Date Combined Trial Balance";

        ws.Cell("B3").Value = "Chase Checking";
        ws.Cell("D3").Value = "Chase Savings";
        ws.Cell("F3").Value = "Chase Visa";
        ws.Cell("H3").Value = "Combined";
        ws.Cell("J3").Value = "Income Summary";

        foreach (string cell in new[] { "B4", "D4", "F4", "H4", "J4" })
            ws.Cell(cell).Value = "Dr.";

        foreach (string cell in new[] { "C4", "E4", "G4", "I4", "K4" })
            ws.Cell(cell).Value = "Cr.";

        string[] rowLabels =
        [
            "Chase Checking",
            "Chase Savings",
            "Chase Visa",
            "Transfers In/CC Payments",
            "Owners Draw",
            "Estimated Taxes",
            "Retained",
            "Counselling Fee",
            "Other Revenue",
            "Interest Income",
            "Expense Reimbursements",
            "Accounting Expense",
            "Advertising Expense",
            "Auto Expense",
            "Continuing Education",
            "Insurance - Liability",
            "Insurance - Work Comp",
            "Interest Expense",
            "Legal Expense",
            "License & Dues",
            "Meals & Entertainment",
            "Misc Expense",
            "Office Expense",
            "Rebates",
            "Rent",
            "Software Expense",
            "Telephone",
            "Transfers Out",
            "Totals"
        ];

        for (int i = 0; i < rowLabels.Length; i++)
            ws.Cell(5 + i, 1).Value = rowLabels[i];

        // Account balances.
        ws.Cell("B5").FormulaA1 = $"MAX(0,{checking.EndingBalanceCell})";
        ws.Cell("C5").FormulaA1 = $"MAX(0,-({checking.EndingBalanceCell}))";
        ws.Cell("D6").FormulaA1 = $"MAX(0,{savings.EndingBalanceCell})";
        ws.Cell("E6").FormulaA1 = $"MAX(0,-({savings.EndingBalanceCell}))";
        ws.Cell("F7").FormulaA1 = $"MAX(0,{card.EndingBalanceCell})";
        ws.Cell("G7").FormulaA1 = $"MAX(0,-({card.EndingBalanceCell}))";

        // Credit-side transfers and card payments.
        ws.Cell("C8").FormulaA1 = Ref(checking, "Transfers in");
        ws.Cell("E8").FormulaA1 = Ref(savings, "Transfers In");
        ws.Cell("G8").FormulaA1 = Ref(card, "Credit Card Pmt");

        // Owner draws and estimated taxes.
        ws.Cell("B9").FormulaA1 = Ref(checking, "Owners Draw");
        ws.Cell("D9").FormulaA1 = Ref(savings, "Owners Draw");
        ws.Cell("F9").FormulaA1 = Ref(card, "Owner Draw");

        ws.Cell("B10").FormulaA1 = Ref(checking, "Payroll Taxes");
        ws.Cell("D10").FormulaA1 = Ref(savings, "Payroll Taxes");

        // Retained balances, matching the structure of the existing worksheet.
        ws.Cell("C11").FormulaA1 = checking.EndingBalanceCell;
        ws.Cell("E11").FormulaA1 = savings.EndingBalanceCell;
        ws.Cell("F11").FormulaA1 = card.EndingBalanceCell;

        // Revenue.
        ws.Cell("C12").FormulaA1 = Ref(checking, "Counselling Fee");
        ws.Cell("E12").FormulaA1 = Ref(savings, "Counselling Fee");

        ws.Cell("C13").FormulaA1 = Ref(checking, "Other Revenue");
        ws.Cell("E13").FormulaA1 = Ref(savings, "Other Revenue");
        ws.Cell("G13").FormulaA1 = Ref(card, "Other Revenue");

        ws.Cell("G15").FormulaA1 = Ref(card, "Refunds / Reimbursements");

        // Expenses.
        ws.Cell("B16").FormulaA1 = "0";
        ws.Cell("D16").FormulaA1 = Ref(savings, "Accounting Fee");

        ws.Cell("B17").FormulaA1 = Ref(checking, "Advertising Expense", fallback: "0");
        ws.Cell("D17").FormulaA1 = Ref(savings, "Advertising Expense");
        ws.Cell("F17").FormulaA1 = Ref(card, "Advertising");

        ws.Cell("B18").FormulaA1 = Ref(checking, "Auto Expense");
        ws.Cell("D18").FormulaA1 = Ref(savings, "Auto Expense");
        ws.Cell("F18").FormulaA1 = Ref(card, "Auto Expense");

        ws.Cell("F19").FormulaA1 = Ref(card, "Continuing Ed");

        ws.Cell("B20").FormulaA1 = Ref(checking, "Insurance - Liability");
        ws.Cell("D20").FormulaA1 = Ref(savings, "Insurance - Liability");

        ws.Cell("B21").FormulaA1 = Ref(checking, "Insurance - Work Comp");
        ws.Cell("D21").FormulaA1 = Ref(savings, "Insurance - Work Comp");

        ws.Cell("B22").FormulaA1 = Ref(checking, "Interest Expense");
        ws.Cell("D22").FormulaA1 = Ref(savings, "Interest Expense");
        ws.Cell("F22").FormulaA1 = Ref(card, "Interest Expense");

        ws.Cell("B23").FormulaA1 = Ref(checking, "Legal Expense");
        ws.Cell("D23").FormulaA1 = Ref(savings, "Legal Expense");

        ws.Cell("B24").FormulaA1 = Ref(checking, "Licenses & Dues");
        ws.Cell("D24").FormulaA1 = Ref(savings, "Licenses & Dues");
        ws.Cell("F24").FormulaA1 = Ref(card, "License Fees");

        ws.Cell("B25").FormulaA1 = Ref(checking, "Meals & Entertainment");
        ws.Cell("D25").FormulaA1 = Ref(savings, "Meals & Entertainment");
        ws.Cell("F25").FormulaA1 = Ref(card, "Meals & Entertainment");

        ws.Cell("B26").FormulaA1 = Ref(checking, "Misc. Expense");
        ws.Cell("D26").FormulaA1 = Ref(savings, "Misc. Expense");
        ws.Cell("F26").FormulaA1 = Ref(card, "Misc. Expense");

        ws.Cell("B27").FormulaA1 = Ref(checking, "Office Expense");
        ws.Cell("D27").FormulaA1 = Ref(savings, "Office Expense");
        ws.Cell("F27").FormulaA1 = Ref(card, "Office Expense");

        ws.Cell("B28").FormulaA1 = Ref(checking, "Rebates");

        ws.Cell("B29").FormulaA1 = Ref(checking, "Rent");

        ws.Cell("F30").FormulaA1 = Ref(card, "Software Expense");

        ws.Cell("B31").FormulaA1 = Ref(checking, "Telephone");
        ws.Cell("D31").FormulaA1 = Ref(savings, "Telephone");

        ws.Cell("B32").FormulaA1 =
            Ref(checking, "Credit Card Payment, Transfers out and non-deductible exp");
        ws.Cell("D32").FormulaA1 = Ref(savings, "Transfers Out");

        // Combined columns and income summary.
        for (int row = 5; row <= 32; row++)
        {
            ws.Cell(row, 8).FormulaA1 = $"SUM(B{row},D{row},F{row})";
            ws.Cell(row, 9).FormulaA1 = $"SUM(C{row},E{row},G{row})";
        }

        for (int row = 12; row <= 15; row++)
            ws.Cell(row, 11).FormulaA1 = $"I{row}";

        for (int row = 16; row <= 31; row++)
            ws.Cell(row, 10).FormulaA1 = $"H{row}";

        ws.Cell("B33").FormulaA1 = "SUM(B5:B32)";
        ws.Cell("C33").FormulaA1 = "SUM(C5:C32)";
        ws.Cell("D33").FormulaA1 = "SUM(D5:D32)";
        ws.Cell("E33").FormulaA1 = "SUM(E5:E32)";
        ws.Cell("F33").FormulaA1 = "SUM(F5:F32)";
        ws.Cell("G33").FormulaA1 = "SUM(G5:G32)";
        ws.Cell("H33").FormulaA1 = "SUM(H5:H32)";
        ws.Cell("I33").FormulaA1 = "SUM(I5:I32)";
        ws.Cell("J33").FormulaA1 = "SUM(J5:J32)";
        ws.Cell("K33").FormulaA1 = "SUM(K5:K32)";

        ws.Cell("A34").Value = "Net income";
        ws.Cell("J34").FormulaA1 = "K33-J33";
        ws.Cell("A35").Value = "Income summary check";
        ws.Cell("J35").FormulaA1 = "J33+J34";
        ws.Cell("K35").FormulaA1 = "K33";

        ws.Range("A1:K4").Style.Font.Bold = true;
        ws.Range("A3:K4").Style.Fill.BackgroundColor = XLColor.FromHtml("#EDEDED");
        ws.Range("B5:K35").Style.NumberFormat.Format = "$#,##0.00;[Red]-$#,##0.00";
        ws.Range("A33:K33").Style.Font.Bold = true;
        ws.Range("A33:K33").Style.Border.TopBorder = XLBorderStyleValues.Thin;
        ws.Column(1).Width = 30;

        for (int column = 2; column <= 11; column++)
            ws.Column(column).Width = 14;

        ws.SheetView.FreezeRows(4);
        ws.SheetView.FreezeColumns(1);
    }

    private static string Ref(
        SheetSummary summary,
        string category,
        string fallback = "0")
    {
        return summary.CategoryTotalCells.TryGetValue(category, out string? cell)
            ? cell
            : fallback;
    }

    private static Dictionary<string, ManualOverride> ReadManualOverrides(
        string workbookPath)
    {
        var result = new Dictionary<string, ManualOverride>(StringComparer.Ordinal);

        try
        {
            using var workbook = new XLWorkbook(workbookPath);

            foreach (IXLWorksheet ws in workbook.Worksheets)
            {
                IXLRow headerRow = ws.Row(2);
                int sourceIdColumn = FindHeaderColumn(headerRow, "Source Transaction IDs");
                int descriptionColumn = FindHeaderColumn(headerRow, "Description");
                int explanationColumn = FindHeaderColumn(headerRow, "Explanation");

                if (sourceIdColumn <= 0 || descriptionColumn <= 0)
                    continue;

                int lastRow = ws.LastRowUsed()?.RowNumber() ?? 2;
                int lastColumn = ws.LastColumnUsed()?.ColumnNumber() ?? sourceIdColumn;

                var categoryColumns = new Dictionary<int, string>();

                for (int column = 1; column <= lastColumn; column++)
                {
                    string header = ws.Cell(2, column).GetString().Trim();

                    if (string.IsNullOrWhiteSpace(header) ||
                        header is "Date" or "Description" or "Chk #" or
                        "Dr." or "Cr." or "Charge" or "Payment" or
                        "Balance" or "Explanation" or "Source Transaction IDs")
                    {
                        continue;
                    }

                    categoryColumns[column] = header;
                }

                for (int row = 3; row <= lastRow; row++)
                {
                    string sourceKey = ws.Cell(row, sourceIdColumn).GetString().Trim();

                    if (string.IsNullOrWhiteSpace(sourceKey))
                        continue;

                    var amounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

                    foreach ((int column, string category) in categoryColumns)
                    {
                        IXLCell cell = ws.Cell(row, column);

                        if (cell.TryGetValue(out decimal amount) && amount != 0)
                            amounts[category] = amount;
                    }

                    result[MakeOverrideKey(ws.Name, sourceKey)] = new ManualOverride(
                        Description: ws.Cell(row, descriptionColumn).GetString(),
                        Explanation: explanationColumn > 0
                            ? ws.Cell(row, explanationColumn).GetString()
                            : string.Empty,
                        CategoryAmounts: amounts);
                }
            }
        }
        catch
        {
            // An existing workbook may be an older template without our hidden IDs,
            // may be open/locked, or may not be a valid workbook. Generation still
            // succeeds; it simply cannot preserve manual overrides from that file.
        }

        return result;
    }

    private static int FindHeaderColumn(
        IXLRow headerRow,
        string header)
    {
        IXLCell? cell = headerRow.CellsUsed()
            .FirstOrDefault(cell => string.Equals(
                cell.GetString().Trim(),
                header,
                StringComparison.OrdinalIgnoreCase));

        return cell?.Address.ColumnNumber ?? -1;
    }

    private static void OpenWorkbook(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static ClassifiedTransaction Auto(
        string groupKey,
        string description,
        string category,
        bool aggregate)
    {
        return new ClassifiedTransaction(
            groupKey,
            description,
            category,
            ReviewReason: null,
            Aggregate: aggregate);
    }

    private static ClassifiedTransaction Review(
        string groupKey,
        string description,
        string reason,
        bool aggregate)
    {
        return new ClassifiedTransaction(
            groupKey,
            description,
            Category: null,
            ReviewReason: reason,
            Aggregate: aggregate);
    }

    private static int IndexOfCategory(
        IReadOnlyList<string> categories,
        string category)
    {
        for (int i = 0; i < categories.Count; i++)
        {
            if (string.Equals(
                    categories[i],
                    category,
                    StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string FriendlyCompanyName(string company)
    {
        if (string.IsNullOrWhiteSpace(company))
            return "Insurance";

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            company.Trim().ToLowerInvariant());
    }

    private static string MakeOverrideKey(
        string sheetName,
        string sourceKey)
    {
        return sheetName + "|" + sourceKey;
    }

    private static string ColumnLetter(int columnNumber)
    {
        if (columnNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(columnNumber));

        string result = string.Empty;

        while (columnNumber > 0)
        {
            columnNumber--;
            result = (char)('A' + (columnNumber % 26)) + result;
            columnNumber /= 26;
        }

        return result;
    }

    private static string QuoteSheetCell(
        string sheetName,
        string column,
        int row)
    {
        return $"'{sheetName.Replace("'", "''")}'!${column}${row}";
    }

    private static decimal CentsToDecimal(long cents) => cents / 100m;

    private static long ToUnixMidnight(DateOnly date)
    {
        return new DateTimeOffset(
            date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            .ToUnixTimeSeconds();
    }

    private static DateOnly FromUnixMidnight(long unixSeconds)
    {
        return DateOnly.FromDateTime(
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime);
    }

    private static string? GetNullableString(
        SqliteDataReader reader,
        int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : reader.GetString(ordinal);
    }

    private static long? GetNullableInt64(
        SqliteDataReader reader,
        int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : reader.GetInt64(ordinal);
    }

    private sealed record DepositSourceRow(
        long TransactionId,
        DateOnly PostingDate,
        long AmountCents,
        string TypeCode,
        long? BalanceCents,
        string? CheckOrSlipNumber,
        string? AchCompanyName,
        string? AchEntryDescription,
        string? TransferDirection,
        string? TransferInstitution,
        string? TransferAccountLabel,
        string? TransferLast4,
        string? TargetCardLast4,
        string? DebitMerchant,
        string? AtmAction,
        string? AtmLocation,
        string? FeeDescription,
        string? RealTimeSender,
        string? RealTimePurpose,
        string? UnparsedDescription,
        int SourceRowNumber);

    private sealed record CreditCardSourceRow(
        long TransactionId,
        DateOnly PostingDate,
        DateOnly TransactionDate,
        long AmountCents,
        string TypeCode,
        string MerchantName,
        string? ChaseCategory,
        string? Memo,
        int SourceRowNumber);

    private sealed record ClassifiedTransaction(
        string GroupKey,
        string Description,
        string? Category,
        string? ReviewReason,
        bool Aggregate);

    private sealed record AccountingRow(
        string SourceKey,
        DateOnly Date,
        string Description,
        string? CheckNumber,
        long AmountCents,
        string? Category,
        string? ReviewReason)
    {
        public bool NeedsReview => !string.IsNullOrWhiteSpace(ReviewReason);
    }

    private sealed class MutableAccountingRow
    {
        public MutableAccountingRow(
            DateOnly date,
            string description,
            string? checkNumber,
            string? category,
            string? reviewReason,
            int ordinal)
        {
            Date = date;
            Description = description;
            CheckNumber = checkNumber;
            Category = category;
            ReviewReason = reviewReason;
            Ordinal = ordinal;
        }

        public DateOnly Date { get; }
        public string Description { get; }
        public string? CheckNumber { get; }
        public string? Category { get; }
        public string? ReviewReason { get; }
        public int Ordinal { get; }
        public long AmountCents { get; set; }
        public List<long> SourceTransactionIds { get; } = [];

        public AccountingRow ToImmutable()
        {
            string sourceKey = string.Join(
                ",",
                SourceTransactionIds
                    .OrderBy(id => id)
                    .Select(id => id.ToString(CultureInfo.InvariantCulture)));

            return new AccountingRow(
                sourceKey,
                Date,
                Description,
                CheckNumber,
                AmountCents,
                Category,
                ReviewReason);
        }
    }

    private sealed record ManualOverride(
        string Description,
        string Explanation,
        IReadOnlyDictionary<string, decimal> CategoryAmounts);

    private sealed record SheetSummary(
        string SheetName,
        string EndingBalanceCell,
        IReadOnlyDictionary<string, string> CategoryTotalCells,
        int TotalsRow);
}

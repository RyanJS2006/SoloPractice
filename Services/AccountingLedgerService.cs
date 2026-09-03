using Microsoft.Data.Sqlite;
using SoloPractice.Data;
using System.Globalization;

namespace SoloPractice.Services;

internal sealed record AccountingGenerationResult(
    int EntriesCreated,
    int SourceTransactionsLinked,
    int OpeningBalancesCreated);

internal sealed record AccountingCategory(
    long Id,
    string Name,
    int DisplayOrder,
    string? NormalSide,
    string? StatementGroup);

internal sealed record AccountingEntry(
    long Id,
    long AccountId,
    string AccountLast4,
    string AccountName,
    DateOnly Date,
    long AmountCents,
    string Description,
    string? Explanation,
    string? Category,
    string? CheckNumber,
    int DisplayOrder,
    bool IsManual,
    bool NeedsReview,
    bool IsOpeningBalance,
    string SourceTransactionIds);

internal static class AccountingLedgerService
{
    public static AccountingGenerationResult GenerateMissingEntries(int year)
    {
        ValidateYear(year);

        using SqliteConnection connection = Database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        int openingsCreated = EnsureOpeningBalances(connection, transaction, year);

        List<AccountingSourceTransaction> source =
            ReadUnlinkedSourceTransactions(connection, transaction, year);

        var groups = new Dictionary<string, PendingEntry>(StringComparer.Ordinal);
        int ordinal = 0;

        foreach (AccountingSourceTransaction row in source)
        {
            AccountingClassification classification = AccountingClassifier.Classify(row);
            string key = classification.Aggregate
                ? $"{row.AccountId}|{row.AccountingDate:yyyy-MM-dd}|{classification.GroupKey}"
                : $"{row.AccountId}|{row.TransactionId}";

            if (!groups.TryGetValue(key, out PendingEntry? pending))
            {
                pending = new PendingEntry(
                    row.AccountId,
                    row.AccountingDate,
                    classification.Description,
                    classification.Category,
                    classification.ReviewReason,
                    row.CheckNumber,
                    ordinal++);
                groups.Add(key, pending);
            }

            pending.AmountCents += row.AmountCents;
            pending.TransactionIds.Add(row.TransactionId);
        }

        int created = 0;
        int linked = 0;
        var nextOrders = new Dictionary<long, int>();

        foreach (PendingEntry pending in groups.Values
                     .OrderBy(value => value.Date)
                     .ThenBy(value => value.Ordinal))
        {
            if (!nextOrders.TryGetValue(pending.AccountId, out int displayOrder))
            {
                displayOrder = ReadNextDisplayOrder(
                    connection,
                    transaction,
                    pending.AccountId,
                    year);
            }

            long entryId = InsertEntry(
                connection,
                transaction,
                pending,
                displayOrder);
            nextOrders[pending.AccountId] = displayOrder + 1;
            created++;

            foreach (long transactionId in pending.TransactionIds)
            {
                using SqliteCommand link = connection.CreateCommand();
                link.Transaction = transaction;
                link.CommandText = """
                    INSERT INTO AccountingEntryTransactions
                        (AccountingEntryId, TransactionId)
                    VALUES ($entry, $transaction);
                    """;
                link.Parameters.AddWithValue("$entry", entryId);
                link.Parameters.AddWithValue("$transaction", transactionId);
                link.ExecuteNonQuery();
                linked++;
            }
        }

        transaction.Commit();
        return new AccountingGenerationResult(created + openingsCreated, linked, openingsCreated);
    }

    public static IReadOnlyList<AccountingEntry> ReadEntries(int year)
    {
        ValidateYear(year);
        using SqliteConnection connection = Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                e.Id,
                e.AccountId,
                a.Last4,
                a.Name,
                d.UnixSeconds,
                m.Cents,
                description.Value,
                explanation.Value,
                category.Name,
                checkNumber.Number,
                e.DisplayOrder,
                e.IsManual,
                e.NeedsReview,
                e.IsOpeningBalance,
                COALESCE
                (
                    (
                        SELECT group_concat(x.TransactionId, ',')
                        FROM
                        (
                            SELECT aet.TransactionId
                            FROM AccountingEntryTransactions aet
                            WHERE aet.AccountingEntryId = e.Id
                            ORDER BY aet.TransactionId
                        ) x
                    ),
                    ''
                )
            FROM AccountingEntries e
            JOIN Accounts a ON a.Id = e.AccountId
            JOIN AccountingDateValues d ON d.Id = e.EntryDateId
            JOIN AccountingMoneyValues m ON m.Id = e.AmountId
            JOIN AccountingTextValues description
              ON description.Id = e.DescriptionTextId
             AND description.Kind = 'DESCRIPTION'
            LEFT JOIN AccountingTextValues explanation
              ON explanation.Id = e.ExplanationTextId
             AND explanation.Kind = 'EXPLANATION'
            LEFT JOIN AccountingCategories category ON category.Id = e.CategoryId
            LEFT JOIN AccountingCheckNumbers checkNumber ON checkNumber.Id = e.CheckNumberId
            WHERE d.UnixSeconds >= $start
              AND d.UnixSeconds < $end
              AND e.IsSuppressed = 0
            ORDER BY a.Id, d.UnixSeconds, e.DisplayOrder, e.Id;
            """;
        AddYearParameters(command, year);

        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<AccountingEntry>();

        while (reader.Read())
        {
            result.Add(new AccountingEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                FromUnixMidnight(reader.GetInt64(4)),
                reader.GetInt64(5),
                reader.GetString(6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                GetNullableString(reader, 9),
                reader.GetInt32(10),
                reader.GetInt64(11) != 0,
                reader.GetInt64(12) != 0,
                reader.GetInt64(13) != 0,
                reader.GetString(14)));
        }

        return result;
    }

    public static IReadOnlyList<AccountingCategory> ReadCategories()
    {
        using SqliteConnection connection = Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, DisplayOrder, NormalSide, StatementGroup
            FROM AccountingCategories
            WHERE IsActive = 1
            ORDER BY DisplayOrder, Name COLLATE NOCASE, Id;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<AccountingCategory>();
        while (reader.Read())
        {
            result.Add(new AccountingCategory(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4)));
        }

        return result;
    }

    public static IReadOnlyDictionary<(string AccountLast4, string Category), long>
        ReadCategoryTotals(int year)
    {
        ValidateYear(year);
        using SqliteConnection connection = Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Last4, c.Name, SUM(abs(m.Cents))
            FROM AccountingEntries e
            JOIN Accounts a ON a.Id = e.AccountId
            JOIN AccountingDateValues d ON d.Id = e.EntryDateId
            JOIN AccountingMoneyValues m ON m.Id = e.AmountId
            JOIN AccountingCategories c ON c.Id = e.CategoryId
            WHERE d.UnixSeconds >= $start
              AND d.UnixSeconds < $end
              AND e.IsSuppressed = 0
            GROUP BY a.Last4, c.Id, c.Name;
            """;
        AddYearParameters(command, year);

        using SqliteDataReader reader = command.ExecuteReader();
        var result = new Dictionary<(string, string), long>();
        while (reader.Read())
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
        return result;
    }

    public static IReadOnlyDictionary<string, long> ReadOpeningBalances(int year)
    {
        ValidateYear(year);
        using SqliteConnection connection = Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Last4, COALESCE(SUM(m.Cents), 0)
            FROM Accounts a
            LEFT JOIN AccountingEntries e
              ON e.AccountId = a.Id
             AND e.IsSuppressed = 0
            LEFT JOIN AccountingDateValues d
              ON d.Id = e.EntryDateId
             AND d.UnixSeconds < $start
            LEFT JOIN AccountingMoneyValues m
              ON m.Id = e.AmountId
             AND d.Id IS NOT NULL
            GROUP BY a.Id, a.Last4;
            """;
        command.Parameters.AddWithValue("$start", ToUnixMidnight(new DateOnly(year, 1, 1)));

        using SqliteDataReader reader = command.ExecuteReader();
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    private static List<AccountingSourceTransaction> ReadUnlinkedSourceTransactions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int year)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH LatestImports AS
            (
                SELECT f.AccountId, MAX(f.Id) AS ImportFileId
                FROM ImportFiles f
                JOIN DateValues downloadDate ON downloadDate.Id = f.DownloadDateId
                WHERE f.Id =
                (
                    SELECT f2.Id
                    FROM ImportFiles f2
                    JOIN DateValues d2 ON d2.Id = f2.DownloadDateId
                    WHERE f2.AccountId = f.AccountId
                    ORDER BY d2.UnixSeconds DESC, f2.Id DESC
                    LIMIT 1
                )
                GROUP BY f.AccountId
            ),
            LatestRows AS
            (
                SELECT ir.TransactionId, ir.SourceRowNumber
                FROM ImportRows ir
                JOIN LatestImports latest ON latest.ImportFileId = ir.ImportFileId
            )
            SELECT
                t.Id,
                t.AccountId,
                account.Last4,
                CASE WHEN account.Last4 = '8027'
                     THEN transactionDate.UnixSeconds
                     ELSE postingDate.UnixSeconds END,
                amount.Cents,
                type.Code,
                deposit.CheckOrSlipNumber,
                originator.CompanyName,
                entryDescription.Description,
                direction.Name,
                counterparty.Institution,
                counterparty.AccountLabel,
                counterparty.Last4,
                target.Last4,
                debit.MerchantDescriptor,
                atm.Action,
                atm.Location,
                fee.Description,
                realtime.Sender,
                realtime.Purpose,
                unparsed.Description,
                merchant.Name,
                cardCategory.Name,
                card.Memo,
                COALESCE(latestRow.SourceRowNumber, 0)
            FROM Transactions t
            JOIN Accounts account ON account.Id = t.AccountId
            JOIN DateValues postingDate ON postingDate.Id = t.PostingDateId
            JOIN MoneyValues amount ON amount.Id = t.AmountId
            JOIN TransactionTypes type ON type.Id = t.TypeId
            LEFT JOIN DepositTransactions deposit ON deposit.TransactionId = t.Id
            LEFT JOIN AchTransactions ach ON ach.TransactionId = t.Id
            LEFT JOIN AchOriginators originator ON originator.Id = ach.OriginatorId
            LEFT JOIN AchEntryDescriptions entryDescription ON entryDescription.Id = ach.EntryDescriptionId
            LEFT JOIN AccountTransfers transfer ON transfer.TransactionId = t.Id
            LEFT JOIN TransferDirections direction ON direction.Id = transfer.DirectionId
            LEFT JOIN TransferCounterparties counterparty ON counterparty.Id = transfer.CounterpartyId
            LEFT JOIN ChaseCardPayments cardPayment ON cardPayment.TransactionId = t.Id
            LEFT JOIN Accounts target ON target.Id = cardPayment.TargetAccountId
            LEFT JOIN DebitCardTransactions debit ON debit.TransactionId = t.Id
            LEFT JOIN AtmTransactions atm ON atm.TransactionId = t.Id
            LEFT JOIN FeeTransactions fee ON fee.TransactionId = t.Id
            LEFT JOIN RealTimePayments realtime ON realtime.TransactionId = t.Id
            LEFT JOIN UnparsedDepositDescriptions unparsed ON unparsed.TransactionId = t.Id
            LEFT JOIN CreditCardTransactions card ON card.TransactionId = t.Id
            LEFT JOIN DateValues transactionDate ON transactionDate.Id = card.TransactionDateId
            LEFT JOIN CreditCardMerchants merchant ON merchant.Id = card.MerchantId
            LEFT JOIN CreditCardCategories cardCategory ON cardCategory.Id = card.CategoryId
            LEFT JOIN LatestRows latestRow ON latestRow.TransactionId = t.Id
            LEFT JOIN AccountingEntryTransactions linked ON linked.TransactionId = t.Id
            WHERE linked.TransactionId IS NULL
              AND CASE WHEN account.Last4 = '8027'
                       THEN transactionDate.UnixSeconds
                       ELSE postingDate.UnixSeconds END >= $start
              AND CASE WHEN account.Last4 = '8027'
                       THEN transactionDate.UnixSeconds
                       ELSE postingDate.UnixSeconds END < $end
            ORDER BY t.AccountId, 4, latestRow.SourceRowNumber DESC, t.Id DESC;
            """;
        AddYearParameters(command, year);

        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<AccountingSourceTransaction>();
        while (reader.Read())
        {
            result.Add(new AccountingSourceTransaction(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                FromUnixMidnight(reader.GetInt64(3)),
                reader.GetInt64(4),
                reader.GetString(5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                GetNullableString(reader, 9),
                GetNullableString(reader, 10),
                GetNullableString(reader, 11),
                GetNullableString(reader, 12),
                GetNullableString(reader, 13),
                GetNullableString(reader, 14),
                GetNullableString(reader, 15),
                GetNullableString(reader, 16),
                GetNullableString(reader, 17),
                GetNullableString(reader, 18),
                GetNullableString(reader, 19),
                GetNullableString(reader, 20),
                GetNullableString(reader, 21),
                GetNullableString(reader, 22),
                GetNullableString(reader, 23),
                reader.GetInt32(24)));
        }

        return result;
    }

    private static int EnsureOpeningBalances(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int year)
    {
        int created = 0;
        foreach (string last4 in new[]
                 {
                     AccountingClassifier.CheckingAccount,
                     AccountingClassifier.SavingsAccount,
                     AccountingClassifier.CreditCardAccount
                 })
        {
            using SqliteCommand accountCommand = connection.CreateCommand();
            accountCommand.Transaction = transaction;
            accountCommand.CommandText = "SELECT Id FROM Accounts WHERE Last4 = $last4;";
            accountCommand.Parameters.AddWithValue("$last4", last4);
            long accountId = Convert.ToInt64(accountCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            long dateId = AccountingLookup.GetOrCreateDate(connection, transaction, new DateOnly(year, 1, 1));

            using SqliteCommand exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = """
                SELECT EXISTS
                (
                    SELECT 1
                    FROM AccountingEntries
                    WHERE AccountId = $account
                      AND EntryDateId = $date
                      AND IsOpeningBalance = 1
                );
                """;
            exists.Parameters.AddWithValue("$account", accountId);
            exists.Parameters.AddWithValue("$date", dateId);
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                continue;

            long openingCents = last4 == AccountingClassifier.CreditCardAccount
                ? 0
                : ReadImportedOpeningBalance(connection, transaction, last4, year);
            long amountId = AccountingLookup.GetOrCreateMoney(connection, transaction, openingCents);
            long descriptionId = AccountingLookup.GetOrCreateText(connection, transaction, "DESCRIPTION", "Balance forward");
            string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO AccountingEntries
                (
                    AccountId, EntryDateId, AmountId, DescriptionTextId,
                    ExplanationTextId, CategoryId, CheckNumberId, DisplayOrder,
                    IsOpeningBalance, IsManual, NeedsReview, IsSuppressed,
                    CreatedAtUtc, ModifiedAtUtc
                )
                VALUES
                (
                    $account, $date, $amount, $description,
                    NULL, NULL, NULL, 0,
                    1, 0, 0, 0,
                    $now, $now
                );
                """;
            insert.Parameters.AddWithValue("$account", accountId);
            insert.Parameters.AddWithValue("$date", dateId);
            insert.Parameters.AddWithValue("$amount", amountId);
            insert.Parameters.AddWithValue("$description", descriptionId);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
            created++;
        }

        return created;
    }

    private static long ReadImportedOpeningBalance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountLast4,
        int year)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH LatestImport AS
            (
                SELECT f.Id
                FROM ImportFiles f
                JOIN Accounts a ON a.Id = f.AccountId
                JOIN DateValues d ON d.Id = f.DownloadDateId
                WHERE a.Last4 = $last4
                ORDER BY d.UnixSeconds DESC, f.Id DESC
                LIMIT 1
            ),
            LatestRows AS
            (
                SELECT TransactionId, SourceRowNumber
                FROM ImportRows
                WHERE ImportFileId = (SELECT Id FROM LatestImport)
            )
            SELECT balance.Cents - amount.Cents
            FROM Transactions t
            JOIN Accounts account ON account.Id = t.AccountId
            JOIN DateValues postingDate ON postingDate.Id = t.PostingDateId
            JOIN MoneyValues amount ON amount.Id = t.AmountId
            JOIN DepositTransactions deposit ON deposit.TransactionId = t.Id
            JOIN MoneyValues balance ON balance.Id = deposit.BalanceAmountId
            LEFT JOIN LatestRows latest ON latest.TransactionId = t.Id
            WHERE account.Last4 = $last4
              AND postingDate.UnixSeconds >= $start
              AND postingDate.UnixSeconds < $end
            ORDER BY postingDate.UnixSeconds, COALESCE(latest.SourceRowNumber, 0) DESC, t.Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$last4", accountLast4);
        AddYearParameters(command, year);
        object? result = command.ExecuteScalar();
        return result is null || result is DBNull
            ? 0
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static long InsertEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PendingEntry pending,
        int displayOrder)
    {
        long dateId = AccountingLookup.GetOrCreateDate(connection, transaction, pending.Date);
        long moneyId = AccountingLookup.GetOrCreateMoney(connection, transaction, pending.AmountCents);
        long descriptionId = AccountingLookup.GetOrCreateText(connection, transaction, "DESCRIPTION", pending.Description);
        long? explanationId = string.IsNullOrWhiteSpace(pending.ReviewReason)
            ? null
            : AccountingLookup.GetOrCreateText(connection, transaction, "EXPLANATION", "REVIEW: " + pending.ReviewReason);
        long? categoryId = string.IsNullOrWhiteSpace(pending.Category)
            ? null
            : AccountingLookup.GetOrCreateCategory(connection, transaction, pending.Category, out _);
        long? checkId = string.IsNullOrWhiteSpace(pending.CheckNumber)
            ? null
            : AccountingLookup.GetOrCreateCheckNumber(connection, transaction, pending.CheckNumber);
        string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AccountingEntries
            (
                AccountId, EntryDateId, AmountId, DescriptionTextId,
                ExplanationTextId, CategoryId, CheckNumberId, DisplayOrder,
                IsOpeningBalance, IsManual, NeedsReview, IsSuppressed, CreatedAtUtc, ModifiedAtUtc
            )
            VALUES
            (
                $account, $date, $amount, $description,
                $explanation, $category, $check, $order,
                0, 0, $review, 0, $now, $now
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$account", pending.AccountId);
        command.Parameters.AddWithValue("$date", dateId);
        command.Parameters.AddWithValue("$amount", moneyId);
        command.Parameters.AddWithValue("$description", descriptionId);
        command.Parameters.AddWithValue("$explanation", (object?)explanationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$check", (object?)checkId ?? DBNull.Value);
        command.Parameters.AddWithValue("$order", displayOrder);
        command.Parameters.AddWithValue("$review", pending.ReviewReason is null ? 0 : 1);
        command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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
        AddYearParameters(command, year);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static void AddYearParameters(SqliteCommand command, int year)
    {
        command.Parameters.AddWithValue("$start", ToUnixMidnight(new DateOnly(year, 1, 1)));
        command.Parameters.AddWithValue("$end", ToUnixMidnight(new DateOnly(year + 1, 1, 1)));
    }

    internal static long ToUnixMidnight(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds();

    internal static DateOnly FromUnixMidnight(long unixSeconds) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void ValidateYear(int year)
    {
        if (year < 2000 || year >= 9999)
            throw new ArgumentOutOfRangeException(nameof(year));
    }

    private sealed class PendingEntry
    {
        public PendingEntry(
            long accountId,
            DateOnly date,
            string description,
            string? category,
            string? reviewReason,
            string? checkNumber,
            int ordinal)
        {
            AccountId = accountId;
            Date = date;
            Description = description;
            Category = category;
            ReviewReason = reviewReason;
            CheckNumber = checkNumber;
            Ordinal = ordinal;
        }

        public long AccountId { get; }
        public DateOnly Date { get; }
        public string Description { get; }
        public string? Category { get; }
        public string? ReviewReason { get; }
        public string? CheckNumber { get; }
        public int Ordinal { get; }
        public long AmountCents { get; set; }
        public List<long> TransactionIds { get; } = [];
    }
}

internal static class AccountingLookup
{
    public static long GetOrCreateDate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly date) =>
        GetOrCreateInt64(connection, transaction, "AccountingDateValues", "UnixSeconds", AccountingLedgerService.ToUnixMidnight(date));

    public static long GetOrCreateMoney(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long cents) =>
        GetOrCreateInt64(connection, transaction, "AccountingMoneyValues", "Cents", cents);

    public static long GetOrCreateText(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string kind,
        string value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AccountingTextValues (Kind, Value)
            VALUES ($kind, $value)
            ON CONFLICT (Kind, Value) DO NOTHING;
            SELECT Id FROM AccountingTextValues WHERE Kind = $kind AND Value = $value;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$value", value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static long GetOrCreateCategory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        out bool inserted)
    {
        string normalized = NormalizeCategoryName(name);

        using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT Id FROM AccountingCategories WHERE Name = $name COLLATE NOCASE;";
            select.Parameters.AddWithValue("$name", normalized);
            object? existing = select.ExecuteScalar();
            if (existing is not null)
            {
                inserted = false;
                return Convert.ToInt64(existing, CultureInfo.InvariantCulture);
            }
        }

        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO AccountingCategories
                (Name, DisplayOrder, IsActive, NormalSide, StatementGroup)
            VALUES
                ($name, (SELECT COALESCE(MAX(DisplayOrder), 0) + 10 FROM AccountingCategories), 1, NULL, NULL);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$name", normalized);
        inserted = true;
        return Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static long GetOrCreateCheckNumber(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string number)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AccountingCheckNumbers (Number)
            VALUES ($number)
            ON CONFLICT (Number) DO NOTHING;
            SELECT Id FROM AccountingCheckNumbers WHERE Number = $number;
            """;
        command.Parameters.AddWithValue("$number", number.Trim());
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long GetOrCreateInt64(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        long value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {table} ({column}) VALUES ($value) ON CONFLICT ({column}) DO NOTHING; " +
            $"SELECT Id FROM {table} WHERE {column} = $value;";
        command.Parameters.AddWithValue("$value", value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string NormalizeCategoryName(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Equals("Transfers in", StringComparison.OrdinalIgnoreCase))
            return "Transfers In";
        if (trimmed.Equals("Owner Draw", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Owners Draw", StringComparison.OrdinalIgnoreCase))
            return "Owners Draw";
        return trimmed;
    }
}

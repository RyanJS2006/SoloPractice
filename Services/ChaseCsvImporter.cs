using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.Sqlite;
using SoloPractice.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SoloPractice.Services;

internal sealed record ChaseImportResult(
    string FileName,
    string AccountLast4,
    string? FormatName,
    int RowsRead,
    int NewTransactions,
    int ReusedTransactions,
    int UnparsedDescriptions,
    bool FileAlreadyImported);

internal static class ChaseCsvImporter
{
    private static readonly string[] CreditCardHeader =
    [
        "Card",
        "Transaction Date",
        "Post Date",
        "Description",
        "Category",
        "Type",
        "Amount",
        "Memo"
    ];

    private static readonly string[] DepositHeader =
    [
        "Details",
        "Posting Date",
        "Description",
        "Amount",
        "Type",
        "Balance",
        "Check or Slip #"
    ];

    private static readonly Regex ChaseFileNameRegex = new(
        @"^Chase(?<last4>\d{4})_Activity_(?<date>\d{8})(?:\s*\(\d+\))?\.csv$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FullAchRegex = new(
        @"^ORIG CO NAME:(?<company>.*?)\s+ORIG ID:(?<originatorId>.*?)\s+" +
        @"DESC DATE:(?<descriptiveDate>.*?)\s+CO ENTRY DESCR:(?<entryDescription>.*?)\s*" +
        @"SEC:(?<sec>\S+)\s+TRACE#:(?<trace>\S+)\s+EED:(?<eed>\S+)\s+" +
        @"IND ID:(?<individualId>.*?)\s+IND NAME:(?<tail>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ShortAchRegex = new(
        @"^ORIG CO NAME:(?<company>.*?)\s+CO ENTRY DESCR:(?<entryDescription>.*?)\s+" +
        @"SEC:(?<sec>\S+)\s+IND ID:(?<individualId>.*?)\s+ORIG ID:(?<originatorId>\S+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AchBankReferenceRegex = new(
        @"\s+(?<kind>PAYABLE TRN|EDI TRN|TRN):\s*(?<reference>\S+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RealtimeTransferRegex = new(
        @"^Online Realtime Transfer to Personal Checking Acct\s+(?<last4>\d{4}) " +
        @"transaction#:\s*(?<transaction>\d+) reference#:\s*(?<reference>\S+) (?<monthDay>\d{2}/\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ToMmaTransferRegex = new(
        @"^Online Transfer to MMA \.\.\.(?<last4>\d{4}) transaction#:\s*(?<transaction>\d+) (?<monthDay>\d{2}/\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FromChkTransferRegex = new(
        @"^Online Transfer from CHK \.\.\.(?<last4>\d{4}) transaction#:\s*(?<transaction>\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NamedTransferRegex = new(
        @"^Online Transfer (?<leading>\d+) to (?<name>.+?) (?<mask>#+)(?<last4>\d{4}) " +
        @"transaction #:\s*(?<transaction>\d+) (?<monthDay>\d{2}/\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ChaseCardPaymentRegex = new(
        @"^Payment to Chase card ending in (?<last4>\d{4}) (?<monthDay>\d{2}/\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CheckPaidRegex = new(
        @"^CHECK\s+(?<number>\d+)\s*(?<monthDay>\d{2}/\d{2})?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RemoteDepositRegex = new(
        @"^REMOTE ONLINE DEPOSIT #\s*(?<number>\d+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DebitCardRegex = new(
        @"^(?<merchant>.*?)\s+(?<monthDay>\d{2}/\d{2})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AtmWithdrawalRegex = new(
        @"^ATM WITHDRAWAL\s+(?<terminal>\d+)\s+(?<monthDay>\d{2}/\d{2})(?<location>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AtmDepositRegex = new(
        @"^ATM CASH DEPOSIT (?<monthDay>\d{2}/\d{2}) (?<location>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RealTimePaymentRegex = new(
        @"^REAL TIME PAYMENT CREDIT RECD FROM ABA/CONTR BNK-(?<aba>\d+)\s+" +
        @"FROM:\s*(?<sender>.*?)\s+REF:\s*(?<reference>\S+)\s+INFO:\s*(?<info>.*?)\s+" +
        @"IID:\s*(?<iid>\S+)\s+RECD:\s*(?<time>\d{2}:\d{2}:\d{2})\s+TRN:\s*(?<bankReference>\S+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RealTimePaymentInfoRegex = new(
        @"^TEXT-RmtInf-TRN\*1\*(?<reference>[^*~]+)\*(?<originatorId>[^*~]+)\*(?<paymentCode>[^~]+)~" +
        @"(?:(?:TIN(?<tin>[^*]+)\*NPI(?<npi>[^*]+)\*(?<receiverName>[^*]+)\*(?<purpose>.+))?)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ChaseImportResult Import(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("No CSV path was supplied.", nameof(filePath));

        filePath = Path.GetFullPath(filePath.Trim().Trim('"'));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("The Chase CSV file does not exist.", filePath);

        FileIdentity fileIdentity = ParseFileIdentity(Path.GetFileName(filePath));
        byte[] fileSha256 = ComputeSha256(filePath);

        using var connection = Database.OpenConnection();

        if (ImportFileAlreadyExists(connection, fileSha256))
        {
            return new ChaseImportResult(
                Path.GetFileName(filePath),
                fileIdentity.AccountLast4,
                null,
                0,
                0,
                0,
                0,
                true);
        }

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            TrimOptions = TrimOptions.None,
            IgnoreBlankLines = false
        };

        using var streamReader = new StreamReader(
            filePath,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        using var parser = new CsvParser(streamReader, config);

        if (!parser.Read())
            throw new InvalidDataException("The CSV file is empty.");

        string[] header = parser.Record?.ToArray()
            ?? throw new InvalidDataException("The CSV header could not be read.");

        ChaseFormat format = DetectFormat(header);

        using SqliteTransaction transaction = connection.BeginTransaction();

        try
        {
            EnsureAccount(connection, transaction, fileIdentity.AccountLast4);
            EnsureDate(connection, transaction, fileIdentity.DownloadDateUnixSeconds);
            EnsureImportFormat(connection, transaction, format.Name);

            long importFileId = InsertImportFile(
                connection,
                transaction,
                fileSha256,
                fileIdentity.AccountLast4,
                format.Name,
                fileIdentity.DownloadDateUnixSeconds);

            int rowsRead = 0;
            int newTransactions = 0;
            int reusedTransactions = 0;
            int unparsedDescriptions = 0;

            var occurrenceByKey = new Dictionary<string, int>();

            int sourceRowNumber = 2;

            while (parser.Read())
            {
                string[] record = parser.Record?.ToArray()
                    ?? throw new InvalidDataException(
                        $"Could not read CSV record {sourceRowNumber}.");

                ValidateExtraFields(record, format.ExpectedColumnCount, sourceRowNumber);

                string[] row = record
                    .Take(format.ExpectedColumnCount)
                    .ToArray();

                ParsedTransaction parsed = format.Kind switch
                {
                    ChaseFormatKind.CreditCard =>
                        ParseCreditCardRow(
                            fileIdentity.AccountLast4,
                            row,
                            sourceRowNumber),

                    ChaseFormatKind.Deposit =>
                        ParseDepositRow(
                            fileIdentity.AccountLast4,
                            row,
                            sourceRowNumber),

                    _ => throw new InvalidOperationException(
                        "Unsupported Chase CSV format.")
                };

                if (parsed.Description is UnparsedDescriptionData)
                    unparsedDescriptions++;

                string occurrenceKey = parsed.ToString();
                int occurrence = occurrenceByKey.GetValueOrDefault(occurrenceKey);
                occurrenceByKey[occurrenceKey] = occurrence + 1;

                List<long> matchingIds = FindExistingTransactionIds(
                    connection,
                    transaction,
                    parsed);

                long transactionId;

                if (occurrence < matchingIds.Count)
                {
                    transactionId = matchingIds[occurrence];
                    reusedTransactions++;
                }
                else
                {
                    transactionId = InsertParsedTransaction(
                        connection,
                        transaction,
                        parsed);

                    newTransactions++;
                }

                InsertImportRow(
                    connection,
                    transaction,
                    importFileId,
                    sourceRowNumber,
                    transactionId);

                rowsRead++;
                sourceRowNumber++;
            }

            if (rowsRead == 0)
                throw new InvalidDataException(
                    "The Chase CSV contains a header but no transaction rows.");

            transaction.Commit();

            return new ChaseImportResult(
                Path.GetFileName(filePath),
                fileIdentity.AccountLast4,
                format.Name,
                rowsRead,
                newTransactions,
                reusedTransactions,
                unparsedDescriptions,
                false);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static ParsedTransaction ParseCreditCardRow(
        string accountLast4,
        string[] row,
        int sourceRowNumber)
    {
        if (!string.Equals(row[0], accountLast4, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"CSV row {sourceRowNumber} says Card={row[0]}, " +
                $"but the filename identifies account {accountLast4}.");
        }

        long transactionDate = ParseDateUnixSeconds(
            row[1],
            "MM/dd/yyyy",
            sourceRowNumber,
            "Transaction Date");

        long postingDate = ParseDateUnixSeconds(
            row[2],
            "MM/dd/yyyy",
            sourceRowNumber,
            "Post Date");

        long amountCents = ParseRequiredCents(
            row[6],
            sourceRowNumber,
            "Amount");

        string? category = NullIfEmpty(row[4]);
        string? memo = NullIfEmpty(row[7]);

        return new ParsedTransaction(
            AccountLast4: accountLast4,
            PostingDateUnixSeconds: postingDate,
            AmountCents: amountCents,
            TypeCode: row[5],
            Deposit: null,
            CreditCard: new CreditCardData(
                TransactionDateUnixSeconds: transactionDate,
                MerchantName: row[3],
                CategoryName: category,
                Memo: memo),
            Description: null);
    }

    private static ParsedTransaction ParseDepositRow(
        string accountLast4,
        string[] row,
        int sourceRowNumber)
    {
        DateOnly postingDate = ParseDate(
            row[1],
            "MM/dd/yyyy",
            sourceRowNumber,
            "Posting Date");

        long postingDateUnixSeconds = ToUnixMidnight(postingDate);

        long amountCents = ParseRequiredCents(
            row[3],
            sourceRowNumber,
            "Amount");

        long? balanceCents = ParseOptionalCents(
            row[5],
            sourceRowNumber,
            "Balance");

        string? checkOrSlipNumber = NullIfEmpty(row[6].Trim());

        var deposit = new DepositData(
            DetailsCode: row[0],
            BalanceCents: balanceCents,
            CheckOrSlipNumber: checkOrSlipNumber);

        DescriptionData description = ParseDepositDescription(
            row[4],
            row[2],
            postingDate,
            checkOrSlipNumber);

        return new ParsedTransaction(
            AccountLast4: accountLast4,
            PostingDateUnixSeconds: postingDateUnixSeconds,
            AmountCents: amountCents,
            TypeCode: row[4],
            Deposit: deposit,
            CreditCard: null,
            Description: description);
    }

    private static DescriptionData ParseDepositDescription(
        string typeCode,
        string rawDescription,
        DateOnly postingDate,
        string? checkOrSlipNumber)
    {
        try
        {
            return typeCode switch
            {
                "ACH_CREDIT" or "ACH_DEBIT" =>
                    ParsedOrUnparsed(
                        ParseAchDescription(rawDescription, postingDate),
                        rawDescription),

                "ACCT_XFER" =>
                    ParsedOrUnparsed(
                        ParseAccountTransfer(rawDescription, postingDate),
                        rawDescription),

                "LOAN_PMT" =>
                    ParsedOrUnparsed(
                        ParseChaseCardPayment(rawDescription, postingDate),
                        rawDescription),

                "CHECK_PAID" =>
                    ValidateCheckPaid(
                        rawDescription,
                        postingDate,
                        checkOrSlipNumber)
                        ? NoDescriptionData.Instance
                        : new UnparsedDescriptionData(rawDescription),

                "CHECK_DEPOSIT" =>
                    ValidateRemoteDeposit(
                        rawDescription,
                        checkOrSlipNumber)
                        ? NoDescriptionData.Instance
                        : new UnparsedDescriptionData(rawDescription),

                "DEBIT_CARD" =>
                    ParsedOrUnparsed(
                        ParseDebitCard(rawDescription, postingDate),
                        rawDescription),

                "ATM" =>
                    ParsedOrUnparsed(
                        ParseAtm(rawDescription, postingDate),
                        rawDescription),

                "FEE_TRANSACTION" =>
                    new FeeDescriptionData(rawDescription),

                "MISC_CREDIT" =>
                    ParsedOrUnparsed(
                        ParseRealTimePayment(rawDescription),
                        rawDescription),

                _ =>
                    new UnparsedDescriptionData(rawDescription)
            };
        }
        catch
        {
            // If Chase changes a description shape, keep the complete source
            // description rather than losing data or half-parsing it.
            return new UnparsedDescriptionData(rawDescription);
        }
    }

    private static DescriptionData ParsedOrUnparsed(
        DescriptionData? parsed,
        string rawDescription)
    {
        return parsed ?? new UnparsedDescriptionData(rawDescription);
    }

    private static AchDescriptionData? ParseAchDescription(
        string description,
        DateOnly postingDate)
    {
        Match full = FullAchRegex.Match(description);

        if (full.Success)
        {
            Match bankReference = AchBankReferenceRegex.Match(
                full.Groups["tail"].Value);

            if (!bankReference.Success)
                return null;

            string tail = full.Groups["tail"].Value;
            string receiverAndAddenda = tail[..bankReference.Index].TrimEnd();

            int addendaIndex = FindFirstAddendaIndex(receiverAndAddenda);

            string? individualName;
            string? paymentRelatedInformation;

            if (addendaIndex >= 0)
            {
                individualName = NullIfEmpty(
                    receiverAndAddenda[..addendaIndex].Trim());

                paymentRelatedInformation = NullIfEmpty(
                    receiverAndAddenda[addendaIndex..].Trim());
            }
            else
            {
                individualName = NullIfEmpty(receiverAndAddenda.Trim());
                paymentRelatedInformation = null;
            }

            DateOnly effectiveEntryDate = ParseYYMMDD(
                full.Groups["eed"].Value.Trim(),
                postingDate);

            return new AchDescriptionData(
                CompanyId: full.Groups["originatorId"].Value.Trim(),
                CompanyName: full.Groups["company"].Value.Trim(),
                CompanyDescriptiveDate: NullIfEmpty(
                    full.Groups["descriptiveDate"].Value.Trim()),
                EntryDescription: full.Groups["entryDescription"].Value.Trim(),
                SecCode: full.Groups["sec"].Value.Trim(),
                TraceNumber: NullIfEmpty(full.Groups["trace"].Value.Trim()),
                EffectiveEntryDateUnixSeconds: ToUnixMidnight(effectiveEntryDate),
                IndividualId: NullIfEmpty(
                    full.Groups["individualId"].Value.Trim()),
                IndividualName: individualName,
                PaymentRelatedInformation: paymentRelatedInformation,
                BankReferenceKind: bankReference.Groups["kind"].Value,
                BankReference: bankReference.Groups["reference"].Value.Trim());
        }

        Match shortForm = ShortAchRegex.Match(description);

        if (!shortForm.Success)
            return null;

        return new AchDescriptionData(
            CompanyId: shortForm.Groups["originatorId"].Value.Trim(),
            CompanyName: shortForm.Groups["company"].Value.Trim(),
            CompanyDescriptiveDate: null,
            EntryDescription: shortForm.Groups["entryDescription"].Value.Trim(),
            SecCode: shortForm.Groups["sec"].Value.Trim(),
            TraceNumber: null,
            EffectiveEntryDateUnixSeconds: null,
            IndividualId: NullIfEmpty(
                shortForm.Groups["individualId"].Value.Trim()),
            IndividualName: null,
            PaymentRelatedInformation: null,
            BankReferenceKind: null,
            BankReference: null);
    }

    private static int FindFirstAddendaIndex(string value)
    {
        int trn = value.IndexOf("TRN*", StringComparison.Ordinal);
        int txp = value.IndexOf("TXP*", StringComparison.Ordinal);

        if (trn < 0)
            return txp;

        if (txp < 0)
            return trn;

        return Math.Min(trn, txp);
    }

    private static AccountTransferData? ParseAccountTransfer(
        string description,
        DateOnly postingDate)
    {
        Match match = RealtimeTransferRegex.Match(description);

        if (match.Success)
        {
            if (!MonthDayMatches(
                    match.Groups["monthDay"].Value,
                    postingDate))
            {
                return null;
            }

            return new AccountTransferData(
                Direction: "TO",
                IsRealtime: true,
                Institution: "Chase",
                AccountLabel: "Personal Checking Acct",
                CounterpartyLast4: match.Groups["last4"].Value,
                ChaseTransactionNumber: match.Groups["transaction"].Value,
                ChaseReference: match.Groups["reference"].Value);
        }

        match = ToMmaTransferRegex.Match(description);

        if (match.Success)
        {
            if (!MonthDayMatches(
                    match.Groups["monthDay"].Value,
                    postingDate))
            {
                return null;
            }

            return new AccountTransferData(
                Direction: "TO",
                IsRealtime: false,
                Institution: "Chase",
                AccountLabel: "MMA",
                CounterpartyLast4: match.Groups["last4"].Value,
                ChaseTransactionNumber: match.Groups["transaction"].Value,
                ChaseReference: null);
        }

        match = FromChkTransferRegex.Match(description);

        if (match.Success)
        {
            return new AccountTransferData(
                Direction: "FROM",
                IsRealtime: false,
                Institution: "Chase",
                AccountLabel: "CHK",
                CounterpartyLast4: match.Groups["last4"].Value,
                ChaseTransactionNumber: match.Groups["transaction"].Value,
                ChaseReference: null);
        }

        match = NamedTransferRegex.Match(description);

        if (!match.Success)
            return null;

        if (!MonthDayMatches(
                match.Groups["monthDay"].Value,
                postingDate))
        {
            return null;
        }

        string transactionNumber =
            match.Groups["transaction"].Value;

        if (!string.Equals(
                match.Groups["leading"].Value,
                transactionNumber,
                StringComparison.Ordinal))
        {
            return null;
        }

        string name = match.Groups["name"].Value;

        (string institution, string accountLabel) = name switch
        {
            "My Discover Bank Savings" =>
                ("Discover Bank", "Savings"),

            "Personal Checking Acct" =>
                ("Chase", "Personal Checking Acct"),

            _ => (name, name)
        };

        return new AccountTransferData(
            Direction: "TO",
            IsRealtime: false,
            Institution: institution,
            AccountLabel: accountLabel,
            CounterpartyLast4: match.Groups["last4"].Value,
            ChaseTransactionNumber: transactionNumber,
            ChaseReference: null);
    }

    private static ChaseCardPaymentData? ParseChaseCardPayment(
        string description,
        DateOnly postingDate)
    {
        Match match = ChaseCardPaymentRegex.Match(description);

        if (!match.Success)
            return null;

        if (!MonthDayMatches(
                match.Groups["monthDay"].Value,
                postingDate))
        {
            return null;
        }

        return new ChaseCardPaymentData(
            match.Groups["last4"].Value);
    }

    private static bool ValidateCheckPaid(
        string description,
        DateOnly postingDate,
        string? checkOrSlipNumber)
    {
        Match match = CheckPaidRegex.Match(description);

        if (!match.Success || checkOrSlipNumber is null)
            return false;

        if (!string.Equals(
                match.Groups["number"].Value,
                checkOrSlipNumber,
                StringComparison.Ordinal))
        {
            return false;
        }

        string monthDay = match.Groups["monthDay"].Value;

        return string.IsNullOrEmpty(monthDay)
            || MonthDayMatches(monthDay, postingDate);
    }

    private static bool ValidateRemoteDeposit(
        string description,
        string? checkOrSlipNumber)
    {
        Match match = RemoteDepositRegex.Match(description);

        return match.Success
            && checkOrSlipNumber is not null
            && string.Equals(
                match.Groups["number"].Value,
                checkOrSlipNumber,
                StringComparison.Ordinal);
    }

    private static DebitCardDescriptionData? ParseDebitCard(
        string description,
        DateOnly postingDate)
    {
        Match match = DebitCardRegex.Match(description);

        if (!match.Success)
            return null;

        if (!MonthDayMatches(
                match.Groups["monthDay"].Value,
                postingDate))
        {
            return null;
        }

        return new DebitCardDescriptionData(
            match.Groups["merchant"].Value.TrimEnd());
    }

    private static AtmDescriptionData? ParseAtm(
        string description,
        DateOnly postingDate)
    {
        Match match = AtmWithdrawalRegex.Match(description);

        if (match.Success)
        {
            if (!MonthDayMatches(
                    match.Groups["monthDay"].Value,
                    postingDate))
            {
                return null;
            }

            return new AtmDescriptionData(
                Action: "WITHDRAWAL",
                TerminalId: NullIfEmpty(match.Groups["terminal"].Value.Trim()),
                Location: NullIfEmpty(match.Groups["location"].Value.Trim()));
        }

        match = AtmDepositRegex.Match(description);

        if (!match.Success)
            return null;

        if (!MonthDayMatches(
                match.Groups["monthDay"].Value,
                postingDate))
        {
            return null;
        }

        return new AtmDescriptionData(
            Action: "CASH_DEPOSIT",
            TerminalId: null,
            Location: NullIfEmpty(match.Groups["location"].Value.Trim()));
    }

    private static RealTimePaymentDescriptionData? ParseRealTimePayment(
        string description)
    {
        Match match = RealTimePaymentRegex.Match(description);

        if (!match.Success)
            return null;

        Match info = RealTimePaymentInfoRegex.Match(
            match.Groups["info"].Value);

        if (!info.Success)
            return null;

        if (!string.Equals(
                info.Groups["reference"].Value,
                match.Groups["reference"].Value,
                StringComparison.Ordinal))
        {
            return null;
        }

        if (!TimeOnly.TryParseExact(
                match.Groups["time"].Value,
                "HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out TimeOnly receivedTime))
        {
            return null;
        }

        return new RealTimePaymentDescriptionData(
            AbaRoutingNumber: match.Groups["aba"].Value,
            Sender: match.Groups["sender"].Value.Trim(),
            Reference: match.Groups["reference"].Value,
            OriginatorCompanyId: info.Groups["originatorId"].Value,
            PaymentCode: info.Groups["paymentCode"].Value,
            Tin: NullIfEmpty(info.Groups["tin"].Value),
            Npi: NullIfEmpty(info.Groups["npi"].Value),
            ReceiverName: NullIfEmpty(info.Groups["receiverName"].Value),
            Purpose: NullIfEmpty(info.Groups["purpose"].Value),
            InstructionId: match.Groups["iid"].Value,
            ReceivedSecondOfDay:
                receivedTime.Hour * 3600
                + receivedTime.Minute * 60
                + receivedTime.Second,
            BankReference: match.Groups["bankReference"].Value);
    }

    private static List<long> FindExistingTransactionIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed)
    {
        if (parsed.CreditCard is not null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = """
                SELECT t.Id
                FROM Transactions t
                JOIN CreditCardTransactions c
                  ON c.TransactionId = t.Id
                WHERE t.AccountId = (SELECT Id FROM Accounts WHERE Last4 = $account)
                  AND t.PostingDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate)
                  AND t.AmountCents = $amount
                  AND t.TypeId = (SELECT Id FROM TransactionTypes WHERE Code = $type)
                  AND c.TransactionDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $transactionDate)
                  AND c.MerchantId = (SELECT Id FROM CreditCardMerchants WHERE Name = $merchant)
                  AND c.CategoryId IS (SELECT Id FROM CreditCardCategories WHERE Name = $category)
                  AND c.Memo IS $memo
                ORDER BY t.Id;
                """;

            AddCommonParameters(command, parsed);
            command.Parameters.AddWithValue(
                "$transactionDate",
                parsed.CreditCard.TransactionDateUnixSeconds);
            command.Parameters.AddWithValue(
                "$merchant",
                parsed.CreditCard.MerchantName);
            AddNullableText(
                command,
                "$category",
                parsed.CreditCard.CategoryName);
            AddNullableText(
                command,
                "$memo",
                parsed.CreditCard.Memo);

            return ReadIds(command);
        }

        if (parsed.Deposit is null)
            throw new InvalidOperationException(
                "A parsed row has neither credit-card nor deposit data.");

        return parsed.Description switch
        {
            AchDescriptionData ach =>
                FindExistingAch(
                    connection,
                    transaction,
                    parsed,
                    ach),

            AccountTransferData transfer =>
                FindExistingTransfer(
                    connection,
                    transaction,
                    parsed,
                    transfer),

            ChaseCardPaymentData payment =>
                FindExistingChaseCardPayment(
                    connection,
                    transaction,
                    parsed,
                    payment),

            DebitCardDescriptionData debitCard =>
                FindExistingDebitCard(
                    connection,
                    transaction,
                    parsed,
                    debitCard),

            AtmDescriptionData atm =>
                FindExistingAtm(
                    connection,
                    transaction,
                    parsed,
                    atm),

            FeeDescriptionData fee =>
                FindExistingFee(
                    connection,
                    transaction,
                    parsed,
                    fee),

            RealTimePaymentDescriptionData realTimePayment =>
                FindExistingRealTimePayment(
                    connection,
                    transaction,
                    parsed,
                    realTimePayment),

            UnparsedDescriptionData unparsed =>
                FindExistingUnparsed(
                    connection,
                    transaction,
                    parsed,
                    unparsed),

            NoDescriptionData =>
                FindExistingDepositBaseOnly(
                    connection,
                    transaction,
                    parsed),

            null =>
                FindExistingDepositBaseOnly(
                    connection,
                    transaction,
                    parsed),

            _ => throw new InvalidOperationException(
                "Unknown parsed description type.")
        };
    }

    private static List<long> FindExistingAch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed,
        AchDescriptionData ach)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            SELECT t.Id
            FROM Transactions t
            JOIN DepositTransactions d
              ON d.TransactionId = t.Id
            JOIN AchTransactionsExpanded a
              ON a.TransactionId = t.Id
            JOIN AchOriginators o
              ON o.Id = a.OriginatorId
            JOIN AchEntryDescriptions ed
              ON ed.Id = a.EntryDescriptionId
            JOIN AchSecCodes sc
              ON sc.Id = a.SecCodeId
            LEFT JOIN AchBankReferenceKinds brk
              ON brk.Id = a.BankReferenceKindId
            WHERE t.AccountId = (SELECT Id FROM Accounts WHERE Last4 = $account)
              AND t.PostingDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate)
              AND t.AmountCents = $amount
              AND t.TypeId = (SELECT Id FROM TransactionTypes WHERE Code = $type)
              AND d.DetailsId = (SELECT Id FROM DepositDetails WHERE Code = $details)
              AND d.BalanceCents IS $balance
              AND d.CheckOrSlipNumber IS $check
              AND o.CompanyId = $companyId
              AND o.CompanyName = $companyName
              AND a.CompanyDescriptiveDate IS $descriptiveDate
              AND ed.Description = $entryDescription
              AND sc.Code = $secCode
              AND a.TraceNumber IS $trace
              AND a.EffectiveEntryDateId IS (SELECT Id FROM DateValues WHERE UnixSeconds = $eed)
              AND a.IndividualId IS $individualId
              AND a.IndividualName IS $individualName
              AND a.PaymentRelatedInformation IS $paymentInfo
              AND brk.Name IS $bankReferenceKind
              AND a.BankReference IS $bankReference
            ORDER BY t.Id;
            """;

        AddDepositParameters(command, parsed);
        command.Parameters.AddWithValue("$companyId", ach.CompanyId);
        command.Parameters.AddWithValue("$companyName", ach.CompanyName);
        AddNullableText(
            command,
            "$descriptiveDate",
            ach.CompanyDescriptiveDate);
        command.Parameters.AddWithValue(
            "$entryDescription",
            ach.EntryDescription);
        command.Parameters.AddWithValue("$secCode", ach.SecCode);
        AddNullableText(command, "$trace", ach.TraceNumber);
        AddNullableInt64(
            command,
            "$eed",
            ach.EffectiveEntryDateUnixSeconds);
        AddNullableText(
            command,
            "$individualId",
            ach.IndividualId);
        AddNullableText(
            command,
            "$individualName",
            ach.IndividualName);
        AddNullableText(
            command,
            "$paymentInfo",
            ach.PaymentRelatedInformation);
        AddNullableText(
            command,
            "$bankReferenceKind",
            ach.BankReferenceKind);
        AddNullableText(
            command,
            "$bankReference",
            ach.BankReference);

        return ReadIds(command);
    }

    private static List<long> FindExistingTransfer(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed,
        AccountTransferData transfer)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            SELECT t.Id
            FROM Transactions t
            JOIN DepositTransactions d
              ON d.TransactionId = t.Id
            JOIN AccountTransfersExpanded x
              ON x.TransactionId = t.Id
            JOIN TransferDirections dir
              ON dir.Id = x.DirectionId
            JOIN TransferCounterparties c
              ON c.Id = x.CounterpartyId
            WHERE t.AccountId = (SELECT Id FROM Accounts WHERE Last4 = $account)
              AND t.PostingDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate)
              AND t.AmountCents = $amount
              AND t.TypeId = (SELECT Id FROM TransactionTypes WHERE Code = $type)
              AND d.DetailsId = (SELECT Id FROM DepositDetails WHERE Code = $details)
              AND d.BalanceCents IS $balance
              AND d.CheckOrSlipNumber IS $check
              AND dir.Name = $direction
              AND x.IsRealtime = $isRealtime
              AND c.Institution = $institution
              AND c.AccountLabel = $accountLabel
              AND c.Last4 = $counterpartyLast4
              AND x.ChaseTransactionNumber = $chaseTransaction
              AND x.ChaseReference IS $chaseReference
            ORDER BY t.Id;
            """;

        AddDepositParameters(command, parsed);
        command.Parameters.AddWithValue("$direction", transfer.Direction);
        command.Parameters.AddWithValue(
            "$isRealtime",
            transfer.IsRealtime ? 1 : 0);
        command.Parameters.AddWithValue(
            "$institution",
            transfer.Institution);
        command.Parameters.AddWithValue(
            "$accountLabel",
            transfer.AccountLabel);
        command.Parameters.AddWithValue(
            "$counterpartyLast4",
            transfer.CounterpartyLast4);
        command.Parameters.AddWithValue(
            "$chaseTransaction",
            transfer.ChaseTransactionNumber);
        AddNullableText(
            command,
            "$chaseReference",
            transfer.ChaseReference);

        return ReadIds(command);
    }

    private static List<long> FindExistingChaseCardPayment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed,
        ChaseCardPaymentData payment)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            SELECT t.Id
            FROM Transactions t
            JOIN DepositTransactions d
              ON d.TransactionId = t.Id
            JOIN ChaseCardPayments p
              ON p.TransactionId = t.Id
            WHERE t.AccountId = (SELECT Id FROM Accounts WHERE Last4 = $account)
              AND t.PostingDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate)
              AND t.AmountCents = $amount
              AND t.TypeId = (SELECT Id FROM TransactionTypes WHERE Code = $type)
              AND d.DetailsId = (SELECT Id FROM DepositDetails WHERE Code = $details)
              AND d.BalanceCents IS $balance
              AND d.CheckOrSlipNumber IS $check
              AND p.TargetAccountId = (SELECT Id FROM Accounts WHERE Last4 = $targetCard)
            ORDER BY t.Id;
            """;

        AddDepositParameters(command, parsed);
        command.Parameters.AddWithValue(
            "$targetCard",
            payment.TargetCardLast4);

        return ReadIds(command);
    }

    private static List<long> FindExistingDebitCard(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed,
        DebitCardDescriptionData debitCard)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            SELECT t.Id
            FROM Transactions t
            JOIN DepositTransactions d
              ON d.TransactionId = t.Id
            JOIN DebitCardTransactions c
              ON c.TransactionId = t.Id
            WHERE t.AccountId = (SELECT Id FROM Accounts WHERE Last4 = $account)
              AND t.PostingDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate)
              AND t.AmountCents = $amount
              AND t.TypeId = (SELECT Id FROM TransactionTypes WHERE Code = $type)
              AND d.DetailsId = (SELECT Id FROM DepositDetails WHERE Code = $details)
              AND d.BalanceCents IS $balance
              AND d.CheckOrSlipNumber IS $check
              AND c.MerchantDescriptor = $merchant
            ORDER BY t.Id;
            """;

        AddDepositParameters(command, parsed);
        command.Parameters.AddWithValue(
            "$merchant",
            debitCard.MerchantDescriptor);

        return ReadIds(command);
    }

    private static List<long> FindExistingAtm(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed,
        AtmDescriptionData atm)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            SELECT t.Id
            FROM Transactions t
            JOIN DepositTransactions d
              ON d.TransactionId = t.Id
            JOIN AtmTransactions a
              ON a.TransactionId = t.Id
            WHERE t.AccountId = (SELECT Id FROM Accounts WHERE Last4 = $account)
              AND t.PostingDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate)
              AND t.AmountCents = $amount
              AND t.TypeId = (SELECT Id FROM TransactionTypes WHERE Code = $type)
              AND d.DetailsId = (SELECT Id FROM DepositDetails WHERE Code = $details)
              AND d.BalanceCents IS $balance
              AND d.CheckOrSlipNumber IS $check
              AND a.Action = $action
              AND a.TerminalId IS $terminal
              AND a.Location IS $location
            ORDER BY t.Id;
            """;

        AddDepositParameters(command, parsed);
        command.Parameters.AddWithValue("$action", atm.Action);
        AddNullableText(command, "$terminal", atm.TerminalId);
        AddNullableText(command, "$location", atm.Location);

        return ReadIds(command);
    }

    private static List<long> FindExistingFee(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed,
        FeeDescriptionData fee)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            SELECT t.Id
            FROM Transactions t
            JOIN DepositTransactions d
              ON d.TransactionId = t.Id
            JOIN FeeTransactions f
              ON f.TransactionId = t.Id
            WHERE t.AccountId = (SELECT Id FROM Accounts WHERE Last4 = $account)
              AND t.PostingDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate)
              AND t.AmountCents = $amount
              AND t.TypeId = (SELECT Id FROM TransactionTypes WHERE Code = $type)
              AND d.DetailsId = (SELECT Id FROM DepositDetails WHERE Code = $details)
              AND d.BalanceCents IS $balance
              AND d.CheckOrSlipNumber IS $check
              AND f.Description = $description
            ORDER BY t.Id;
            """;

        AddDepositParameters(command, parsed);
        command.Parameters.AddWithValue(
            "$description",
            fee.Description);

        return ReadIds(command);
    }

    private static List<long> FindExistingRealTimePayment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed,
        RealTimePaymentDescriptionData payment)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            SELECT t.Id
            FROM Transactions t
            JOIN DepositTransactions d
              ON d.TransactionId = t.Id
            JOIN RealTimePayments r
              ON r.TransactionId = t.Id
            WHERE t.AccountId = (SELECT Id FROM Accounts WHERE Last4 = $account)
              AND t.PostingDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate)
              AND t.AmountCents = $amount
              AND t.TypeId = (SELECT Id FROM TransactionTypes WHERE Code = $type)
              AND d.DetailsId = (SELECT Id FROM DepositDetails WHERE Code = $details)
              AND d.BalanceCents IS $balance
              AND d.CheckOrSlipNumber IS $check
              AND r.AbaRoutingNumber = $aba
              AND r.Sender = $sender
              AND r.Reference = $reference
              AND r.OriginatorCompanyId = $originatorCompanyId
              AND r.PaymentCode = $paymentCode
              AND r.Tin IS $tin
              AND r.Npi IS $npi
              AND r.ReceiverName IS $receiverName
              AND r.Purpose IS $purpose
              AND r.InstructionId = $instructionId
              AND r.ReceivedSecondOfDay = $receivedSecondOfDay
              AND r.BankReference = $bankReference
            ORDER BY t.Id;
            """;

        AddDepositParameters(command, parsed);
        command.Parameters.AddWithValue("$aba", payment.AbaRoutingNumber);
        command.Parameters.AddWithValue("$sender", payment.Sender);
        command.Parameters.AddWithValue("$reference", payment.Reference);
        command.Parameters.AddWithValue(
            "$originatorCompanyId",
            payment.OriginatorCompanyId);
        command.Parameters.AddWithValue(
            "$paymentCode",
            payment.PaymentCode);
        AddNullableText(command, "$tin", payment.Tin);
        AddNullableText(command, "$npi", payment.Npi);
        AddNullableText(
            command,
            "$receiverName",
            payment.ReceiverName);
        AddNullableText(command, "$purpose", payment.Purpose);
        command.Parameters.AddWithValue(
            "$instructionId",
            payment.InstructionId);
        command.Parameters.AddWithValue(
            "$receivedSecondOfDay",
            payment.ReceivedSecondOfDay);
        command.Parameters.AddWithValue(
            "$bankReference",
            payment.BankReference);

        return ReadIds(command);
    }

    private static List<long> FindExistingUnparsed(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed,
        UnparsedDescriptionData unparsed)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            SELECT t.Id
            FROM Transactions t
            JOIN DepositTransactions d
              ON d.TransactionId = t.Id
            JOIN UnparsedDepositDescriptions u
              ON u.TransactionId = t.Id
            WHERE t.AccountId = (SELECT Id FROM Accounts WHERE Last4 = $account)
              AND t.PostingDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate)
              AND t.AmountCents = $amount
              AND t.TypeId = (SELECT Id FROM TransactionTypes WHERE Code = $type)
              AND d.DetailsId = (SELECT Id FROM DepositDetails WHERE Code = $details)
              AND d.BalanceCents IS $balance
              AND d.CheckOrSlipNumber IS $check
              AND u.Description = $description
            ORDER BY t.Id;
            """;

        AddDepositParameters(command, parsed);
        command.Parameters.AddWithValue(
            "$description",
            unparsed.Description);

        return ReadIds(command);
    }

    private static List<long> FindExistingDepositBaseOnly(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            SELECT t.Id
            FROM Transactions t
            JOIN DepositTransactions d
              ON d.TransactionId = t.Id
            WHERE t.AccountId = (SELECT Id FROM Accounts WHERE Last4 = $account)
              AND t.PostingDateId = (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate)
              AND t.AmountCents = $amount
              AND t.TypeId = (SELECT Id FROM TransactionTypes WHERE Code = $type)
              AND d.DetailsId = (SELECT Id FROM DepositDetails WHERE Code = $details)
              AND d.BalanceCents IS $balance
              AND d.CheckOrSlipNumber IS $check
              AND NOT EXISTS
              (
                  SELECT 1 FROM AchTransactions a
                  WHERE a.TransactionId = t.Id
              )
              AND NOT EXISTS
              (
                  SELECT 1 FROM AccountTransfers x
                  WHERE x.TransactionId = t.Id
              )
              AND NOT EXISTS
              (
                  SELECT 1 FROM ChaseCardPayments p
                  WHERE p.TransactionId = t.Id
              )
              AND NOT EXISTS
              (
                  SELECT 1 FROM DebitCardTransactions c
                  WHERE c.TransactionId = t.Id
              )
              AND NOT EXISTS
              (
                  SELECT 1 FROM AtmTransactions a
                  WHERE a.TransactionId = t.Id
              )
              AND NOT EXISTS
              (
                  SELECT 1 FROM FeeTransactions f
                  WHERE f.TransactionId = t.Id
              )
              AND NOT EXISTS
              (
                  SELECT 1 FROM RealTimePayments r
                  WHERE r.TransactionId = t.Id
              )
              AND NOT EXISTS
              (
                  SELECT 1 FROM UnparsedDepositDescriptions u
                  WHERE u.TransactionId = t.Id
              )
            ORDER BY t.Id;
            """;

        AddDepositParameters(command, parsed);

        return ReadIds(command);
    }

    private static long InsertParsedTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ParsedTransaction parsed)
    {
        EnsureAccount(
            connection,
            transaction,
            parsed.AccountLast4);

        EnsureDate(
            connection,
            transaction,
            parsed.PostingDateUnixSeconds);

        EnsureSingleTextValue(
            connection,
            transaction,
            "TransactionTypes",
            "Code",
            parsed.TypeCode);

        long transactionId;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;

            command.CommandText = """
                INSERT INTO Transactions
                (
                    AccountId,
                    PostingDateId,
                    AmountCents,
                    TypeId
                )
                VALUES
                (
                    (SELECT Id FROM Accounts WHERE Last4 = $account),
                    (SELECT Id FROM DateValues WHERE UnixSeconds = $postingDate),
                    $amount,
                    (SELECT Id FROM TransactionTypes WHERE Code = $type)
                );

                SELECT last_insert_rowid();
                """;

            AddCommonParameters(command, parsed);

            transactionId = Convert.ToInt64(
                command.ExecuteScalar(),
                CultureInfo.InvariantCulture);
        }

        if (parsed.CreditCard is not null)
        {
            InsertCreditCardData(
                connection,
                transaction,
                transactionId,
                parsed.CreditCard);

            return transactionId;
        }

        if (parsed.Deposit is null)
            throw new InvalidOperationException(
                "A parsed transaction has neither card nor deposit data.");

        InsertDepositData(
            connection,
            transaction,
            transactionId,
            parsed.Deposit);

        switch (parsed.Description)
        {
            case AchDescriptionData ach:
                InsertAch(
                    connection,
                    transaction,
                    transactionId,
                    ach,
                    parsed.PostingDateUnixSeconds);
                break;

            case AccountTransferData transfer:
                InsertTransfer(
                    connection,
                    transaction,
                    transactionId,
                    transfer);
                break;

            case ChaseCardPaymentData payment:
                InsertChaseCardPayment(
                    connection,
                    transaction,
                    transactionId,
                    payment);
                break;

            case DebitCardDescriptionData debitCard:
                InsertDebitCard(
                    connection,
                    transaction,
                    transactionId,
                    debitCard);
                break;

            case AtmDescriptionData atm:
                InsertAtm(
                    connection,
                    transaction,
                    transactionId,
                    atm);
                break;

            case FeeDescriptionData fee:
                InsertFee(
                    connection,
                    transaction,
                    transactionId,
                    fee);
                break;

            case RealTimePaymentDescriptionData realTimePayment:
                InsertRealTimePayment(
                    connection,
                    transaction,
                    transactionId,
                    realTimePayment);
                break;

            case UnparsedDescriptionData unparsed:
                InsertUnparsed(
                    connection,
                    transaction,
                    transactionId,
                    unparsed);
                break;

            case NoDescriptionData:
            case null:
                break;

            default:
                throw new InvalidOperationException(
                    "Unknown parsed description type.");
        }

        return transactionId;
    }

    private static void InsertCreditCardData(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transactionId,
        CreditCardData card)
    {
        EnsureDate(
            connection,
            transaction,
            card.TransactionDateUnixSeconds);

        EnsureSingleTextValue(
            connection,
            transaction,
            "CreditCardMerchants",
            "Name",
            card.MerchantName);

        if (card.CategoryName is not null)
        {
            EnsureSingleTextValue(
                connection,
                transaction,
                "CreditCardCategories",
                "Name",
                card.CategoryName);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO CreditCardTransactions
            (
                TransactionId,
                TransactionDateId,
                MerchantId,
                CategoryId,
                Memo
            )
            VALUES
            (
                $transactionId,
                (SELECT Id FROM DateValues WHERE UnixSeconds = $transactionDate),
                (SELECT Id FROM CreditCardMerchants WHERE Name = $merchant),
                (SELECT Id FROM CreditCardCategories WHERE Name = $category),
                $memo
            );
            """;

        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);
        command.Parameters.AddWithValue(
            "$transactionDate",
            card.TransactionDateUnixSeconds);
        command.Parameters.AddWithValue(
            "$merchant",
            card.MerchantName);
        AddNullableText(
            command,
            "$category",
            card.CategoryName);
        AddNullableText(command, "$memo", card.Memo);

        command.ExecuteNonQuery();
    }

    private static void InsertDepositData(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transactionId,
        DepositData deposit)
    {
        EnsureSingleTextValue(
            connection,
            transaction,
            "DepositDetails",
            "Code",
            deposit.DetailsCode);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO DepositTransactions
            (
                TransactionId,
                DetailsId,
                BalanceCents,
                CheckOrSlipNumber
            )
            VALUES
            (
                $transactionId,
                (SELECT Id FROM DepositDetails WHERE Code = $details),
                $balance,
                $check
            );
            """;

        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);
        command.Parameters.AddWithValue(
            "$details",
            deposit.DetailsCode);
        AddNullableInt64(
            command,
            "$balance",
            deposit.BalanceCents);
        AddNullableText(
            command,
            "$check",
            deposit.CheckOrSlipNumber);

        command.ExecuteNonQuery();
    }

    private static void InsertAch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transactionId,
        AchDescriptionData ach,
        long postingDateUnixSeconds)
    {
        long originatorId = GetOrCreateAchOriginator(
            connection,
            transaction,
            ach.CompanyId,
            ach.CompanyName);

        long entryDescriptionId = GetOrCreateTextLookupId(
            connection,
            transaction,
            "AchEntryDescriptions",
            "Description",
            ach.EntryDescription);

        long secCodeId = GetOrCreateTextLookupId(
            connection,
            transaction,
            "AchSecCodes",
            "Code",
            ach.SecCode);

        CompactAchTrace trace = CompactAchTraceValue(
            connection,
            transaction,
            ach.TraceNumber);

        int? paymentFormatId = null;
        string? paymentPayload = null;

        if (ach.PaymentRelatedInformation is not null)
        {
            (int encodedFormatId, string encodedPayload) =
                EncodeAchPaymentInformation(ach.PaymentRelatedInformation);
            paymentFormatId = encodedFormatId;
            paymentPayload = encodedPayload;
        }

        long? bankReferenceKindId = GetOrCreateNullableTextLookupId(
            connection,
            transaction,
            "AchBankReferenceKinds",
            "Name",
            ach.BankReferenceKind);

        (int? descriptiveDate, string? descriptiveDateOverride) =
            CompactCompanyDescriptiveDate(ach.CompanyDescriptiveDate);

        string? bankReferenceOverride = GetBankReferenceOverride(
            ach.BankReference,
            trace.Sequence,
            postingDateUnixSeconds);

        if (ach.EffectiveEntryDateUnixSeconds.HasValue)
        {
            EnsureDate(
                connection,
                transaction,
                ach.EffectiveEntryDateUnixSeconds.Value);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO AchTransactions
            (
                TransactionId,
                OriginatorId,
                CompanyDescriptiveDate,
                CompanyDescriptiveDateOverride,
                EntryDescriptionId,
                SecCodeId,
                TraceOdfiId,
                TraceSequence,
                TraceNumberOverride,
                EffectiveEntryDateId,
                IndividualIdentifier,
                IndividualName,
                PaymentInformationFormatId,
                PaymentInformationPayload,
                BankReferenceKindId,
                HasBankReference,
                BankReferenceOverride
            )
            VALUES
            (
                $transactionId,
                $originatorId,
                $descriptiveDate,
                $descriptiveDateOverride,
                $entryDescriptionId,
                $secCodeId,
                $traceOdfiId,
                $traceSequence,
                $traceNumberOverride,
                (SELECT Id FROM DateValues WHERE UnixSeconds = $eed),
                $individualIdentifier,
                $individualName,
                $paymentFormatId,
                $paymentPayload,
                $bankReferenceKindId,
                $hasBankReference,
                $bankReferenceOverride
            );
            """;

        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);
        command.Parameters.AddWithValue(
            "$originatorId",
            originatorId);
        AddNullableInt64(
            command,
            "$descriptiveDate",
            descriptiveDate);
        AddNullableText(command, "$descriptiveDateOverride", descriptiveDateOverride);
        command.Parameters.AddWithValue(
            "$entryDescriptionId",
            entryDescriptionId);
        command.Parameters.AddWithValue(
            "$secCodeId",
            secCodeId);
        AddNullableInt64(
            command,
            "$traceOdfiId",
            trace.OdfiId);
        AddNullableInt64(command, "$traceSequence", trace.Sequence);
        AddNullableText(command, "$traceNumberOverride", trace.Override);
        AddNullableInt64(
            command,
            "$eed",
            ach.EffectiveEntryDateUnixSeconds);
        AddNullableText(command, "$individualIdentifier", ach.IndividualId);
        AddNullableText(command, "$individualName", ach.IndividualName);
        AddNullableInt64(
            command,
            "$paymentFormatId",
            paymentFormatId);
        AddNullableText(command, "$paymentPayload", paymentPayload);
        AddNullableInt64(
            command,
            "$bankReferenceKindId",
            bankReferenceKindId);
        command.Parameters.AddWithValue(
            "$hasBankReference",
            ach.BankReference is null ? 0 : 1);
        AddNullableText(
            command,
            "$bankReferenceOverride",
            bankReferenceOverride);

        command.ExecuteNonQuery();
    }

    private static void InsertTransfer(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transactionId,
        AccountTransferData transfer)
    {
        long directionId = GetOrCreateTextLookupId(
            connection,
            transaction,
            "TransferDirections",
            "Name",
            transfer.Direction);

        long counterpartyId = GetOrCreateTransferCounterparty(
            connection,
            transaction,
            transfer.Institution,
            transfer.AccountLabel,
            transfer.CounterpartyLast4);

        if (!long.TryParse(
                transfer.ChaseTransactionNumber,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long chaseTransactionNumber))
        {
            throw new InvalidOperationException(
                "A parsed account transfer has a nonnumeric Chase transaction number.");
        }

        string derivedReference = "9" +
            transfer.ChaseTransactionNumber[^9..] +
            "RX";
        string? referenceOverride = transfer.ChaseReference is not null &&
            !string.Equals(
                transfer.ChaseReference,
                derivedReference,
                StringComparison.Ordinal)
                    ? transfer.ChaseReference
                    : null;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO AccountTransfers
            (
                TransactionId,
                DirectionId,
                IsRealtime,
                CounterpartyId,
                ChaseTransactionNumber,
                HasChaseReference,
                ChaseReferenceOverride
            )
            VALUES
            (
                $transactionId,
                $directionId,
                $isRealtime,
                $counterpartyId,
                $chaseTransaction,
                $hasChaseReference,
                $chaseReferenceOverride
            );
            """;

        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);
        command.Parameters.AddWithValue(
            "$directionId",
            directionId);
        command.Parameters.AddWithValue(
            "$isRealtime",
            transfer.IsRealtime ? 1 : 0);
        command.Parameters.AddWithValue(
            "$counterpartyId",
            counterpartyId);
        command.Parameters.AddWithValue(
            "$chaseTransaction",
            chaseTransactionNumber);
        command.Parameters.AddWithValue(
            "$hasChaseReference",
            transfer.ChaseReference is null ? 0 : 1);
        AddNullableText(command, "$chaseReferenceOverride", referenceOverride);

        command.ExecuteNonQuery();
    }

    private static void InsertChaseCardPayment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transactionId,
        ChaseCardPaymentData payment)
    {
        EnsureAccount(
            connection,
            transaction,
            payment.TargetCardLast4);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO ChaseCardPayments
            (
                TransactionId,
                TargetAccountId
            )
            VALUES
            (
                $transactionId,
                (SELECT Id FROM Accounts WHERE Last4 = $targetCard)
            );
            """;

        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);
        command.Parameters.AddWithValue(
            "$targetCard",
            payment.TargetCardLast4);

        command.ExecuteNonQuery();
    }

    private static void InsertDebitCard(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transactionId,
        DebitCardDescriptionData debitCard)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO DebitCardTransactions
            (
                TransactionId,
                MerchantDescriptor
            )
            VALUES
            (
                $transactionId,
                $merchant
            );
            """;

        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);
        command.Parameters.AddWithValue(
            "$merchant",
            debitCard.MerchantDescriptor);

        command.ExecuteNonQuery();
    }

    private static void InsertAtm(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transactionId,
        AtmDescriptionData atm)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO AtmTransactions
            (
                TransactionId,
                Action,
                TerminalId,
                Location
            )
            VALUES
            (
                $transactionId,
                $action,
                $terminal,
                $location
            );
            """;

        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);
        command.Parameters.AddWithValue(
            "$action",
            atm.Action);
        AddNullableText(
            command,
            "$terminal",
            atm.TerminalId);
        AddNullableText(
            command,
            "$location",
            atm.Location);

        command.ExecuteNonQuery();
    }

    private static void InsertFee(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transactionId,
        FeeDescriptionData fee)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO FeeTransactions
            (
                TransactionId,
                Description
            )
            VALUES
            (
                $transactionId,
                $description
            );
            """;

        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);
        command.Parameters.AddWithValue(
            "$description",
            fee.Description);

        command.ExecuteNonQuery();
    }

    private static void InsertRealTimePayment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transactionId,
        RealTimePaymentDescriptionData payment)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO RealTimePayments
            (
                TransactionId,
                AbaRoutingNumber,
                Sender,
                Reference,
                OriginatorCompanyId,
                PaymentCode,
                Tin,
                Npi,
                ReceiverName,
                Purpose,
                InstructionId,
                ReceivedSecondOfDay,
                BankReference
            )
            VALUES
            (
                $transactionId,
                $aba,
                $sender,
                $reference,
                $originatorCompanyId,
                $paymentCode,
                $tin,
                $npi,
                $receiverName,
                $purpose,
                $instructionId,
                $receivedSecondOfDay,
                $bankReference
            );
            """;

        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);
        command.Parameters.AddWithValue(
            "$aba",
            payment.AbaRoutingNumber);
        command.Parameters.AddWithValue(
            "$sender",
            payment.Sender);
        command.Parameters.AddWithValue(
            "$reference",
            payment.Reference);
        command.Parameters.AddWithValue(
            "$originatorCompanyId",
            payment.OriginatorCompanyId);
        command.Parameters.AddWithValue(
            "$paymentCode",
            payment.PaymentCode);
        AddNullableText(command, "$tin", payment.Tin);
        AddNullableText(command, "$npi", payment.Npi);
        AddNullableText(
            command,
            "$receiverName",
            payment.ReceiverName);
        AddNullableText(
            command,
            "$purpose",
            payment.Purpose);
        command.Parameters.AddWithValue(
            "$instructionId",
            payment.InstructionId);
        command.Parameters.AddWithValue(
            "$receivedSecondOfDay",
            payment.ReceivedSecondOfDay);
        command.Parameters.AddWithValue(
            "$bankReference",
            payment.BankReference);

        command.ExecuteNonQuery();
    }

    private static void InsertUnparsed(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long transactionId,
        UnparsedDescriptionData unparsed)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO UnparsedDepositDescriptions
            (
                TransactionId,
                Description
            )
            VALUES
            (
                $transactionId,
                $description
            );
            """;

        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);
        command.Parameters.AddWithValue(
            "$description",
            unparsed.Description);

        command.ExecuteNonQuery();
    }

    private static bool ImportFileAlreadyExists(
        SqliteConnection connection,
        byte[] fileSha256)
    {
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT EXISTS
            (
                SELECT 1
                FROM ImportFiles
                WHERE FileSha256 = $sha256
            );
            """;

        command.Parameters.AddWithValue(
            "$sha256",
            fileSha256);

        return Convert.ToInt64(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture) != 0;
    }

    private static long InsertImportFile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] fileSha256,
        string accountLast4,
        string formatName,
        long downloadDateUnixSeconds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO ImportFiles
            (
                FileSha256,
                AccountId,
                FormatId,
                DownloadDateId,
                ImportedAtUtc
            )
            VALUES
            (
                $sha256,
                (SELECT Id FROM Accounts WHERE Last4 = $account),
                (SELECT Id FROM ImportFormats WHERE Name = $format),
                (SELECT Id FROM DateValues WHERE UnixSeconds = $downloadDate),
                $importedAt
            );

            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue(
            "$sha256",
            fileSha256);
        command.Parameters.AddWithValue(
            "$account",
            accountLast4);
        command.Parameters.AddWithValue(
            "$format",
            formatName);
        command.Parameters.AddWithValue(
            "$downloadDate",
            downloadDateUnixSeconds);
        command.Parameters.AddWithValue(
            "$importedAt",
            DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture));

        return Convert.ToInt64(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture);
    }

    private static void InsertImportRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long importFileId,
        int sourceRowNumber,
        long transactionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO ImportRows
            (
                ImportFileId,
                SourceRowNumber,
                TransactionId
            )
            VALUES
            (
                $importFileId,
                $sourceRowNumber,
                $transactionId
            );
            """;

        command.Parameters.AddWithValue(
            "$importFileId",
            importFileId);
        command.Parameters.AddWithValue(
            "$sourceRowNumber",
            sourceRowNumber);
        command.Parameters.AddWithValue(
            "$transactionId",
            transactionId);

        command.ExecuteNonQuery();
    }

    private static void EnsureAccount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string last4)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            SELECT Name
            FROM Accounts
            WHERE Last4 = $last4;
            """;

        command.Parameters.AddWithValue("$last4", last4);

        object? accountName = command.ExecuteScalar();

        if (accountName is null)
        {
            throw new InvalidDataException(
                $"Chase account {last4} is not configured. " +
                "Add it to the Accounts table with its account name before importing.");
        }
    }

    private static void EnsureDate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long unixSeconds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT OR IGNORE INTO DateValues (UnixSeconds)
            VALUES ($unixSeconds);
            """;

        command.Parameters.AddWithValue(
            "$unixSeconds",
            unixSeconds);
        command.ExecuteNonQuery();
    }

    private static CompactAchTrace CompactAchTraceValue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? traceNumber)
    {
        if (traceNumber is null)
            return new CompactAchTrace(null, null, null);

        if (traceNumber.Length != 15 ||
            !int.TryParse(
                traceNumber.AsSpan(0, 8),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int odfi) ||
            !int.TryParse(
                traceNumber.AsSpan(8, 7),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int sequence))
        {
            return new CompactAchTrace(null, null, traceNumber);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AchOdfiIds (Value)
            VALUES ($value)
            ON CONFLICT(Value) DO NOTHING;

            SELECT Id
            FROM AchOdfiIds
            WHERE Value = $value;
            """;

        command.Parameters.AddWithValue("$value", odfi);
        long odfiId = Convert.ToInt64(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture);

        return new CompactAchTrace(odfiId, sequence, null);
    }

    private static (int? Value, string? Override)
        CompactCompanyDescriptiveDate(string? value)
    {
        if (value is null)
            return (null, null);

        if (value.Length == 6 && int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int compact))
        {
            return (compact, null);
        }

        return (null, value);
    }

    private static string? GetBankReferenceOverride(
        string? bankReference,
        int? traceSequence,
        long postingDateUnixSeconds)
    {
        if (bankReference is null)
            return null;

        if (!traceSequence.HasValue)
            return bankReference;

        int dayOfYear = DateTimeOffset
            .FromUnixTimeSeconds(postingDateUnixSeconds)
            .UtcDateTime
            .DayOfYear;
        string derived = string.Format(
            CultureInfo.InvariantCulture,
            "{0:000}{1:0000000}TC",
            dayOfYear,
            traceSequence.Value);

        return string.Equals(bankReference, derived, StringComparison.Ordinal)
            ? null
            : bankReference;
    }

    private static void EnsureImportFormat(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string formatName)
    {
        EnsureSingleTextValue(
            connection,
            transaction,
            "ImportFormats",
            "Name",
            formatName);
    }

    private static void EnsureSingleTextValue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string value)
    {
        // tableName and columnName are only ever compile-time constants
        // supplied by this class.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
            $"INSERT OR IGNORE INTO {tableName} ({columnName}) " +
            $"VALUES ($value);";

        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static (int FormatId, string Payload)
        EncodeAchPaymentInformation(string information)
    {
        const string trnPrefix = "TRN*1*";

        if (TryEncodeDerivedCpPaymentInformation(
                information,
                out string cpPayload))
        {
            return (5, cpPayload);
        }

        if (TryStripAchPaymentTemplate(
                information,
                trnPrefix,
                "*1411289245*0000877 26\\",
                out string payload))
        {
            return (1, payload);
        }

        if (TryStripAchPaymentTemplate(
                information,
                trnPrefix,
                "*1411289245*000087726 \\",
                out payload))
        {
            return (2, payload);
        }

        if (TryStripAchPaymentTemplate(
                information,
                trnPrefix,
                "*1411648670\\",
                out payload))
        {
            return (3, payload);
        }

        if (TryStripAchPaymentTemplate(
                information,
                trnPrefix,
                "*1066033492\\",
                out payload))
        {
            return (4, payload);
        }

        if (TryStripAchPaymentTemplate(
                information,
                trnPrefix,
                "*1341858379\\",
                out payload))
        {
            return (6, payload);
        }

        if (TryStripAchPaymentTemplate(
                information,
                trnPrefix,
                "*1364004756*36 4004756~                                      0",
                out payload))
        {
            return (7, payload);
        }

        if (TryStripAchPaymentTemplate(
                information,
                trnPrefix,
                "*139 1995276*0000UMR01\\",
                out payload))
        {
            return (8, payload);
        }

        if (TryStripAchPaymentTemplate(
                information,
                trnPrefix,
                "*1860507074*0 000UHCEX\\",
                out payload))
        {
            return (9, payload);
        }

        if (TryStripAchPaymentTemplate(
                information,
                trnPrefix,
                "*1591031071~                                                    HCCLAIMPMT",
                out payload))
        {
            return (10, payload);
        }

        if (TryStripAchPaymentTemplate(
                information,
                "TXP*337743360SOLE*012*20261231*T*",
                "\\",
                out payload))
        {
            return (11, payload);
        }

        // Unknown future Chase addenda remain lossless. They are simply stored
        // verbatim using the RAW format until a repeated structure is worth
        // assigning its own compact format ID.
        return (12, information);
    }

    private static bool TryStripAchPaymentTemplate(
        string information,
        string prefix,
        string suffix,
        out string payload)
    {
        if (!information.StartsWith(prefix, StringComparison.Ordinal) ||
            !information.EndsWith(suffix, StringComparison.Ordinal) ||
            information.Length < prefix.Length + suffix.Length)
        {
            payload = string.Empty;
            return false;
        }

        payload = information.Substring(
            prefix.Length,
            information.Length - prefix.Length - suffix.Length);

        return true;
    }

    private static bool TryEncodeDerivedCpPaymentInformation(
        string information,
        out string payload)
    {
        const string prefix = "TRN*1*";
        const string marker = "*1361236610*CP ";
        const string suffix = "-1376879510\\";

        payload = string.Empty;

        if (!information.StartsWith(prefix, StringComparison.Ordinal) ||
            !information.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        int markerIndex = information.IndexOf(
            marker,
            prefix.Length,
            StringComparison.Ordinal);

        if (markerIndex < prefix.Length)
            return false;

        string candidate = information.Substring(
            prefix.Length,
            markerIndex - prefix.Length);

        // The compact CP form is only used when the entire original string can
        // be deterministically reconstructed from the reference.
        if (candidate.Length < 7 ||
            candidate[0] != 'C' ||
            candidate[6] != 'E' ||
            !int.TryParse(
                candidate.AsSpan(1, 2),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int twoDigitYear) ||
            !int.TryParse(
                candidate.AsSpan(3, 3),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int dayOfYear))
        {
            return false;
        }

        int year = 2000 + twoDigitYear;
        int daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;

        if (dayOfYear < 1 || dayOfYear > daysInYear)
            return false;

        string date = new DateOnly(year, 1, 1)
            .AddDays(dayOfYear - 1)
            .ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        string reconstructed =
            prefix
            + candidate
            + marker
            + date
            + candidate[6..]
            + "0"
            + suffix;

        if (!string.Equals(
                reconstructed,
                information,
                StringComparison.Ordinal))
        {
            return false;
        }

        payload = candidate;
        return true;
    }

    private static long GetOrCreateTextLookupId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string valueColumn,
        string value)
    {
        // tableName and valueColumn are compile-time constants supplied only
        // by this class. Values themselves remain SQL parameters.
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                $"SELECT Id FROM {tableName} WHERE {valueColumn} = $value;";

            select.Parameters.AddWithValue("$value", value);

            object? existing = select.ExecuteScalar();

            if (existing is not null)
            {
                return Convert.ToInt64(
                    existing,
                    CultureInfo.InvariantCulture);
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;

        insert.CommandText =
            $"INSERT INTO {tableName} ({valueColumn}) VALUES ($value); " +
            "SELECT last_insert_rowid();";

        insert.Parameters.AddWithValue("$value", value);

        return Convert.ToInt64(
            insert.ExecuteScalar(),
            CultureInfo.InvariantCulture);
    }

    private static long? GetOrCreateNullableTextLookupId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string valueColumn,
        string? value)
    {
        if (value is null)
            return null;

        return GetOrCreateTextLookupId(
            connection,
            transaction,
            tableName,
            valueColumn,
            value);
    }

    private static long GetOrCreateAchOriginator(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string companyId,
        string companyName)
    {
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT Id
                FROM AchOriginators
                WHERE CompanyId = $companyId
                  AND CompanyName = $companyName;
                """;

            select.Parameters.AddWithValue(
                "$companyId",
                companyId);
            select.Parameters.AddWithValue(
                "$companyName",
                companyName);

            object? existing = select.ExecuteScalar();

            if (existing is not null)
            {
                return Convert.ToInt64(
                    existing,
                    CultureInfo.InvariantCulture);
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;

        insert.CommandText = """
            INSERT INTO AchOriginators
            (
                CompanyId,
                CompanyName
            )
            VALUES
            (
                $companyId,
                $companyName
            );

            SELECT last_insert_rowid();
            """;

        insert.Parameters.AddWithValue(
            "$companyId",
            companyId);
        insert.Parameters.AddWithValue(
            "$companyName",
            companyName);

        return Convert.ToInt64(
            insert.ExecuteScalar(),
            CultureInfo.InvariantCulture);
    }

    private static long GetOrCreateTransferCounterparty(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string institution,
        string accountLabel,
        string last4)
    {
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT Id
                FROM TransferCounterparties
                WHERE Institution = $institution
                  AND AccountLabel = $accountLabel
                  AND Last4 = $last4;
                """;

            select.Parameters.AddWithValue(
                "$institution",
                institution);
            select.Parameters.AddWithValue(
                "$accountLabel",
                accountLabel);
            select.Parameters.AddWithValue(
                "$last4",
                last4);

            object? existing = select.ExecuteScalar();

            if (existing is not null)
            {
                return Convert.ToInt64(
                    existing,
                    CultureInfo.InvariantCulture);
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;

        insert.CommandText = """
            INSERT INTO TransferCounterparties
            (
                Institution,
                AccountLabel,
                Last4
            )
            VALUES
            (
                $institution,
                $accountLabel,
                $last4
            );

            SELECT last_insert_rowid();
            """;

        insert.Parameters.AddWithValue(
            "$institution",
            institution);
        insert.Parameters.AddWithValue(
            "$accountLabel",
            accountLabel);
        insert.Parameters.AddWithValue(
            "$last4",
            last4);

        return Convert.ToInt64(
            insert.ExecuteScalar(),
            CultureInfo.InvariantCulture);
    }

    private static void AddCommonParameters(
        SqliteCommand command,
        ParsedTransaction parsed)
    {
        command.Parameters.AddWithValue(
            "$account",
            parsed.AccountLast4);
        command.Parameters.AddWithValue(
            "$postingDate",
            parsed.PostingDateUnixSeconds);
        command.Parameters.AddWithValue(
            "$amount",
            parsed.AmountCents);
        command.Parameters.AddWithValue(
            "$type",
            parsed.TypeCode);
    }

    private static void AddDepositParameters(
        SqliteCommand command,
        ParsedTransaction parsed)
    {
        if (parsed.Deposit is null)
            throw new InvalidOperationException(
                "Deposit parameters requested for a non-deposit transaction.");

        AddCommonParameters(command, parsed);

        command.Parameters.AddWithValue(
            "$details",
            parsed.Deposit.DetailsCode);
        AddNullableInt64(
            command,
            "$balance",
            parsed.Deposit.BalanceCents);
        AddNullableText(
            command,
            "$check",
            parsed.Deposit.CheckOrSlipNumber);
    }

    private static void AddNullableText(
        SqliteCommand command,
        string name,
        string? value)
    {
        command.Parameters.AddWithValue(
            name,
            value is null ? DBNull.Value : value);
    }

    private static void AddNullableInt64(
        SqliteCommand command,
        string name,
        long? value)
    {
        command.Parameters.AddWithValue(
            name,
            value.HasValue ? value.Value : DBNull.Value);
    }

    private static List<long> ReadIds(SqliteCommand command)
    {
        var ids = new List<long>();

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
            ids.Add(reader.GetInt64(0));

        return ids;
    }

    private static FileIdentity ParseFileIdentity(string fileName)
    {
        Match match = ChaseFileNameRegex.Match(fileName);

        if (!match.Success)
        {
            throw new InvalidDataException(
                "The filename must match Chase####_Activity_YYYYMMDD.csv " +
                "(an optional copy suffix like (1) is allowed).");
        }

        DateOnly downloadDate;

        if (!DateOnly.TryParseExact(
                match.Groups["date"].Value,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out downloadDate))
        {
            throw new InvalidDataException(
                $"The download date in \"{fileName}\" is invalid.");
        }

        return new FileIdentity(
            match.Groups["last4"].Value,
            ToUnixMidnight(downloadDate));
    }

    private static ChaseFormat DetectFormat(string[] header)
    {
        if (header.SequenceEqual(
                CreditCardHeader,
                StringComparer.Ordinal))
        {
            return new ChaseFormat(
                ChaseFormatKind.CreditCard,
                "ChaseCreditCardActivity",
                CreditCardHeader.Length);
        }

        if (header.SequenceEqual(
                DepositHeader,
                StringComparer.Ordinal))
        {
            return new ChaseFormat(
                ChaseFormatKind.Deposit,
                "ChaseDepositActivity",
                DepositHeader.Length);
        }

        throw new InvalidDataException(
            "This CSV does not match either known Chase download format.\n\n" +
            "Header found:\n" +
            string.Join(" | ", header));
    }

    private static void ValidateExtraFields(
        string[] record,
        int expectedColumnCount,
        int sourceRowNumber)
    {
        if (record.Length < expectedColumnCount)
        {
            throw new InvalidDataException(
                $"CSV row {sourceRowNumber} has only {record.Length} fields; " +
                $"{expectedColumnCount} were expected.");
        }

        for (int i = expectedColumnCount; i < record.Length; i++)
        {
            if (record[i].Length != 0)
            {
                throw new InvalidDataException(
                    $"CSV row {sourceRowNumber} contains unexpected data in " +
                    $"unnamed field {i + 1}: \"{record[i]}\".");
            }
        }
    }

    private static byte[] ComputeSha256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return SHA256.HashData(stream);
    }

    private static DateOnly ParseDate(
        string text,
        string format,
        int sourceRowNumber,
        string fieldName)
    {
        if (!DateOnly.TryParseExact(
                text,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly value))
        {
            throw new InvalidDataException(
                $"CSV row {sourceRowNumber} has an invalid {fieldName}: \"{text}\".");
        }

        return value;
    }

    private static long ParseDateUnixSeconds(
        string text,
        string format,
        int sourceRowNumber,
        string fieldName)
    {
        return ToUnixMidnight(
            ParseDate(
                text,
                format,
                sourceRowNumber,
                fieldName));
    }

    private static DateOnly ParseYYMMDD(
        string text,
        DateOnly nearbyDate)
    {
        if (text.Length != 6
            || !int.TryParse(
                text[..2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int twoDigitYear)
            || !int.TryParse(
                text.Substring(2, 2),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int month)
            || !int.TryParse(
                text.Substring(4, 2),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int day))
        {
            throw new FormatException(
                $"Invalid ACH Effective Entry Date: \"{text}\".");
        }

        int century = nearbyDate.Year / 100 * 100;
        int year = century + twoDigitYear;

        if (year - nearbyDate.Year > 50)
            year -= 100;
        else if (nearbyDate.Year - year > 50)
            year += 100;

        return new DateOnly(year, month, day);
    }

    private static long ToUnixMidnight(DateOnly date)
    {
        DateTime utc = new(
            date.Year,
            date.Month,
            date.Day,
            0,
            0,
            0,
            DateTimeKind.Utc);

        return new DateTimeOffset(utc).ToUnixTimeSeconds();
    }

    private static long ParseRequiredCents(
        string text,
        int sourceRowNumber,
        string fieldName)
    {
        return ParseCents(
            text,
            sourceRowNumber,
            fieldName,
            required: true)
            ?? throw new InvalidDataException(
                $"CSV row {sourceRowNumber} has a blank required {fieldName}.");
    }

    private static long? ParseOptionalCents(
        string text,
        int sourceRowNumber,
        string fieldName)
    {
        return ParseCents(
            text,
            sourceRowNumber,
            fieldName,
            required: false);
    }

    private static long? ParseCents(
        string text,
        int sourceRowNumber,
        string fieldName,
        bool required)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (required)
            {
                throw new InvalidDataException(
                    $"CSV row {sourceRowNumber} has a blank required {fieldName}.");
            }

            return null;
        }

        if (!decimal.TryParse(
                text,
                NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out decimal value))
        {
            throw new InvalidDataException(
                $"CSV row {sourceRowNumber} has an invalid {fieldName}: \"{text}\".");
        }

        decimal exactCents = value * 100m;

        if (decimal.Truncate(exactCents) != exactCents)
        {
            throw new InvalidDataException(
                $"CSV row {sourceRowNumber} has more than two decimal places " +
                $"in {fieldName}: \"{text}\".");
        }

        return checked(decimal.ToInt64(exactCents));
    }

    private static bool MonthDayMatches(
        string monthDay,
        DateOnly date)
    {
        return string.Equals(
            monthDay,
            date.ToString("MM/dd", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static string? NullIfEmpty(string value)
    {
        return value.Length == 0 ? null : value;
    }

    private readonly record struct FileIdentity(
        string AccountLast4,
        long DownloadDateUnixSeconds);

    private readonly record struct CompactAchTrace(
        long? OdfiId,
        int? Sequence,
        string? Override);

    private readonly record struct ChaseFormat(
        ChaseFormatKind Kind,
        string Name,
        int ExpectedColumnCount);

    private enum ChaseFormatKind
    {
        CreditCard,
        Deposit
    }

    private sealed record ParsedTransaction(
        string AccountLast4,
        long PostingDateUnixSeconds,
        long AmountCents,
        string TypeCode,
        DepositData? Deposit,
        CreditCardData? CreditCard,
        DescriptionData? Description);

    private sealed record DepositData(
        string DetailsCode,
        long? BalanceCents,
        string? CheckOrSlipNumber);

    private sealed record CreditCardData(
        long TransactionDateUnixSeconds,
        string MerchantName,
        string? CategoryName,
        string? Memo);

    private abstract record DescriptionData;

    private sealed record NoDescriptionData : DescriptionData
    {
        public static NoDescriptionData Instance { get; } = new();

        private NoDescriptionData()
        {
        }
    }

    private sealed record AchDescriptionData(
        string CompanyId,
        string CompanyName,
        string? CompanyDescriptiveDate,
        string EntryDescription,
        string SecCode,
        string? TraceNumber,
        long? EffectiveEntryDateUnixSeconds,
        string? IndividualId,
        string? IndividualName,
        string? PaymentRelatedInformation,
        string? BankReferenceKind,
        string? BankReference)
        : DescriptionData;

    private sealed record AccountTransferData(
        string Direction,
        bool IsRealtime,
        string Institution,
        string AccountLabel,
        string CounterpartyLast4,
        string ChaseTransactionNumber,
        string? ChaseReference)
        : DescriptionData;

    private sealed record ChaseCardPaymentData(
        string TargetCardLast4)
        : DescriptionData;

    private sealed record DebitCardDescriptionData(
        string MerchantDescriptor)
        : DescriptionData;

    private sealed record AtmDescriptionData(
        string Action,
        string? TerminalId,
        string? Location)
        : DescriptionData;

    private sealed record FeeDescriptionData(
        string Description)
        : DescriptionData;

    private sealed record RealTimePaymentDescriptionData(
        string AbaRoutingNumber,
        string Sender,
        string Reference,
        string OriginatorCompanyId,
        string PaymentCode,
        string? Tin,
        string? Npi,
        string? ReceiverName,
        string? Purpose,
        string InstructionId,
        int ReceivedSecondOfDay,
        string BankReference)
        : DescriptionData;

    private sealed record UnparsedDescriptionData(
        string Description)
        : DescriptionData;
}

using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.Sqlite;
using SoloPractice.Data;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SoloPractice.Services;

internal sealed record ChaseImportResult(string FileName, string AccountLast4, string? FormatName,
    int RowsRead, int NewTransactions, int ReusedTransactions, int UnparsedDescriptions,
    bool FileAlreadyImported);

internal static class ChaseCsvImporter
{
    private static readonly string[] CreditCardHeader = ["Card", "Transaction Date", "Post Date", "Description", "Category", "Type", "Amount", "Memo"];
    private static readonly string[] DepositHeader = ["Details", "Posting Date", "Description", "Amount", "Type", "Balance", "Check or Slip #"];
    private static readonly Regex ChaseFileNameRegex = Rx(@"^Chase(?<last4>\d{4})_Activity_(?<date>\d{8})(?:\s*\(\d+\))?\.csv$", RegexOptions.IgnoreCase);
    private static readonly Regex FullAchRegex = Rx(@"^ORIG CO NAME:(?<company>.*?)\s+ORIG ID:(?<originatorId>.*?)\s+DESC DATE:(?<descriptiveDate>.*?)\s+CO ENTRY DESCR:(?<entryDescription>.*?)\s*SEC:(?<sec>\S+)\s+TRACE#:(?<trace>\S+)\s+EED:(?<eed>\S+)\s+IND ID:(?<individualId>.*?)\s+IND NAME:(?<tail>.*)$");
    private static readonly Regex ShortAchRegex = Rx(@"^ORIG CO NAME:(?<company>.*?)\s+CO ENTRY DESCR:(?<entryDescription>.*?)\s+SEC:(?<sec>\S+)\s+IND ID:(?<individualId>.*?)\s+ORIG ID:(?<originatorId>\S+)\s*$");
    private static readonly Regex AchBankReferenceRegex = Rx(@"\s+(?<kind>PAYABLE TRN|EDI TRN|TRN):\s*(?<reference>\S+)\s*$");
    private static readonly Regex RealtimeTransferRegex = Rx(@"^Online Realtime Transfer to Personal Checking Acct\s+(?<last4>\d{4}) transaction#:\s*(?<transaction>\d+) reference#:\s*(?<reference>\S+) (?<monthDay>\d{2}/\d{2})$");
    private static readonly Regex ToMmaTransferRegex = Rx(@"^Online Transfer to MMA \.\.\.(?<last4>\d{4}) transaction#:\s*(?<transaction>\d+) (?<monthDay>\d{2}/\d{2})$");
    private static readonly Regex FromChkTransferRegex = Rx(@"^Online Transfer from CHK \.\.\.(?<last4>\d{4}) transaction#:\s*(?<transaction>\d+)$");
    private static readonly Regex NamedTransferRegex = Rx(@"^Online Transfer (?<leading>\d+) to (?<name>.+?) (?<mask>#+)(?<last4>\d{4}) transaction #:\s*(?<transaction>\d+) (?<monthDay>\d{2}/\d{2})$");
    private static readonly Regex ChaseCardPaymentRegex = Rx(@"^Payment to Chase card ending in (?<last4>\d{4}) (?<monthDay>\d{2}/\d{2})$");
    private static readonly Regex CheckPaidRegex = Rx(@"^CHECK\s+(?<number>\d+)\s*(?<monthDay>\d{2}/\d{2})?\s*$");
    private static readonly Regex RemoteDepositRegex = Rx(@"^REMOTE ONLINE DEPOSIT #\s*(?<number>\d+)\s*$");
    private static readonly Regex DebitCardRegex = Rx(@"^(?<merchant>.*?)\s+(?<monthDay>\d{2}/\d{2})$");
    private static readonly Regex AtmWithdrawalRegex = Rx(@"^ATM WITHDRAWAL\s+(?<terminal>\d+)\s+(?<monthDay>\d{2}/\d{2})(?<location>.*)$");
    private static readonly Regex AtmDepositRegex = Rx(@"^ATM CASH DEPOSIT (?<monthDay>\d{2}/\d{2}) (?<location>.+)$");
    private static readonly Regex RealTimePaymentRegex = Rx(@"^REAL TIME PAYMENT CREDIT RECD FROM ABA/CONTR BNK-(?<aba>\d+)\s+FROM:\s*(?<sender>.*?)\s+REF:\s*(?<reference>\S+)\s+INFO:\s*(?<info>.*?)\s+IID:\s*(?<iid>\S+)\s+RECD:\s*(?<time>\d{2}:\d{2}:\d{2})\s+TRN:\s*(?<bankReference>\S+)\s*$");
    private static readonly Regex RealTimePaymentInfoRegex = Rx(@"^TEXT-RmtInf-TRN\*1\*(?<reference>[^*~]+)\*(?<originatorId>[^*~]+)\*(?<paymentCode>[^~]+)~(?:(?:TIN(?<tin>[^*]+)\*NPI(?<npi>[^*]+)\*(?<receiverName>[^*]+)\*(?<purpose>.+))?)$");

    private static Regex Rx(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ChaseImportResult Import(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("No CSV path was supplied.", nameof(filePath));
        filePath = Path.GetFullPath(filePath.Trim().Trim('"'));
        if (!File.Exists(filePath)) throw new FileNotFoundException("The Chase CSV file does not exist.", filePath);

        FileIdentity identity = ParseFileIdentity(Path.GetFileName(filePath));
        byte[] sourceBytes = File.ReadAllBytes(filePath);
        byte[] sha256 = SHA256.HashData(sourceBytes);
        using var connection = Database.OpenConnection();
        if (ImportFileAlreadyExists(connection, sha256))
            return new(Path.GetFileName(filePath), identity.AccountLast4, null, 0, 0, 0, 0, true);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { TrimOptions = TrimOptions.None, IgnoreBlankLines = false };
        using var source = new MemoryStream(sourceBytes, writable: false);
        using var reader = new StreamReader(source, Encoding.UTF8, true);
        using var parser = new CsvParser(reader, config);
        if (!parser.Read()) throw new InvalidDataException("The CSV file is empty.");
        ChaseFormat format = DetectFormat(parser.Record?.ToArray() ?? throw new InvalidDataException("The CSV header could not be read."));

        using SqliteTransaction transaction = connection.BeginTransaction();
        var db = new ImportDatabase(connection, transaction);
        try
        {
            long accountId = db.RequireAccount(identity.AccountLast4);
            long formatId = db.Lookup("ImportFormats", "Name", format.Name);
            long importFileId = InsertImportFile(db, sha256, accountId, formatId, identity.DownloadDay);
            InsertImportSourceData(db, importFileId, sourceBytes);

            int rows = 0, added = 0, reused = 0, unparsed = 0;
            var occurrenceByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int sourceRow = 2; parser.Read(); sourceRow++)
            {
                string[] record = parser.Record?.ToArray() ?? throw new InvalidDataException($"Could not read CSV record {sourceRow}.");
                ValidateExtraFields(record, format.ExpectedColumnCount, sourceRow);
                string[] row = record.Take(format.ExpectedColumnCount).ToArray();
                ParsedTransaction parsed = format.Kind == ChaseFormatKind.CreditCard
                    ? ParseCreditCardRow(identity.AccountLast4, row, sourceRow)
                    : ParseDepositRow(identity.AccountLast4, row, sourceRow);
                if (parsed.Description is UnparsedDescriptionData) unparsed++;

                string key = parsed.ToString();
                int occurrence = occurrenceByKey.GetValueOrDefault(key);
                occurrenceByKey[key] = occurrence + 1;
                List<long> matches = FindExisting(db, parsed);
                long transactionId;
                if (occurrence < matches.Count) { transactionId = matches[occurrence]; reused++; }
                else { transactionId = InsertTransaction(db, parsed); added++; }
                db.Execute("INSERT INTO ImportRows(ImportFileId,SourceRowNumber,TransactionId) VALUES($f,$r,$t);",
                    ("$f", importFileId), ("$r", sourceRow), ("$t", transactionId));
                rows++;
            }
            if (rows == 0) throw new InvalidDataException("The Chase CSV contains a header but no transaction rows.");
            db.Execute("""
                UPDATE RealTimePayments
                SET OriginatorId=(SELECT Id FROM AchOriginators WHERE CompanyIdentifier=RawOriginatorIdentifier),
                    RawOriginatorIdentifier=NULL
                WHERE RawOriginatorIdentifier IS NOT NULL
                  AND EXISTS(SELECT 1 FROM AchOriginators WHERE CompanyIdentifier=RawOriginatorIdentifier);
                UPDATE RealTimePayments
                SET EntryDescriptionId=(SELECT Id FROM AchEntryDescriptions WHERE Description=RawPurpose),
                    RawPurpose=NULL
                WHERE RawPurpose IS NOT NULL
                  AND EXISTS(SELECT 1 FROM AchEntryDescriptions WHERE Description=RawPurpose);
                """);
            transaction.Commit();
            using (SqliteCommand optimize = connection.CreateCommand())
            {
                optimize.CommandText = "PRAGMA optimize;";
                optimize.ExecuteNonQuery();
            }
            return new(Path.GetFileName(filePath), identity.AccountLast4, format.Name, rows, added, reused, unparsed, false);
        }
        catch { transaction.Rollback(); throw; }
    }

    private static ParsedTransaction ParseCreditCardRow(string last4, string[] row, int number)
    {
        if (row[0] != last4) throw new InvalidDataException($"CSV row {number} says Card={row[0]}, but the filename identifies account {last4}.");
        return new(last4, ParseDay(row[2], "MM/dd/yyyy", number, "Post Date"), ParseRequiredCents(row[6], number, "Amount"), row[5], null,
            new(ParseDay(row[1], "MM/dd/yyyy", number, "Transaction Date"), row[3], NullIfEmpty(row[4]), NullIfEmpty(row[7])), null);
    }

    private static ParsedTransaction ParseDepositRow(string last4, string[] row, int number)
    {
        DateOnly date = ParseDate(row[1], "MM/dd/yyyy", number, "Posting Date");
        long amount = ParseRequiredCents(row[3], number, "Amount");
        string? check = NullIfEmpty(row[6].Trim());
        return new(last4, ToUnixDay(date), amount, row[4],
            new(row[0], ParseOptionalCents(row[5], number, "Balance"), check), null,
            ParseDepositDescription(row[4], row[2], date, check));
    }

    private static DescriptionData ParseDepositDescription(string type, string raw, DateOnly date, string? check)
    {
        try
        {
            return type switch
            {
                "ACH_CREDIT" or "ACH_DEBIT" => ParsedOrRaw(ParseAch(raw, date), raw),
                "ACCT_XFER" => ParsedOrRaw(ParseTransfer(raw, date), raw),
                "LOAN_PMT" => ParsedOrRaw(ParseCardPayment(raw, date), raw),
                "CHECK_PAID" => ValidateCheck(raw, date, check) ? NoDescriptionData.Instance : new UnparsedDescriptionData(raw),
                "CHECK_DEPOSIT" => ValidateDeposit(raw, check) ? NoDescriptionData.Instance : new UnparsedDescriptionData(raw),
                "DEBIT_CARD" => ParsedOrRaw(ParseDebit(raw, date), raw),
                "ATM" => ParsedOrRaw(ParseAtm(raw, date), raw),
                "FEE_TRANSACTION" => new FeeDescriptionData(raw),
                "MISC_CREDIT" => ParsedOrRaw(ParseRtp(raw), raw),
                _ => new UnparsedDescriptionData(raw)
            };
        }
        catch { return new UnparsedDescriptionData(raw); }
    }

    private static DescriptionData ParsedOrRaw(DescriptionData? value, string raw) => value ?? new UnparsedDescriptionData(raw);

    private static AchDescriptionData? ParseAch(string description, DateOnly posting)
    {
        Match m = FullAchRegex.Match(description);
        if (m.Success)
        {
            Match bank = AchBankReferenceRegex.Match(m.Groups["tail"].Value);
            if (!bank.Success) return null;
            string receiver = m.Groups["tail"].Value[..bank.Index].TrimEnd();
            int i = FirstAddenda(receiver);
            return new(m.Groups["originatorId"].Value.Trim(), m.Groups["company"].Value.Trim(), NullIfEmpty(m.Groups["descriptiveDate"].Value.Trim()),
                m.Groups["entryDescription"].Value.Trim(), m.Groups["sec"].Value.Trim(), NullIfEmpty(m.Groups["trace"].Value.Trim()),
                ToUnixDay(ParseYYMMDD(m.Groups["eed"].Value.Trim(), posting)), NullIfEmpty(m.Groups["individualId"].Value.Trim()),
                NullIfEmpty((i < 0 ? receiver : receiver[..i]).Trim()), i < 0 ? null : NullIfEmpty(receiver[i..].Trim()),
                bank.Groups["kind"].Value, bank.Groups["reference"].Value.Trim());
        }
        m = ShortAchRegex.Match(description);
        return !m.Success ? null : new(m.Groups["originatorId"].Value.Trim(), m.Groups["company"].Value.Trim(), null,
            m.Groups["entryDescription"].Value.Trim(), m.Groups["sec"].Value.Trim(), null, null,
            NullIfEmpty(m.Groups["individualId"].Value.Trim()), null, null, null, null);
    }

    private static int FirstAddenda(string value)
    {
        int trn = value.IndexOf("TRN*", StringComparison.Ordinal), txp = value.IndexOf("TXP*", StringComparison.Ordinal);
        return trn < 0 ? txp : txp < 0 ? trn : Math.Min(trn, txp);
    }

    private static AccountTransferData? ParseTransfer(string text, DateOnly date)
    {
        Match m = RealtimeTransferRegex.Match(text);
        if (m.Success) return MonthDayMatches(m.Groups["monthDay"].Value, date) ? new("TO", true, "Chase", "Personal Checking Acct", m.Groups["last4"].Value, m.Groups["transaction"].Value, m.Groups["reference"].Value) : null;
        m = ToMmaTransferRegex.Match(text);
        if (m.Success) return MonthDayMatches(m.Groups["monthDay"].Value, date) ? new("TO", false, "Chase", "MMA", m.Groups["last4"].Value, m.Groups["transaction"].Value, null) : null;
        m = FromChkTransferRegex.Match(text);
        if (m.Success) return new("FROM", false, "Chase", "CHK", m.Groups["last4"].Value, m.Groups["transaction"].Value, null);
        m = NamedTransferRegex.Match(text);
        if (!m.Success || !MonthDayMatches(m.Groups["monthDay"].Value, date) || m.Groups["leading"].Value != m.Groups["transaction"].Value) return null;
        string name = m.Groups["name"].Value;
        (string institution, string label) = name switch { "My Discover Bank Savings" => ("Discover Bank", "Savings"), "Personal Checking Acct" => ("Chase", "Personal Checking Acct"), _ => (name, name) };
        return new("TO", false, institution, label, m.Groups["last4"].Value, m.Groups["transaction"].Value, null);
    }

    private static ChaseCardPaymentData? ParseCardPayment(string text, DateOnly date)
    { Match m = ChaseCardPaymentRegex.Match(text); return m.Success && MonthDayMatches(m.Groups["monthDay"].Value, date) ? new(m.Groups["last4"].Value) : null; }
    private static bool ValidateCheck(string text, DateOnly date, string? check)
    { Match m = CheckPaidRegex.Match(text); return m.Success && check is not null && m.Groups["number"].Value == check && (m.Groups["monthDay"].Value.Length == 0 || MonthDayMatches(m.Groups["monthDay"].Value, date)); }
    private static bool ValidateDeposit(string text, string? check)
    { Match m = RemoteDepositRegex.Match(text); return m.Success && check is not null && m.Groups["number"].Value == check; }
    private static DebitCardDescriptionData? ParseDebit(string text, DateOnly date)
    { Match m = DebitCardRegex.Match(text); return m.Success && MonthDayMatches(m.Groups["monthDay"].Value, date) ? new(m.Groups["merchant"].Value.TrimEnd()) : null; }
    private static AtmDescriptionData? ParseAtm(string text, DateOnly date)
    {
        Match m = AtmWithdrawalRegex.Match(text);
        if (m.Success) return MonthDayMatches(m.Groups["monthDay"].Value, date) ? new(1, NullIfEmpty(m.Groups["terminal"].Value.Trim()), NullIfEmpty(m.Groups["location"].Value.Trim())) : null;
        m = AtmDepositRegex.Match(text);
        return m.Success && MonthDayMatches(m.Groups["monthDay"].Value, date) ? new(2, null, NullIfEmpty(m.Groups["location"].Value.Trim())) : null;
    }
    private static RealTimePaymentDescriptionData? ParseRtp(string text)
    {
        Match m = RealTimePaymentRegex.Match(text); if (!m.Success) return null;
        Match info = RealTimePaymentInfoRegex.Match(m.Groups["info"].Value); if (!info.Success || info.Groups["reference"].Value != m.Groups["reference"].Value) return null;
        if (!TimeOnly.TryParseExact(m.Groups["time"].Value, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly time)) return null;
        return new(m.Groups["aba"].Value, m.Groups["sender"].Value.Trim(), m.Groups["reference"].Value, info.Groups["originatorId"].Value,
            info.Groups["paymentCode"].Value, NullIfEmpty(info.Groups["tin"].Value), NullIfEmpty(info.Groups["npi"].Value),
            NullIfEmpty(info.Groups["receiverName"].Value), NullIfEmpty(info.Groups["purpose"].Value), m.Groups["iid"].Value,
            time.Hour * 3600 + time.Minute * 60 + time.Second, m.Groups["bankReference"].Value);
    }

    private static List<long> FindExisting(ImportDatabase db, ParsedTransaction p)
    {
        long account = db.RequireAccount(p.AccountLast4), type = db.Lookup("TransactionTypes", "Code", p.TypeCode);
        const string common = "t.AccountId=$a AND t.PostingDay=$d AND t.AmountCents=$m AND t.TypeId=$y";
        var args = new List<(string, object?)> { ("$a", account), ("$d", p.PostingDay), ("$m", p.AmountCents), ("$y", type) };
        string sql;
        if (p.CreditCard is { } card)
        {
            long merchant = db.Lookup("MerchantDescriptors", "Descriptor", card.MerchantName);
            long? category = card.CategoryName is null ? null : db.Lookup("CreditCardCategories", "Name", card.CategoryName);
            sql = $"SELECT t.Id FROM Transactions t JOIN CreditCardTransactions c ON c.TransactionId=t.Id WHERE {common} AND c.TransactionDay=$td AND c.MerchantId=$merchant AND c.CategoryId IS $category AND c.Memo IS $memo ORDER BY t.Id;";
            args.AddRange([("$td", card.TransactionDay), ("$merchant", merchant), ("$category", category), ("$memo", card.Memo)]);
        }
        else
        {
            DepositData deposit = p.Deposit!;
            long? overrideId = DetailOverrideId(db, p);
            string depositJoin = " JOIN DepositTransactions d ON d.TransactionId=t.Id ";
            string depositWhere = " AND d.DetailsOverrideId IS $detail AND d.BalanceCents IS $balance AND d.CheckOrSlipNumber IS $check ";
            args.AddRange([("$detail", overrideId), ("$balance", deposit.BalanceCents), ("$check", deposit.CheckOrSlipNumber)]);
            switch (p.Description)
            {
                case AchDescriptionData a:
                    long profile = db.AchProfile(a); AddAchArgs(args, a, profile);
                    sql = $"SELECT t.Id FROM Transactions t{depositJoin}JOIN AchTransactions x ON x.TransactionId=t.Id LEFT JOIN vAchTransactions v ON v.Id=t.Id WHERE {common}{depositWhere}AND x.ProfileId=$profile AND x.CompanyDescriptiveDate IS $desc AND x.TraceNumber IS $trace AND x.EffectiveEntryDay IS $eed AND x.IndividualIdentifier IS $iid AND x.IndividualName IS $iname AND v.PaymentRelatedInformation IS $payment AND x.BankReference IS $bank ORDER BY t.Id;";
                    break;
                case AccountTransferData x:
                    sql = $"SELECT t.Id FROM Transactions t{depositJoin}JOIN vAccountTransfers v ON v.Id=t.Id WHERE {common}{depositWhere}AND v.Direction=$direction AND v.IsRealtime=$rt AND v.Institution=$institution AND v.AccountLabel=$label AND v.CounterpartyLast4=$last4 AND v.ChaseTransactionNumber=$number AND v.ChaseReference IS $reference ORDER BY t.Id;";
                    args.AddRange([("$direction", x.Direction), ("$rt", x.IsRealtime ? 1 : 0), ("$institution", x.Institution), ("$label", x.AccountLabel), ("$last4", x.CounterpartyLast4), ("$number", x.ChaseTransactionNumber), ("$reference", x.ChaseReference)]);
                    break;
                case ChaseCardPaymentData x:
                    sql = $"SELECT t.Id FROM Transactions t{depositJoin}JOIN ChaseCardPayments x ON x.TransactionId=t.Id WHERE {common}{depositWhere}AND x.TargetAccountId=$target ORDER BY t.Id;";
                    args.Add(("$target", db.RequireAccount(x.TargetCardLast4))); break;
                case DebitCardDescriptionData x:
                    sql = $"SELECT t.Id FROM Transactions t{depositJoin}JOIN DebitCardTransactions x ON x.TransactionId=t.Id WHERE {common}{depositWhere}AND x.MerchantId=$merchant ORDER BY t.Id;";
                    args.Add(("$merchant", db.Lookup("MerchantDescriptors", "Descriptor", x.MerchantDescriptor))); break;
                case AtmDescriptionData x:
                    sql = $"SELECT t.Id FROM Transactions t{depositJoin}JOIN AtmTransactions x ON x.TransactionId=t.Id WHERE {common}{depositWhere}AND x.ActionId=$action AND x.TerminalId IS $terminal AND x.Location IS $location ORDER BY t.Id;";
                    args.AddRange([("$action", x.ActionId), ("$terminal", x.TerminalId), ("$location", x.Location)]); break;
                case FeeDescriptionData x:
                    sql = $"SELECT t.Id FROM Transactions t{depositJoin}JOIN FeeTransactions x ON x.TransactionId=t.Id WHERE {common}{depositWhere}AND x.Description=$description ORDER BY t.Id;";
                    args.Add(("$description", x.Description)); break;
                case RealTimePaymentDescriptionData x:
                    sql = $"SELECT t.Id FROM Transactions t{depositJoin}JOIN RealTimePayments x ON x.TransactionId=t.Id JOIN RealTimePaymentSenders s ON s.Id=x.SenderId LEFT JOIN AchOriginators o ON o.Id=x.OriginatorId LEFT JOIN AchEntryDescriptions e ON e.Id=x.EntryDescriptionId WHERE {common}{depositWhere}AND x.AbaRoutingNumber=$aba AND s.Name=$sender AND x.Reference=$reference AND coalesce(o.CompanyIdentifier,x.RawOriginatorIdentifier)=$originator AND x.PaymentCode=$code AND x.Tin IS $tin AND x.Npi IS $npi AND x.ReceiverName IS $receiver AND coalesce(e.Description,x.RawPurpose) IS $purpose AND x.InstructionId=$instruction AND x.ReceivedSecondOfDay=$second AND x.BankReference=$bank ORDER BY t.Id;";
                    args.AddRange([("$aba", long.Parse(x.AbaRoutingNumber, CultureInfo.InvariantCulture)), ("$sender", x.Sender), ("$reference", x.Reference), ("$originator", x.OriginatorCompanyId), ("$code", x.PaymentCode), ("$tin", x.Tin), ("$npi", x.Npi), ("$receiver", x.ReceiverName), ("$purpose", x.Purpose), ("$instruction", x.InstructionId), ("$second", x.ReceivedSecondOfDay), ("$bank", x.BankReference)]); break;
                case UnparsedDescriptionData x:
                    sql = $"SELECT t.Id FROM Transactions t{depositJoin}JOIN UnparsedDepositDescriptions x ON x.TransactionId=t.Id WHERE {common}{depositWhere}AND x.Description=$description ORDER BY t.Id;";
                    args.Add(("$description", x.Description)); break;
                default:
                    sql = $"SELECT t.Id FROM Transactions t{depositJoin}WHERE {common}{depositWhere}AND NOT EXISTS(SELECT 1 FROM AchTransactions x WHERE x.TransactionId=t.Id) AND NOT EXISTS(SELECT 1 FROM AccountTransfers x WHERE x.TransactionId=t.Id) AND NOT EXISTS(SELECT 1 FROM ChaseCardPayments x WHERE x.TransactionId=t.Id) AND NOT EXISTS(SELECT 1 FROM DebitCardTransactions x WHERE x.TransactionId=t.Id) AND NOT EXISTS(SELECT 1 FROM AtmTransactions x WHERE x.TransactionId=t.Id) AND NOT EXISTS(SELECT 1 FROM FeeTransactions x WHERE x.TransactionId=t.Id) AND NOT EXISTS(SELECT 1 FROM RealTimePayments x WHERE x.TransactionId=t.Id) AND NOT EXISTS(SELECT 1 FROM UnparsedDepositDescriptions x WHERE x.TransactionId=t.Id) ORDER BY t.Id;";
                    break;
            }
        }
        return db.Ids(sql, args);
    }

    private static void AddAchArgs(List<(string, object?)> args, AchDescriptionData a, long profile) =>
        args.AddRange([("$profile", profile), ("$desc", a.CompanyDescriptiveDate), ("$trace", a.TraceNumber), ("$eed", a.EffectiveEntryDay),
            ("$iid", a.IndividualId), ("$iname", a.IndividualName), ("$payment", a.PaymentRelatedInformation), ("$bank", a.BankReference)]);

    private static long InsertTransaction(ImportDatabase db, ParsedTransaction p)
    {
        long account = db.RequireAccount(p.AccountLast4), type = db.Lookup("TransactionTypes", "Code", p.TypeCode);
        long id = db.Scalar("INSERT INTO Transactions(AccountId,PostingDay,AmountCents,TypeId) VALUES($a,$d,$m,$t); SELECT last_insert_rowid();",
            ("$a", account), ("$d", p.PostingDay), ("$m", p.AmountCents), ("$t", type));
        if (p.CreditCard is { } card)
        {
            long merchant = db.Lookup("MerchantDescriptors", "Descriptor", card.MerchantName);
            long? category = card.CategoryName is null ? null : db.Lookup("CreditCardCategories", "Name", card.CategoryName);
            db.Execute("INSERT INTO CreditCardTransactions VALUES($id,$day,$merchant,$category,$memo);", ("$id", id), ("$day", card.TransactionDay), ("$merchant", merchant), ("$category", category), ("$memo", card.Memo));
            return id;
        }
        DepositData deposit = p.Deposit!;
        db.Execute("INSERT INTO DepositTransactions VALUES($id,$detail,$balance,$check);", ("$id", id), ("$detail", DetailOverrideId(db, p)), ("$balance", deposit.BalanceCents), ("$check", deposit.CheckOrSlipNumber));
        switch (p.Description)
        {
            case AchDescriptionData a: InsertAch(db, id, a); break;
            case AccountTransferData x: InsertTransfer(db, id, x); break;
            case ChaseCardPaymentData x: db.Execute("INSERT INTO ChaseCardPayments VALUES($id,$target);", ("$id", id), ("$target", db.RequireAccount(x.TargetCardLast4))); break;
            case DebitCardDescriptionData x: db.Execute("INSERT INTO DebitCardTransactions VALUES($id,$merchant);", ("$id", id), ("$merchant", db.Lookup("MerchantDescriptors", "Descriptor", x.MerchantDescriptor))); break;
            case AtmDescriptionData x: db.Execute("INSERT INTO AtmTransactions VALUES($id,$action,$terminal,$location);", ("$id", id), ("$action", x.ActionId), ("$terminal", x.TerminalId), ("$location", x.Location)); break;
            case FeeDescriptionData x: db.Execute("INSERT INTO FeeTransactions VALUES($id,$description);", ("$id", id), ("$description", x.Description)); break;
            case RealTimePaymentDescriptionData x: InsertRtp(db, id, x); break;
            case UnparsedDescriptionData x: db.Execute("INSERT INTO UnparsedDepositDescriptions VALUES($id,$description);", ("$id", id), ("$description", x.Description)); break;
        }
        return id;
    }

    private static long? DetailOverrideId(ImportDatabase db, ParsedTransaction p)
    {
        string inferred = p.TypeCode switch { "CHECK_PAID" => "CHECK", "CHECK_DEPOSIT" => "DSLIP", _ when p.AmountCents > 0 => "CREDIT", _ => "DEBIT" };
        return p.Deposit!.DetailsCode == inferred ? null : db.Lookup("DepositDetails", "Code", p.Deposit.DetailsCode);
    }

    private static void InsertAch(ImportDatabase db, long id, AchDescriptionData a)
    {
        long profile = db.AchProfile(a);
        AddendaData? addenda = ParseAddenda(a.PaymentRelatedInformation);
        string? raw = addenda is null ? a.PaymentRelatedInformation : null;
        db.Execute("INSERT INTO AchTransactions VALUES($id,$profile,$desc,$trace,$eed,$iid,$name,$raw,$bank);",
            ("$id", id), ("$profile", profile), ("$desc", a.CompanyDescriptiveDate), ("$trace", a.TraceNumber), ("$eed", a.EffectiveEntryDay),
            ("$iid", a.IndividualId), ("$name", a.IndividualName), ("$raw", raw), ("$bank", a.BankReference));
        switch (addenda)
        {
            case TrnAddenda t: db.Execute("INSERT INTO AchTrnAddenda VALUES($id,1,$reference,$originator,$extra,$terminator);", ("$id", id), ("$reference", t.Reference), ("$originator", t.OriginatorIdentifier), ("$extra", t.AdditionalText), ("$terminator", "\\")); break;
            case TaxAddenda t: db.Execute("INSERT INTO AchTaxPaymentAddenda VALUES($id,$taxpayer,$type,$period,$amountType,$amount,$terminator);", ("$id", id), ("$taxpayer", t.TaxpayerId), ("$type", t.TaxType), ("$period", t.TaxPeriod), ("$amountType", t.AmountType), ("$amount", t.AmountText), ("$terminator", "\\")); break;
        }
    }

    private static AddendaData? ParseAddenda(string? value)
    {
        if (value is null || !value.EndsWith('\\')) return null;
        string[] p = value[..^1].Split('*', 5);
        if (p.Length is 4 or 5 && p[0] == "TRN" && p[1] == "1" && p.Skip(2).All(x => x.Length > 0)) return new TrnAddenda(p[2], p[3], p.Length == 5 ? p[4] : null);
        string[] tax = value[..^1].Split('*');
        if (tax.Length == 6 && tax[0] == "TXP" && tax.Skip(1).All(x => x.Length > 0)) return new TaxAddenda(tax[1], tax[2], tax[3], tax[4], tax[5]);
        return null;
    }

    private static void InsertTransfer(ImportDatabase db, long id, AccountTransferData x)
    {
        long institution = db.Lookup("FinancialInstitutions", "Name", x.Institution);
        long? internalAccount = db.TryAccount(x.CounterpartyLast4);
        long? externalLast4 = internalAccount is null ? long.Parse(x.CounterpartyLast4, CultureInfo.InvariantCulture) : null;
        long counterparty = db.Counterparty(institution, x.AccountLabel, internalAccount, externalLast4);
        long direction = x.Direction == "TO" ? 1 : 2;
        db.Execute("INSERT INTO AccountTransfers VALUES($id,$direction,$rt,$counterparty,$number,$reference);", ("$id", id), ("$direction", direction), ("$rt", x.IsRealtime ? 1 : 0), ("$counterparty", counterparty), ("$number", x.ChaseTransactionNumber), ("$reference", x.ChaseReference));
    }

    private static void InsertRtp(ImportDatabase db, long id, RealTimePaymentDescriptionData x)
    {
        long sender = db.Lookup("RealTimePaymentSenders", "Name", x.Sender);
        long? originator = db.TryOriginator(x.OriginatorCompanyId);
        long? entry = x.Purpose is null ? null : db.TryLookup("AchEntryDescriptions", "Description", x.Purpose);
        db.Execute("INSERT INTO RealTimePayments VALUES($id,$aba,$sender,$reference,$originator,$rawOriginator,$code,$tin,$npi,$receiver,$entry,$rawPurpose,$instruction,$second,$bank);",
            ("$id", id), ("$aba", long.Parse(x.AbaRoutingNumber, CultureInfo.InvariantCulture)), ("$sender", sender), ("$reference", x.Reference),
            ("$originator", originator), ("$rawOriginator", originator is null ? x.OriginatorCompanyId : null), ("$code", x.PaymentCode), ("$tin", x.Tin),
            ("$npi", x.Npi), ("$receiver", x.ReceiverName), ("$entry", entry), ("$rawPurpose", entry is null ? x.Purpose : null),
            ("$instruction", x.InstructionId), ("$second", x.ReceivedSecondOfDay), ("$bank", x.BankReference));
    }

    private static long InsertImportFile(ImportDatabase db, byte[] sha, long account, long format, long day) =>
        db.Scalar("INSERT INTO ImportFiles(FileSha256,AccountId,FormatId,DownloadDay,ImportedAtUnixSeconds) VALUES($sha,$account,$format,$day,$at); SELECT last_insert_rowid();",
            ("$sha", sha), ("$account", account), ("$format", format), ("$day", day), ("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private static void InsertImportSourceData(ImportDatabase db, long fileId, byte[] source)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(source);
        db.Execute("INSERT INTO ImportSourceData VALUES($id,$data);", ("$id", fileId), ("$data", compressed.ToArray()));
    }

    private static bool ImportFileAlreadyExists(SqliteConnection connection, byte[] sha)
    {
        using var c = connection.CreateCommand(); c.CommandText = "SELECT EXISTS(SELECT 1 FROM ImportFiles WHERE FileSha256=$sha);"; c.Parameters.AddWithValue("$sha", sha);
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static FileIdentity ParseFileIdentity(string name)
    {
        Match m = ChaseFileNameRegex.Match(name); if (!m.Success) throw new InvalidDataException("The file name must match Chase####_Activity_yyyyMMdd.csv (optional numbered-copy suffix allowed).");
        if (!DateOnly.TryParseExact(m.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)) throw new InvalidDataException("The Chase download date in the file name is invalid.");
        return new(m.Groups["last4"].Value, ToUnixDay(date));
    }

    private static ChaseFormat DetectFormat(string[] header)
    {
        if (header.SequenceEqual(CreditCardHeader, StringComparer.Ordinal)) return new("ChaseCreditCardActivity", ChaseFormatKind.CreditCard, CreditCardHeader.Length);
        if (header.SequenceEqual(DepositHeader, StringComparer.Ordinal)) return new("ChaseDepositActivity", ChaseFormatKind.Deposit, DepositHeader.Length);
        throw new InvalidDataException("The CSV header is not a supported Chase download format.");
    }
    private static void ValidateExtraFields(string[] row, int expected, int number)
    { if (row.Length < expected || row.Skip(expected).Any(x => !string.IsNullOrEmpty(x))) throw new InvalidDataException($"CSV row {number} has an unexpected number of fields."); }
    private static DateOnly ParseDate(string value, string format, int row, string column)
    { if (!DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly result)) throw new InvalidDataException($"CSV row {row} has an invalid {column}: {value}"); return result; }
    private static long ParseDay(string value, string format, int row, string column) => ToUnixDay(ParseDate(value, format, row, column));
    private static DateOnly ParseYYMMDD(string value, DateOnly posting)
    {
        if (value.Length != 6 || !int.TryParse(value[..2], out int yy) || !int.TryParse(value.Substring(2, 2), out int month) || !int.TryParse(value.Substring(4, 2), out int day)) throw new FormatException("Invalid ACH effective-entry date.");
        int century = posting.Year / 100 * 100; DateOnly candidate = new(century + yy, month, day);
        if (candidate > posting.AddYears(50)) candidate = candidate.AddYears(-100); else if (candidate < posting.AddYears(-50)) candidate = candidate.AddYears(100);
        return candidate;
    }
    private static long ToUnixDay(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).Ticks / TimeSpan.TicksPerDay - DateTime.UnixEpoch.Ticks / TimeSpan.TicksPerDay;
    private static long ParseRequiredCents(string value, int row, string column) => ParseCents(value, row, column) ?? throw new InvalidDataException($"CSV row {row} has an empty {column}.");
    private static long? ParseOptionalCents(string value, int row, string column) => string.IsNullOrWhiteSpace(value) ? null : ParseCents(value, row, column);
    private static long? ParseCents(string value, int row, string column)
    {
        string normalized = value.Trim().Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
        if (!decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal amount) || decimal.Round(amount, 2) != amount) throw new InvalidDataException($"CSV row {row} has an invalid {column}: {value}");
        return checked((long)(amount * 100m));
    }
    private static bool MonthDayMatches(string value, DateOnly date) => DateOnly.TryParseExact($"{value}/{date.Year}", "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed) && parsed == date;
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed class ImportDatabase(SqliteConnection connection, SqliteTransaction transaction)
    {
        private readonly Dictionary<string, long> cache = new(StringComparer.Ordinal);
        public int Execute(string sql, params (string Name, object? Value)[] args) { using var c = Command(sql, args); return c.ExecuteNonQuery(); }
        public long Scalar(string sql, params (string Name, object? Value)[] args) { using var c = Command(sql, args); return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture); }
        public List<long> Ids(string sql, IEnumerable<(string Name, object? Value)> args) { using var c = Command(sql, args); using var r = c.ExecuteReader(); var ids = new List<long>(); while (r.Read()) ids.Add(r.GetInt64(0)); return ids; }
        private SqliteCommand Command(string sql, IEnumerable<(string Name, object? Value)> args) { var c = connection.CreateCommand(); c.Transaction = transaction; c.CommandText = sql; foreach ((string n, object? v) in args) c.Parameters.AddWithValue(n, v ?? DBNull.Value); return c; }
        public long Lookup(string table, string column, string value)
        {
            string key = table + "\0" + value; if (cache.TryGetValue(key, out long id)) return id;
            using (var select = Command($"SELECT Id FROM {table} WHERE {column}=$v;", [("$v", value)])) { object? found = select.ExecuteScalar(); if (found is not null) return cache[key] = Convert.ToInt64(found, CultureInfo.InvariantCulture); }
            return cache[key] = Scalar($"INSERT INTO {table}({column}) VALUES($v); SELECT last_insert_rowid();", ("$v", value));
        }
        public long? TryLookup(string table, string column, string value)
        { using var c = Command($"SELECT Id FROM {table} WHERE {column}=$v;", [("$v", value)]); object? found = c.ExecuteScalar(); return found is null ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture); }
        public long RequireAccount(string last4) => TryAccount(last4) ?? throw new InvalidDataException($"Chase account {last4} is not configured in Accounts.");
        public long? TryAccount(string last4) => TryLookup("Accounts", "Last4", long.Parse(last4, CultureInfo.InvariantCulture));
        private long? TryLookup(string table, string column, long value) { using var c = Command($"SELECT Id FROM {table} WHERE {column}=$v;", [("$v", value)]); object? found = c.ExecuteScalar(); return found is null ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture); }
        public long? TryOriginator(string identifier) => TryLookup("AchOriginators", "CompanyIdentifier", identifier);
        public long AchProfile(AchDescriptionData a)
        {
            long company = Lookup("AchCompanies", "Name", a.CompanyName);
            long originator = Originator(a.CompanyId, company);
            long entry = Lookup("AchEntryDescriptions", "Description", a.EntryDescription), sec = Lookup("AchSecCodes", "Code", a.SecCode);
            long? bank = a.BankReferenceKind is null ? null : Lookup("AchBankReferenceKinds", "Name", a.BankReferenceKind);
            using (var c = Command("SELECT Id FROM AchProfiles WHERE OriginatorId=$o AND EntryDescriptionId=$e AND SecCodeId=$s AND BankReferenceKindId IS $b;", [("$o", originator), ("$e", entry), ("$s", sec), ("$b", bank)])) { object? found = c.ExecuteScalar(); if (found is not null) return Convert.ToInt64(found, CultureInfo.InvariantCulture); }
            return Scalar("INSERT INTO AchProfiles(OriginatorId,EntryDescriptionId,SecCodeId,BankReferenceKindId) VALUES($o,$e,$s,$b); SELECT last_insert_rowid();", ("$o", originator), ("$e", entry), ("$s", sec), ("$b", bank));
        }
        private long Originator(string identifier, long company)
        {
            using var c = Command("SELECT Id,CompanyId FROM AchOriginators WHERE CompanyIdentifier=$i;", [("$i", identifier)]); using var r = c.ExecuteReader();
            if (r.Read()) { if (r.GetInt64(1) != company) throw new InvalidDataException($"ACH originator {identifier} changed company name; source retained but import stopped to avoid silently merging identities."); return r.GetInt64(0); }
            return Scalar("INSERT INTO AchOriginators(CompanyIdentifier,CompanyId) VALUES($i,$c); SELECT last_insert_rowid();", ("$i", identifier), ("$c", company));
        }
        public long Counterparty(long institution, string label, long? internalAccount, long? externalLast4)
        {
            using (var c = Command("SELECT Id FROM TransferCounterparties WHERE InstitutionId=$i AND AccountLabel=$l AND InternalAccountId IS $a AND ExternalLast4 IS $e;", [("$i", institution), ("$l", label), ("$a", internalAccount), ("$e", externalLast4)])) { object? found = c.ExecuteScalar(); if (found is not null) return Convert.ToInt64(found, CultureInfo.InvariantCulture); }
            return Scalar("INSERT INTO TransferCounterparties(InstitutionId,AccountLabel,InternalAccountId,ExternalLast4) VALUES($i,$l,$a,$e); SELECT last_insert_rowid();", ("$i", institution), ("$l", label), ("$a", internalAccount), ("$e", externalLast4));
        }
    }

    private readonly record struct FileIdentity(string AccountLast4, long DownloadDay);
    private readonly record struct ChaseFormat(string Name, ChaseFormatKind Kind, int ExpectedColumnCount);
    private enum ChaseFormatKind { CreditCard, Deposit }
    private sealed record ParsedTransaction(string AccountLast4, long PostingDay, long AmountCents, string TypeCode, DepositData? Deposit, CreditCardData? CreditCard, DescriptionData? Description);
    private sealed record DepositData(string DetailsCode, long? BalanceCents, string? CheckOrSlipNumber);
    private sealed record CreditCardData(long TransactionDay, string MerchantName, string? CategoryName, string? Memo);
    private abstract record DescriptionData;
    private sealed record NoDescriptionData : DescriptionData { public static NoDescriptionData Instance { get; } = new(); }
    private sealed record AchDescriptionData(string CompanyId, string CompanyName, string? CompanyDescriptiveDate, string EntryDescription, string SecCode, string? TraceNumber, long? EffectiveEntryDay, string? IndividualId, string? IndividualName, string? PaymentRelatedInformation, string? BankReferenceKind, string? BankReference) : DescriptionData;
    private sealed record AccountTransferData(string Direction, bool IsRealtime, string Institution, string AccountLabel, string CounterpartyLast4, string ChaseTransactionNumber, string? ChaseReference) : DescriptionData;
    private sealed record ChaseCardPaymentData(string TargetCardLast4) : DescriptionData;
    private sealed record DebitCardDescriptionData(string MerchantDescriptor) : DescriptionData;
    private sealed record AtmDescriptionData(int ActionId, string? TerminalId, string? Location) : DescriptionData;
    private sealed record FeeDescriptionData(string Description) : DescriptionData;
    private sealed record RealTimePaymentDescriptionData(string AbaRoutingNumber, string Sender, string Reference, string OriginatorCompanyId, string PaymentCode, string? Tin, string? Npi, string? ReceiverName, string? Purpose, string InstructionId, int ReceivedSecondOfDay, string BankReference) : DescriptionData;
    private sealed record UnparsedDescriptionData(string Description) : DescriptionData;
    private abstract record AddendaData;
    private sealed record TrnAddenda(string Reference, string OriginatorIdentifier, string? AdditionalText) : AddendaData;
    private sealed record TaxAddenda(string TaxpayerId, string TaxType, string TaxPeriod, string AmountType, string AmountText) : AddendaData;
}

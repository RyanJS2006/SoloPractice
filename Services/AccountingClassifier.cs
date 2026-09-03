namespace SoloPractice.Services;

internal sealed record AccountingSourceTransaction(
    long TransactionId,
    long AccountId,
    string AccountLast4,
    DateOnly AccountingDate,
    long AmountCents,
    string TypeCode,
    string? CheckNumber,
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
    string? CreditCardMerchant,
    string? CreditCardCategory,
    string? CreditCardMemo,
    int SourceRowNumber);

internal sealed record AccountingClassification(
    string GroupKey,
    string Description,
    string? Category,
    string? ReviewReason,
    bool Aggregate);

internal static class AccountingClassifier
{
    internal const string CheckingAccount = "8936";
    internal const string SavingsAccount = "9350";
    internal const string CreditCardAccount = "8027";

    public static AccountingClassification Classify(
        AccountingSourceTransaction row)
    {
        return row.AccountLast4 switch
        {
            CheckingAccount => ClassifyChecking(row),
            SavingsAccount => ClassifySavings(row),
            CreditCardAccount => ClassifyCreditCard(row),
            _ => Review(
                $"unknown-account:{row.TransactionId}",
                row.UnparsedDescription ?? row.CreditCardMerchant ?? row.TypeCode,
                $"No accounting classifier exists for account *{row.AccountLast4}.",
                aggregate: false)
        };
    }

    private static AccountingClassification ClassifyChecking(
        AccountingSourceTransaction row)
    {
        string type = row.TypeCode.ToUpperInvariant();
        string company = (row.AchCompanyName ?? string.Empty).Trim();
        string companyUpper = company.ToUpperInvariant();
        string entryUpper = (row.AchEntryDescription ?? string.Empty).ToUpperInvariant();

        if (type == "ACH_CREDIT")
        {
            if (companyUpper.Contains("STRIPE"))
                return Auto("checking:stripe", "Stripe payment processing through Simple Practice - Counseling fee", "Counselling Fee", true);
            if (companyUpper.Contains("BCBS"))
                return Auto("checking:bcbs", "BCBS payment - Counseling fee", "Counselling Fee", true);
            if (companyUpper.Contains("UNITEDHEALTHCARE"))
                return Auto("checking:united", "UBH payment - Counseling fee", "Counselling Fee", true);
            if (companyUpper.Contains("AETNA"))
                return Auto("checking:aetna", "Aetna payment - Counseling fee", "Counselling Fee", true);
            if (companyUpper.Contains("CIGNA"))
                return Auto("checking:cigna", "Cigna payment - Counseling fee", "Counselling Fee", true);
            if (companyUpper == "UMR")
                return Auto("checking:umr", "UMR payment - Counseling fee", "Counselling Fee", true);
            if (companyUpper.Contains("VENTANEX"))
                return Auto("checking:guidehealth", "GuideHealth Behavioral payment - Counseling fee", "Counselling Fee", true);

            if (companyUpper.Contains("HNB - ECHO"))
            {
                if (row.AmountCents == 6_975)
                    return Auto("checking:magellan", "HNB?Magellan - Counseling fee", "Counselling Fee", true);
                if (row.AmountCents == 3 && entryUpper.Contains("ACH XFR"))
                    return Auto("checking:guidehealth-ping", "GuideHealth Behavioral payment - Counseling fee", "Counselling Fee", true);
                return Auto("checking:meritain-echo", "Meritain Health payment - Counseling fee", "Counselling Fee", true);
            }

            if (entryUpper.Contains("HCCLAIMPMT") ||
                entryUpper.Contains("EOP") ||
                entryUpper.Contains("CLAIM"))
            {
                return Auto(
                    $"checking:claim:{companyUpper}",
                    $"{FriendlyCompanyName(company)} payment - Counseling fee",
                    "Counselling Fee",
                    true);
            }

            return Review(
                $"checking:ach-credit:{companyUpper}:{entryUpper}",
                string.IsNullOrWhiteSpace(company) ? "ACH credit" : $"{company} - {row.AchEntryDescription}",
                "Unrecognized ACH credit; choose the revenue category.",
                false);
        }

        if (type == "MISC_CREDIT")
        {
            string sender = row.RealTimeSender ?? string.Empty;
            if (sender.Contains("UnitedHealthcare", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(row.RealTimePurpose, "HCCLAIMPMT", StringComparison.OrdinalIgnoreCase))
            {
                return Auto("checking:united-realtime", "UBH payment - Counseling fee", "Counselling Fee", true);
            }

            return Review(
                $"checking:misc-credit:{sender}",
                string.IsNullOrWhiteSpace(sender) ? "Miscellaneous credit" : sender,
                "Unrecognized miscellaneous credit; verify the revenue category.",
                false);
        }

        if (type == "ACCT_XFER")
        {
            bool to = string.Equals(row.TransferDirection, "TO", StringComparison.OrdinalIgnoreCase);
            bool from = string.Equals(row.TransferDirection, "FROM", StringComparison.OrdinalIgnoreCase);

            if (to && (row.TransferLast4 == "0231" || row.TransferLast4 == "8153"))
                return Auto("checking:owners-draw", "Owners Draw", "Owners Draw", true);
            if (to && row.TransferLast4 == SavingsAccount)
                return Auto("checking:transfer-to-savings", $"Transfer to Savings *{SavingsAccount}", "Credit Card Payment, Transfers out and non-deductible exp", true);
            if (from && row.TransferLast4 == SavingsAccount)
                return Auto("checking:transfer-from-savings", $"Transfer from Savings *{SavingsAccount}", "Transfers In", true);

            string description =
                $"Transfer {row.TransferDirection} {row.TransferInstitution} " +
                $"{row.TransferAccountLabel} *{row.TransferLast4}";
            return Review(
                $"checking:transfer:{row.TransferDirection}:{row.TransferLast4}",
                description.Trim(),
                "Unrecognized transfer counterparty; verify whether it is an owner draw or business transfer.",
                true);
        }

        if (type == "LOAN_PMT" && row.TargetCardLast4 == CreditCardAccount)
            return Auto("checking:chase-card-payment", "Payment to Chase card", "Credit Card Payment, Transfers out and non-deductible exp", true);

        if (type == "ACH_DEBIT")
        {
            if (companyUpper == "IRS")
                return Auto("checking:irs-tax", "federal tax automatic withdrawal", "Payroll Taxes", true);
            if (companyUpper.Contains("IL DEPT OF REVEN"))
                return Auto("checking:illinois-tax", "Illinois Dept of Revenue-automatic deduction", "Payroll Taxes", true);
            if (LooksLikeLlcFee(companyUpper, entryUpper))
                return Auto($"checking:llc:{row.TransactionId}", $"{company} - {row.AchEntryDescription}".Trim(' ', '-'), "LLC Fee", false);
            if (LooksLikeLicenseFee(companyUpper, entryUpper))
                return Auto($"checking:license:{row.TransactionId}", $"{company} - {row.AchEntryDescription}".Trim(' ', '-'), "Professional Licenses Fee", false);

            return Review(
                $"checking:ach-debit:{companyUpper}:{entryUpper}",
                string.IsNullOrWhiteSpace(company) ? "ACH debit" : $"{company} - {row.AchEntryDescription}",
                "Unrecognized ACH debit; verify the expense category.",
                false);
        }

        if (type == "CHECK_PAID")
        {
            if (row.AmountCents == -40_500)
                return Auto($"checking:rent-check:{row.TransactionId}", "Rent - Collings LaGrange Mall, LLC", "Rent", false);
            if (row.AmountCents == -5_135)
                return Auto($"checking:wifi-check:{row.TransactionId}", "Kris Maynard, LCSW- reimbursement for Xfinity wifi", "WiFi Fee", false);
            if (row.CheckNumber == "1368" && row.AmountCents == -48_000)
                return Auto($"checking:illinois-check:{row.TransactionId}", "Illinois Dept of Revenue", "Payroll Taxes", false);

            return Review(
                $"checking:check:{row.TransactionId}",
                string.IsNullOrWhiteSpace(row.CheckNumber) ? "Check payment" : $"Check {row.CheckNumber}",
                "Chase does not provide the payee for this check; enter the description/category manually.",
                false);
        }

        if (type == "CHECK_DEPOSIT")
        {
            if (row.AmountCents is 5_902 or 9_605)
                return Auto("checking:meritain-check-deposit", "Meritain Health payment - Counseling fee", "Counselling Fee", true);
            if (row.AmountCents == 10_476)
                return Auto("checking:guidehealth-check-deposit", "GuideHealth Behavioral payment - Counseling fee", "Counselling Fee", true);

            return Review(
                $"checking:check-deposit:{row.TransactionId}",
                string.IsNullOrWhiteSpace(row.CheckNumber) ? "Check deposit" : $"Check deposit #{row.CheckNumber}",
                "Chase does not identify the check payer in this download; verify the revenue description/category.",
                false);
        }

        if (type == "DEBIT_CARD")
        {
            string merchant = row.DebitMerchant ?? string.Empty;
            if (merchant.Contains("MATRIX", StringComparison.OrdinalIgnoreCase))
                return Auto($"checking:matrix:{row.TransactionId}", "CEUs workshops - MATRIX CEUMATRIX.COM", "Licenses & Dues", false);
            if (merchant.Contains("XFINITY", StringComparison.OrdinalIgnoreCase))
                return Auto($"checking:xfinity:{row.TransactionId}", merchant, "WiFi Fee", false);

            return Review(
                $"checking:debit-card:{row.TransactionId}",
                string.IsNullOrWhiteSpace(merchant) ? "Debit card purchase" : merchant,
                "Unrecognized debit-card purchase; verify the expense category.",
                false);
        }

        if (type == "ATM" && string.Equals(row.AtmAction, "WITHDRAWAL", StringComparison.OrdinalIgnoreCase))
            return Auto($"checking:atm-owner-draw:{row.TransactionId}", "Owners Draw", "Owners Draw", false);
        if (type == "FEE_TRANSACTION")
            return Auto($"checking:fee:{row.TransactionId}", row.FeeDescription ?? "Bank fee", "Misc. Expense", false);

        string fallback = row.UnparsedDescription ?? row.DebitMerchant ?? row.FeeDescription ?? row.TypeCode;
        return Review(
            $"checking:unknown:{row.TransactionId}",
            fallback,
            $"No accounting rule exists yet for Chase transaction type {row.TypeCode}.",
            false);
    }

    private static AccountingClassification ClassifySavings(
        AccountingSourceTransaction row)
    {
        if (row.TypeCode.Equals("ACCT_XFER", StringComparison.OrdinalIgnoreCase))
        {
            bool to = string.Equals(row.TransferDirection, "TO", StringComparison.OrdinalIgnoreCase);
            bool from = string.Equals(row.TransferDirection, "FROM", StringComparison.OrdinalIgnoreCase);

            if (from && row.TransferLast4 == CheckingAccount)
                return Auto("savings:from-checking", $"Transfer from Checking *{CheckingAccount}", "Transfers In", true);
            if (to && row.TransferLast4 == CheckingAccount)
                return Auto("savings:to-checking", $"Transfer to Checking *{CheckingAccount}", "Transfers Out", true);
            if (to && (row.TransferLast4 == "0231" || row.TransferLast4 == "8153"))
                return Auto("savings:owners-draw", "Owners Draw", "Owners Draw", true);
        }

        return Review(
            $"savings:unknown:{row.TransactionId}",
            row.UnparsedDescription ?? row.TypeCode,
            "No savings-account accounting rule exists for this transaction yet.",
            false);
    }

    private static AccountingClassification ClassifyCreditCard(
        AccountingSourceTransaction row)
    {
        string merchant = (row.CreditCardMerchant ?? string.Empty).Trim();
        string upper = merchant.ToUpperInvariant();

        if (row.TypeCode.Equals("Payment", StringComparison.OrdinalIgnoreCase))
            return Auto("card:payment", "payment", "Credit Card Pmt", false);
        if (upper.Contains("PSYCHOLOGY TODAY"))
            return Auto("card:psychology-today", "Psychology Today advertisement", "Advertising", false);
        if (upper.Contains("SIMPLEPRACTICE"))
            return Auto("card:simplepractice", "SimplePractice - electronic record keeping, claim filing, Stripe payment site", "Software Expense", false);
        if (upper.Contains("MICROSOFT") && upper.Contains("365"))
            return Auto("card:microsoft365", "Microsoft 365", "Software Expense", false);
        if (upper.Contains("AMAZON"))
            return Auto("card:amazon", "Amazon", "Office Expense", false);
        if (upper.Contains("ILLINOIS COUNSELING ASSOC"))
            return Auto("card:illinois-counseling", "Illinois Counseling Association - workshop", "Continuing Ed", false);
        if (upper.Contains("E CARE BHI"))
            return Auto("card:e-care-bhi", "E Care BHI - workshop", "Continuing Ed", false);
        if (upper.Contains("IFS INSTITUTE"))
            return Auto("card:ifs", "IFS institute - general application fee for future workshops", "Continuing Ed", false);
        if (upper.Contains("ESET"))
            return Auto("card:eset", "ESET antivirus for computer", "Office Expense", false);
        if (upper.Contains("IAODAPCA"))
            return Auto("card:iaodapca", "IAODAPCA.ORG - CADC certification fee", "Professional Licenses Fee", false);
        if (upper.Contains("XFINITY"))
            return Auto("card:xfinity", merchant, "WiFi Fee", false);
        if (LooksLikeLlcFee(upper, string.Empty))
            return Auto($"card:llc:{row.TransactionId}", merchant, "LLC Fee", false);
        if (LooksLikeLicenseFee(upper, string.Empty))
            return Auto($"card:license:{row.TransactionId}", merchant, "Professional Licenses Fee", false);
        if (upper.Contains("ASSOCIATION") && (upper.Contains("DUES") || upper.Contains("MEMBER")))
            return Auto($"card:association:{row.TransactionId}", merchant, "Professional Association Fee", false);

        string chaseCategory = row.CreditCardCategory ?? string.Empty;
        if (chaseCategory.Equals("Education", StringComparison.OrdinalIgnoreCase))
            return Auto($"card:education:{upper}", merchant, "Continuing Ed", false);
        if (chaseCategory.Equals("Office & Shipping", StringComparison.OrdinalIgnoreCase) ||
            chaseCategory.Equals("Merchandise & Inventory", StringComparison.OrdinalIgnoreCase))
            return Auto($"card:office:{upper}", merchant, "Office Expense", false);

        return Review(
            $"card:unknown:{row.TransactionId}",
            merchant,
            $"No accounting rule exists yet for merchant '{merchant}' / Chase category '{row.CreditCardCategory}'.",
            false);
    }

    private static bool LooksLikeLlcFee(string first, string second) =>
        (first.Contains("LLC") || second.Contains("LLC")) &&
        (first.Contains("FILING") || first.Contains("REGISTR") || first.Contains("SECRETARY OF STATE") ||
         second.Contains("FILING") || second.Contains("REGISTR"));

    private static bool LooksLikeLicenseFee(string first, string second) =>
        first.Contains("IAODAPCA") || second.Contains("IAODAPCA") ||
        first.Contains("CERTIFICATION") || second.Contains("CERTIFICATION") ||
        first.Contains("LICENSE FEE") || second.Contains("LICENSE FEE");

    private static AccountingClassification Auto(
        string key,
        string description,
        string category,
        bool aggregate) =>
        new(key, description, category, null, aggregate);

    private static AccountingClassification Review(
        string key,
        string description,
        string reason,
        bool aggregate) =>
        new(key, description, null, reason, aggregate);

    private static string FriendlyCompanyName(string company)
    {
        if (string.IsNullOrWhiteSpace(company))
            return "Insurance";

        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            company.Trim().ToLowerInvariant());
    }
}

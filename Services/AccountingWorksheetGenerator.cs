namespace SoloPractice.Services;

// Compatibility facade retained for callers of the original generator.
// New code should use AccountingLedgerService and AccountingWorkbookService.
internal sealed record AccountingWorksheetResult(
    string WorkbookPath,
    int CheckingRows,
    int SavingsRows,
    int CreditCardRows,
    int ReviewRows,
    bool ReplacedExistingWorkbook);

internal static class AccountingWorksheetGenerator
{
    public static AccountingWorksheetResult GenerateOrUpdate(
        int year,
        bool openAfterSaving = true)
    {
        AccountingLedgerService.GenerateMissingEntries(year);
        AccountingWorkbookResult result = AccountingWorkbookService.Generate(
            year,
            openAfterSaving: openAfterSaving);

        return new AccountingWorksheetResult(
            result.WorkbookPath,
            result.CheckingRows,
            result.SavingsRows,
            result.CreditCardRows,
            result.ReviewRows,
            result.ReplacedExistingWorkbook);
    }
}

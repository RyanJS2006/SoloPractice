namespace SoloPractice.Utilities;

internal static class AppPaths
{
    private const string ApplicationFolderName = "SoloPractice";

    public static string DocumentsDirectory
    {
        get
        {
            string documents =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments);

            if (string.IsNullOrWhiteSpace(documents))
            {
                documents =
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile);
            }

            return documents;
        }
    }

    public static string ApplicationDirectory
    {
        get
        {
            string? overrideDirectory =
                Environment.GetEnvironmentVariable("SOLOPRACTICE_DATA_DIRECTORY");

            return string.IsNullOrWhiteSpace(overrideDirectory)
                ? Path.Combine(DocumentsDirectory, ApplicationFolderName)
                : Path.GetFullPath(overrideDirectory);
        }
    }

    public static string DatabasePath =>
        Path.Combine(
            ApplicationDirectory,
            "SoloPractice.db");

    public static string DefaultWorkbookPath =>
        GetWorkbookPath(DateTime.Today.Year);

    public static string BackupsDirectory =>
        Path.Combine(ApplicationDirectory, "Backups");

    public static string DatabaseBackupPath =>
        Path.Combine(BackupsDirectory, "SoloPractice_bak.db");

    public static string GetAccountingDirectory(int year)
    {
        ValidateYear(year);
        return Path.Combine(ApplicationDirectory, $"{year}_Accounting");
    }

    public static string GetWorkbookPath(int year) =>
        Path.Combine(GetAccountingDirectory(year), $"{year}_Accounting_Worksheet.xlsx");

    public static string GetReceiptsDirectory(int year) =>
        Path.Combine(GetAccountingDirectory(year), $"{year}_Receipts");

    public static string GetMonthlyReceiptsDirectory(int year, int month)
    {
        ValidateYear(year);
        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month));
        return Path.Combine(GetReceiptsDirectory(year), $"{year}-{month:00}_Receipts");
    }

    public static string GetAdditionalReceiptsDirectory(int year) =>
        Path.Combine(GetReceiptsDirectory(year), $"{year}_Additional_Receipts");

    public static string GetTaxFormsDirectory(int year) =>
        Path.Combine(GetAccountingDirectory(year), $"{year}_Tax_Forms");

    public static void EnsureApplicationDirectoriesExist()
    {
        Directory.CreateDirectory(ApplicationDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }

    public static void EnsureAccountingYearDirectoriesExist(int year)
    {
        ValidateYear(year);
        EnsureApplicationDirectoriesExist();
        Directory.CreateDirectory(GetAccountingDirectory(year));
        Directory.CreateDirectory(GetReceiptsDirectory(year));
        for (int month = 1; month <= 12; month++)
            Directory.CreateDirectory(GetMonthlyReceiptsDirectory(year, month));
        Directory.CreateDirectory(GetAdditionalReceiptsDirectory(year));
        Directory.CreateDirectory(GetTaxFormsDirectory(year));

        string legacyWorkbook = Path.Combine(
            ApplicationDirectory,
            $"{year} Accounting Worksheet.xlsx");
        string destination = GetWorkbookPath(year);
        if (File.Exists(legacyWorkbook) && File.Exists(destination))
        {
            throw new IOException(
                $"Both the legacy workbook and the new year-layout workbook exist. " +
                $"SoloPractice will not overwrite either file. Resolve the conflict manually:\n" +
                $"  {legacyWorkbook}\n  {destination}");
        }

        if (File.Exists(legacyWorkbook))
        {
            try
            {
                File.Move(legacyWorkbook, destination);
            }
            catch (IOException exception)
            {
                throw new IOException(
                    $"Could not move the legacy workbook into the {year} accounting folder. " +
                    "Save and close the workbook, then try again.",
                    exception);
            }
        }
    }

    public static void EnsureDirectoriesExist() =>
        EnsureApplicationDirectoriesExist();

    private static void ValidateYear(int year)
    {
        if (year is < 2000 or > 9998)
            throw new ArgumentOutOfRangeException(nameof(year));
    }
}

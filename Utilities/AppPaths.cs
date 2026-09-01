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

    public static string ApplicationDirectory =>
        Path.Combine(
            DocumentsDirectory,
            ApplicationFolderName);

    public static string DatabasePath =>
        Path.Combine(
            ApplicationDirectory,
            "accounting.db");

    public static string DefaultWorkbookPath =>
        Path.Combine(
            ApplicationDirectory,
            $"{DateTime.Today.Year} Accounting Worksheet.xlsx");

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(ApplicationDirectory);
    }
}
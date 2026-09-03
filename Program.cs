using SoloPractice.Data;
using SoloPractice.Services;
using SoloPractice.Utilities;
using System.Text;

namespace SoloPractice;

internal static class Program
{
    private const int ResizeDebounceMilliseconds = 100;
    private const int CsvPasteIdleMilliseconds = 100;

    private static void Main()
    {
        try
        {
            Database.Initialize();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("""
                Failed to initialize the accounting database.

                """);
            Console.Error.WriteLine(exception);
            return;
        }

        RunMainMenu();
    }

    private static void RunMainMenu()
    {
        while (true)
        {
            DrawMainMenu();

            MainMenuSelection selection =
                WaitForMainMenuSelection();

            switch (selection.Action)
            {
                case MainMenuAction.ImportChase:
                    ImportChaseDownload(selection.CsvPath);
                    break;

                case MainMenuAction.AccountingWorksheet:
                    GenerateAccountingWorksheet();
                    break;

                case MainMenuAction.ReceiptScans:
                    NotImplemented("Receipt scans");
                    break;

                case MainMenuAction.InsuranceAndTaxForms:
                    NotImplemented("Insurance and tax forms");
                    break;

                case MainMenuAction.About:
                    NotImplemented("About");
                    break;

                case MainMenuAction.Exit:
                    return;
            }
        }
    }

    private static void DrawMainMenu()
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("""
            

            SoloPractice is a C# program meant to turn CSV files downloaded from Chase's website into a flexible database of transactions. This database can then be used to automate and streamline accounting by automatically filling in most of the cells in a spreadsheet. There are also tools for organizing scans of receipts, insurance company statements, and tax forms.
            
            """);

        Console.ForegroundColor = ConsoleColor.Cyan; Console.Write("  1. "); Console.ForegroundColor = ConsoleColor.Gray; Console.WriteLine("Import Chase Bank Statement CSV");
        Console.ForegroundColor = ConsoleColor.Cyan; Console.Write("  2. "); Console.ForegroundColor = ConsoleColor.Gray; Console.WriteLine("Generate/Update/Open Accounting Spreadsheet");
        Console.ForegroundColor = ConsoleColor.Cyan; Console.Write("  3. "); Console.ForegroundColor = ConsoleColor.Gray; Console.WriteLine("Upload Receipt Scans");
        Console.ForegroundColor = ConsoleColor.Cyan; Console.Write("  4. "); Console.ForegroundColor = ConsoleColor.Gray; Console.WriteLine("Upload Insurance Company Statements and Tax Forms");
        Console.ForegroundColor = ConsoleColor.Cyan; Console.Write("  5. "); Console.ForegroundColor = ConsoleColor.Gray; Console.WriteLine("About"); Console.WriteLine("");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Press ");

        Console.ForegroundColor = ConsoleColor.Black; Console.BackgroundColor = ConsoleColor.Cyan;
        Console.Write("[Esc]"); Console.BackgroundColor = ConsoleColor.Black;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to Exit.");

        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("> ");

        Console.CursorVisible = true;
    }

    private static MainMenuSelection WaitForMainMenuSelection()
    {
        int drawnWidth = Console.WindowWidth;
        int drawnHeight = Console.WindowHeight;

        int latestWidth = drawnWidth;
        int latestHeight = drawnHeight;

        DateTime lastResizeTime = DateTime.MinValue;
        DateTime lastTextInputTime = DateTime.MinValue;

        var buffer = new StringBuilder();

        while (true)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key =
                    Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape)
                {
                    return new MainMenuSelection(
                        MainMenuAction.Exit);
                }

                if (buffer.Length == 0)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.D1:
                        case ConsoleKey.NumPad1:
                            return new MainMenuSelection(
                                MainMenuAction.ImportChase);

                        case ConsoleKey.D2:
                        case ConsoleKey.NumPad2:
                            return new MainMenuSelection(
                                MainMenuAction.AccountingWorksheet);

                        case ConsoleKey.D3:
                        case ConsoleKey.NumPad3:
                            return new MainMenuSelection(
                                MainMenuAction.ReceiptScans);

                        case ConsoleKey.D4:
                        case ConsoleKey.NumPad4:
                            return new MainMenuSelection(
                                MainMenuAction.InsuranceAndTaxForms);

                        case ConsoleKey.D5:
                        case ConsoleKey.NumPad5:
                            return new MainMenuSelection(
                                MainMenuAction.About);
                    }
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (TryGetExistingCsvPath(
                            buffer.ToString(),
                            out string? csvPath))
                    {
                        Console.WriteLine();

                        return new MainMenuSelection(
                            MainMenuAction.ImportChase,
                            csvPath);
                    }

                    buffer.Clear();
                    DrawMainMenu();

                    drawnWidth = Console.WindowWidth;
                    drawnHeight = Console.WindowHeight;
                    latestWidth = drawnWidth;
                    latestHeight = drawnHeight;

                    continue;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    RemoveLastConsoleCharacter(buffer);
                    lastTextInputTime = DateTime.UtcNow;
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                    lastTextInputTime = DateTime.UtcNow;
                }
            }

            if (buffer.Length > 0 &&
                (DateTime.UtcNow - lastTextInputTime)
                    .TotalMilliseconds >= CsvPasteIdleMilliseconds &&
                TryGetExistingCsvPath(
                    buffer.ToString(),
                    out string? droppedCsvPath))
            {
                Console.WriteLine();

                return new MainMenuSelection(
                    MainMenuAction.ImportChase,
                    droppedCsvPath);
            }

            int currentWidth = Console.WindowWidth;
            int currentHeight = Console.WindowHeight;

            if (currentWidth != latestWidth ||
                currentHeight != latestHeight)
            {
                latestWidth = currentWidth;
                latestHeight = currentHeight;
                lastResizeTime = DateTime.UtcNow;
            }

            bool sizeChanged =
                latestWidth != drawnWidth ||
                latestHeight != drawnHeight;

            bool resizeSettled =
                (DateTime.UtcNow - lastResizeTime)
                    .TotalMilliseconds >= ResizeDebounceMilliseconds;

            if (sizeChanged && resizeSettled)
            {
                DrawMainMenu();

                if (buffer.Length > 0)
                    Console.Write(buffer.ToString());

                drawnWidth = latestWidth;
                drawnHeight = latestHeight;
            }

            Thread.Sleep(10);
        }
    }

    private static void ImportChaseDownload(
        string? initialCsvPath = null)
    {
        DrawImportPage();

        string? queuedPath = initialCsvPath;

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("> ");

            string? input;

            if (!string.IsNullOrWhiteSpace(queuedPath))
            {
                input = queuedPath;
                queuedPath = null;

                Console.WriteLine(input);
            }
            else
            {
                input = ReadPathOrEscape();
            }

            if (input is null)
                return;

            if (string.IsNullOrWhiteSpace(input))
                continue;

            string path = NormalizePathInput(input);

            Console.WriteLine();

            ImportOneChaseFile(path);

            Console.WriteLine();
        }
    }

    private static void DrawImportPage()
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine("Import Chase Download");

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();
        Console.WriteLine(
            "Drag a Chase .csv here, or type/paste its path.");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Press ");

        Console.ForegroundColor = ConsoleColor.Black; Console.BackgroundColor = ConsoleColor.Cyan;
        Console.Write("[Esc]"); Console.BackgroundColor = ConsoleColor.Black;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to go back.");

        Console.WriteLine();

        Console.CursorVisible = true;
    }

    private static void ImportOneChaseFile(
        string path)
    {
        try
        {
            ChaseImportResult result =
                ChaseCsvImporter.Import(path);

            if (result.FileAlreadyImported)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    "This exact Chase download has already been imported.");

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(result.FileName);

                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Import successful.");

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine();

            Console.WriteLine(
                $"File:                  {result.FileName}");

            Console.WriteLine(
                $"Account:               {result.AccountLast4}");

            Console.WriteLine(
                $"Format:                {result.FormatName}");

            Console.WriteLine(
                $"Rows read:             {result.RowsRead:N0}");

            Console.WriteLine(
                $"New transactions:      {result.NewTransactions:N0}");

            Console.WriteLine(
                $"Existing transactions: {result.ReusedTransactions:N0}");

            Console.WriteLine(
                $"Unparsed descriptions: {result.UnparsedDescriptions:N0}");

            Console.ResetColor();
        }
        catch (Exception exception)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Import failed.");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(exception.Message);

#if DEBUG
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine();
            Console.WriteLine(exception);
#endif

            Console.ResetColor();
        }
    }

    private static string? ReadPathOrEscape()
    {
        var buffer = new StringBuilder();
        DateTime lastTextInputTime = DateTime.MinValue;

        while (true)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key =
                    Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape)
                {
                    Console.WriteLine();
                    return null;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return buffer.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    RemoveLastConsoleCharacter(buffer);
                    lastTextInputTime = DateTime.UtcNow;
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                    lastTextInputTime = DateTime.UtcNow;
                }
            }

            // A dragged/pasted file path commonly arrives as a burst of
            // characters. Once input has gone idle briefly and the complete
            // text resolves to an existing CSV, submit it without requiring
            // another Enter press.
            if (buffer.Length > 0 &&
                (DateTime.UtcNow - lastTextInputTime)
                    .TotalMilliseconds >= CsvPasteIdleMilliseconds &&
                TryGetExistingCsvPath(
                    buffer.ToString(),
                    out _))
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            Thread.Sleep(10);
        }
    }

    private static bool TryGetExistingCsvPath(
        string input,
        out string? csvPath)
    {
        csvPath = null;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        string normalized = NormalizePathInput(input);

        if (!string.Equals(
                Path.GetExtension(normalized),
                ".csv",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!File.Exists(normalized))
            return false;

        csvPath = normalized;
        return true;
    }

    private static string NormalizePathInput(
        string input)
    {
        string path = input.Trim();

        if (path.Length >= 2)
        {
            bool doubleQuoted =
                path[0] == '"' &&
                path[^1] == '"';

            bool singleQuoted =
                path[0] == '\'' &&
                path[^1] == '\'';

            if (doubleQuoted || singleQuoted)
                path = path[1..^1];
        }

        // macOS/Linux terminals commonly escape spaces and punctuation when
        // a file is dragged into a shell-like prompt. Windows paths keep
        // backslashes literally, so only unescape on Unix-like systems.
        if (!OperatingSystem.IsWindows())
            path = UnescapeUnixPath(path);

        return path;
    }

    private static string UnescapeUnixPath(
        string path)
    {
        var result = new StringBuilder(path.Length);

        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\' &&
                i + 1 < path.Length)
            {
                result.Append(path[++i]);
                continue;
            }

            result.Append(path[i]);
        }

        return result.ToString();
    }

    private static void RemoveLastConsoleCharacter(
        StringBuilder buffer)
    {
        if (buffer.Length == 0)
            return;

        buffer.Length--;

        int left = Console.CursorLeft;

        if (left > 0)
        {
            Console.Write("\b \b");
            return;
        }

        // If the input wrapped onto another console line, redrawing just the
        // current prompt is safer than trying to calculate terminal wrapping.
        Console.WriteLine();
        Console.Write("> ");
        Console.Write(buffer.ToString());
    }

    private static void GenerateAccountingWorksheet()
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine("Generate / Update Accounting Spreadsheet");
        Console.WriteLine();

        try
        {
            int year = DateTime.Today.Year;

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(
                $"Building the {year} workbook from SoloPractice.db...");
            Console.WriteLine();

            AccountingWorksheetResult result =
                AccountingWorksheetGenerator.GenerateOrUpdate(year);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(
                result.ReplacedExistingWorkbook
                    ? "Accounting workbook updated successfully."
                    : "Accounting workbook generated successfully.");

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine();
            Console.WriteLine(
                $"Checking rows:    {result.CheckingRows:N0}");
            Console.WriteLine(
                $"Savings rows:     {result.SavingsRows:N0}");
            Console.WriteLine(
                $"Credit-card rows: {result.CreditCardRows:N0}");
            Console.WriteLine(
                $"Rows to review:   {result.ReviewRows:N0}");
            Console.WriteLine();
            Console.WriteLine(result.WorkbookPath);

            if (result.ReviewRows > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine();
                Console.WriteLine(
                    "Rows highlighted in yellow still need an accounting decision.");
            }
        }
        catch (Exception exception)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(
                "Could not generate the accounting workbook.");
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(exception.Message);

#if DEBUG
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine();
            Console.WriteLine(exception);
#endif
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.Write("Press ");

        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.Cyan;
        Console.Write("[Esc]");
        Console.BackgroundColor = ConsoleColor.Black;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to go back.");

        while (Console.ReadKey(intercept: true).Key !=
               ConsoleKey.Escape)
        {
        }

        Console.ResetColor();
    }

    private static void NotImplemented(
        string feature)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();
        Console.WriteLine($"{feature} is not implemented yet.");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Press ");

        Console.ForegroundColor = ConsoleColor.Black; Console.BackgroundColor = ConsoleColor.Cyan;
        Console.Write("[Esc]"); Console.BackgroundColor = ConsoleColor.Black;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to go back.");

        while (Console.ReadKey(intercept: true).Key !=
               ConsoleKey.Escape)
        {
        }
    }

    private static void WriteCenteredRainbowBlock(
        string[] lines,
        ConsoleColor[] colors,
        int windowWidth)
    {
        int blockWidth = lines.Max(x => x.Length);
        int left = Math.Max(
            0,
            (windowWidth - blockWidth) / 2);

        for (int i = 0; i < lines.Length; i++)
        {
            Console.Write(new string(' ', left));

            Console.ForegroundColor =
                i < colors.Length
                    ? colors[i]
                    : ConsoleColor.White;

            Console.WriteLine(lines[i]);
        }
    }

    private static void DrawHeader()
    {
        string[] solo =
        {
            "███████╗ ██████╗ ██╗      ██████╗ ",
            "██╔════╝██╔═══██╗██║     ██╔═══██╗",
            "███████╗██║   ██║██║     ██║   ██║",
            "╚════██║██║   ██║██║     ██║   ██║",
            "███████║╚██████╔╝███████╗╚██████╔╝",
            "╚══════╝ ╚═════╝ ╚══════╝ ╚═════╝ "
        };

        string[] practice =
        {
            "██████╗ ██████╗  █████╗  ██████╗████████╗██╗ ██████╗███████╗",
            "██╔══██╗██╔══██╗██╔══██╗██╔════╝╚══██╔══╝██║██╔════╝██╔════╝",
            "██████╔╝██████╔╝███████║██║        ██║   ██║██║     █████╗  ",
            "██╔═══╝ ██╔══██╗██╔══██║██║        ██║   ██║██║     ██╔══╝  ",
            "██║     ██║  ██║██║  ██║╚██████╗   ██║   ██║╚██████╗███████╗",
            "╚═╝     ╚═╝  ╚═╝╚═╝  ╚═╝ ╚═════╝   ╚═╝   ╚═╝ ╚═════╝╚══════╝"
        };

        ConsoleColor[] soloRainbow =
        {
            ConsoleColor.Red,
            ConsoleColor.DarkYellow,
            ConsoleColor.Green,
            ConsoleColor.Cyan,
            ConsoleColor.Blue,
            ConsoleColor.Magenta
        };

        const int gap = 1;

        int windowWidth = Console.WindowWidth;
        int soloWidth = solo.Max(
            line => line.Length);

        int practiceWidth = practice.Max(
            line => line.Length);

        int combinedWidth =
            soloWidth +
            gap +
            practiceWidth;

        bool useWideLayout =
            combinedWidth + 4 <= windowWidth;

        Console.WriteLine();

        if (useWideLayout)
        {
            int left = Math.Max(
                0,
                (windowWidth - combinedWidth) / 2);

            for (int i = 0; i < solo.Length; i++)
            {
                Console.Write(
                    new string(' ', left));

                Console.ForegroundColor =
                    soloRainbow[i];

                Console.Write(
                    solo[i].PadRight(soloWidth));

                Console.Write(
                    new string(' ', gap));

                Console.ForegroundColor =
                    ConsoleColor.White;

                Console.WriteLine(
                    practice[i]);
            }
        }
        else
        {
            WriteCenteredRainbowBlock(
                solo,
                soloRainbow,
                windowWidth);

            Console.WriteLine();

            WriteCenteredBlock(
                practice,
                ConsoleColor.White,
                windowWidth);
        }

        Console.WriteLine();
        DrawFullWidthDivider();
        Console.ResetColor();
    }

    private static void DrawFullWidthDivider()
    {
        Console.ForegroundColor =
            ConsoleColor.Gray;

        int width = Console.WindowWidth;

        if (width <= 0)
            return;

        Console.Write(
            new string('─', width));
    }

    private static void ClearForRedraw()
    {
        Console.Write(
            "\x1b[2J\x1b[3J\x1b[H");
    }

    private static void WriteCenteredBlock(
        string[] lines,
        ConsoleColor color,
        int windowWidth)
    {
        int blockWidth =
            lines.Max(line => line.Length);

        int left = Math.Max(
            0,
            (windowWidth - blockWidth) / 2);

        Console.ForegroundColor = color;

        foreach (string line in lines)
        {
            Console.Write(
                new string(' ', left));

            Console.WriteLine(line);
        }
    }

    private enum MainMenuAction
    {
        ImportChase,
        AccountingWorksheet,
        ReceiptScans,
        InsuranceAndTaxForms,
        About,
        Exit
    }

    private readonly record struct MainMenuSelection(
        MainMenuAction Action,
        string? CsvPath = null);
}
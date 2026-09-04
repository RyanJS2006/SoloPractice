using SoloPractice.Data;
using SoloPractice.Services;
using SoloPractice.Utilities;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SoloPractice;

internal static class Program
{
    private const int ResizeDebounceMilliseconds = 100;
    private const int CsvPasteIdleMilliseconds = 100;
    private const int FirstAccountingYear = 2024;

    private static readonly (MainMenuAction Action, string Label)[] MainMenuOptions =
    [
        (MainMenuAction.ImportChase, "Import Chase Bank Statement CSV"),
        (MainMenuAction.AccountingWorksheet, "Generate/Update/Open Accounting Spreadsheet"),
        (MainMenuAction.DocumentArchive, "Upload / Archive Documents"),
        (MainMenuAction.About, "About")
    ];

    private static void Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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

        if (args.Length > 0)
        {
            RunCommand(args);
            return;
        }

        RunMainMenu();
    }

    private static void RunCommand(string[] args)
    {
        try
        {
            if (args[0].Equals("--accounting-update", StringComparison.OrdinalIgnoreCase))
            {
                int year = args.Length >= 2
                    ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)
                    : DateTime.Today.Year;
                AppPaths.EnsureAccountingYearDirectoriesExist(year);
                string path = args.Length >= 3
                    ? Path.GetFullPath(args[2])
                    : AccountingWorkbookService.GetWorkbookPath(year);

                DatabaseBackupService.CreateVerifiedBackup();

                AccountingWorkbookSyncResult? imported = null;
                bool legacy = File.Exists(path) &&
                    !AccountingWorkbookService.IsDatabaseBackedWorkbook(year, path);
                if (File.Exists(path) && !legacy)
                    imported = AccountingWorkbookService.ImportWorkbookEdits(year, path);
                AccountingGenerationResult generated = AccountingLedgerService.GenerateMissingEntries(year);
                if (legacy)
                    imported = AccountingWorkbookService.ImportLegacyWorkbookEdits(year, path);
                AccountingWorkbookResult workbook = AccountingWorkbookService.Generate(year, path);

                Console.WriteLine(
                    $"Workbook: {workbook.WorkbookPath}\n" +
                    $"Entries created: {generated.EntriesCreated}\n" +
                    $"Source transactions linked: {generated.SourceTransactionsLinked}\n" +
                    $"Rows imported: {(imported?.RowsInserted ?? 0)}\n" +
                    $"Rows updated: {(imported?.RowsUpdated ?? 0)}\n" +
                    $"Categories added: {(imported?.CategoriesAdded ?? 0)}\n" +
                    $"Rows to review: {workbook.ReviewRows}");
                return;
            }

            if (args[0].Equals("--accounting-sync", StringComparison.OrdinalIgnoreCase) && args.Length == 3)
            {
                int year = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
                AppPaths.EnsureAccountingYearDirectoriesExist(year);
                string path = Path.GetFullPath(args[2]);
                DatabaseBackupService.CreateVerifiedBackup();
                AccountingWorkbookSyncResult sync = AccountingWorkbookService.ImportWorkbookEdits(year, path);
                AccountingWorkbookService.Generate(year, path);
                Console.WriteLine(
                    $"Rows inserted: {sync.RowsInserted}\n" +
                    $"Rows updated: {sync.RowsUpdated}\n" +
                    $"Categories added: {sync.CategoriesAdded}\n" +
                    $"Unresolved rows: {sync.UnresolvedRows}");
                return;
            }

            Console.Error.WriteLine(
                "Usage:\n" +
                "  SoloPractice --accounting-update [year] [workbook-path]\n" +
                "  SoloPractice --accounting-sync <year> <workbook-path>");
            Environment.ExitCode = 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
        }
    }

    private static void RunMainMenu()
    {
        while (true)
        {
            MainMenuSelection selection =
                WaitForMainMenuSelection();

            switch (selection.Action)
            {
                case MainMenuAction.ImportChase:
                    ImportChaseDownload(selection.CsvPath);
                    break;

                case MainMenuAction.AccountingWorksheet:
                    RunAccountingWorksheetMenu();
                    break;

                case MainMenuAction.DocumentArchive:
                    RunDocumentArchive();
                    break;

                case MainMenuAction.About:
                    RunAbout();
                    break;

                case MainMenuAction.Exit:
                    return;
            }
        }
    }

    private static MainMenuSelection WaitForMainMenuSelection()
    {
        int selectedIndex = 0;
        var buffer = new StringBuilder();

        MainMenuLayout layout =
            DrawMainMenu(selectedIndex, buffer.ToString());

        int drawnWidth = layout.WindowWidth;
        int drawnHeight = layout.WindowHeight;
        int latestWidth = drawnWidth;
        int latestHeight = drawnHeight;
        DateTime lastResizeTime = DateTime.MinValue;
        DateTime lastTextInputTime = DateTime.MinValue;

        while (true)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key =
                    Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape)
                {
                    Console.CursorVisible = false;
                    return new MainMenuSelection(
                        MainMenuAction.Exit);
                }

                if (buffer.Length == 0 &&
                    TryGetNavigationDelta(key.Key, out int delta))
                {
                    selectedIndex = WrapSelection(
                        selectedIndex + delta,
                        MainMenuOptions.Length);

                    RedrawMenuOptions(
                        layout.OptionsTop,
                        MainMenuOptions.Select(option => option.Label).ToArray(),
                        selectedIndex);

                    RestoreInputCursor(
                        layout.PromptTop,
                        "Chase CSV > ",
                        buffer.ToString());
                    continue;
                }

                if (buffer.Length == 0 &&
                    TryGetNumericSelection(
                        key.Key,
                        MainMenuOptions.Length,
                        out int numericSelection))
                {
                    selectedIndex = numericSelection;

                    RedrawMenuOptions(
                        layout.OptionsTop,
                        MainMenuOptions.Select(option => option.Label).ToArray(),
                        selectedIndex);

                    RestoreInputCursor(
                        layout.PromptTop,
                        "Chase CSV > ",
                        buffer.ToString());
                    continue;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (buffer.Length > 0)
                    {
                        Console.CursorVisible = false;
                        return new MainMenuSelection(
                            MainMenuAction.ImportChase,
                            NormalizePathInput(buffer.ToString()));
                    }

                    Console.CursorVisible = false;
                    return new MainMenuSelection(
                        MainMenuOptions[selectedIndex].Action);
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        lastTextInputTime = DateTime.UtcNow;
                        layout = DrawMainMenu(
                            selectedIndex,
                            buffer.ToString());
                        drawnWidth = layout.WindowWidth;
                        drawnHeight = layout.WindowHeight;
                        latestWidth = drawnWidth;
                        latestHeight = drawnHeight;
                    }
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                    lastTextInputTime = DateTime.UtcNow;
                }
            }

            // Dragging a CSV into Windows Terminal arrives as a burst of
            // printable characters. As soon as the complete burst resolves to
            // an existing CSV, open the import page without requiring Enter.
            if (buffer.Length > 0 &&
                (DateTime.UtcNow - lastTextInputTime)
                    .TotalMilliseconds >= CsvPasteIdleMilliseconds &&
                TryGetExistingCsvPath(
                    buffer.ToString(),
                    out string? droppedCsvPath))
            {
                Console.CursorVisible = false;
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
                layout = DrawMainMenu(
                    selectedIndex,
                    buffer.ToString());
                drawnWidth = layout.WindowWidth;
                drawnHeight = layout.WindowHeight;
                latestWidth = drawnWidth;
                latestHeight = drawnHeight;
            }

            Thread.Sleep(10);
        }
    }

    private static MainMenuLayout DrawMainMenu(
        int selectedIndex,
        string inputBuffer)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("""
            

            SoloPractice keeps the practice's accounting files organized in one place. Import Chase downloads, create and sync yearly accounting spreadsheets, and archive scanned receipts, tax forms, and insurance statements.
            
            """);

        int optionsTop = Console.CursorTop;
        string[] labels = MainMenuOptions
            .Select(option => option.Label)
            .ToArray();

        for (int i = 0; i < labels.Length; i++)
        {
            DrawMenuOptionLine(
                labels[i],
                i == selectedIndex,
                Console.WindowWidth);
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Use ");
        DrawKeyHint("↑/↓/←/→");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" or ");
        DrawKeyHint($"1-{MainMenuOptions.Length}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" to Navigate, ");
        DrawKeyHint("Enter");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" to Select, or ");
        DrawKeyHint("Esc");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to Exit.");

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            "You can also paste or drag a Chase CSV file into this window to import it.");

        Console.ForegroundColor = ConsoleColor.White;
        int promptTop = Console.CursorTop;
        Console.Write("Chase CSV > ");
        Console.Write(inputBuffer);
        Console.ResetColor();
        Console.CursorVisible = true;

        return new MainMenuLayout(
            optionsTop,
            promptTop,
            Console.WindowWidth,
            Console.WindowHeight);
    }

    private static void RedrawMenuOptions(
        int optionsTop,
        IReadOnlyList<string> labels,
        int selectedIndex)
    {
        Console.CursorVisible = false;

        try
        {
            for (int i = 0; i < labels.Count; i++)
            {
                Console.SetCursorPosition(0, optionsTop + i);
                DrawMenuOptionLine(
                    labels[i],
                    i == selectedIndex,
                    Console.WindowWidth);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // A resize can briefly invalidate the old row coordinates. The
            // resize loop will do a full redraw as soon as the new size settles.
        }
        catch (IOException)
        {
        }
    }

    private static void DrawMenuOptionLine(
        string label,
        bool selected,
        int windowWidth)
    {
        Console.ResetColor();

        Console.ForegroundColor = selected
            ? ConsoleColor.Magenta
            : ConsoleColor.DarkGray;
        Console.Write(selected ? " > " : "   ");

        if (selected)
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.Magenta;
            Console.Write(label);
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(label);
        }

        int used = 3 + label.Length;
        int padding = Math.Max(0, windowWidth - used - 1);
        if (padding > 0)
            Console.Write(new string(' ', padding));

        Console.ResetColor();
    }

    private static void RestoreInputCursor(
        int promptTop,
        string prefix,
        string buffer)
    {
        try
        {
            int left = Math.Min(
                Math.Max(0, prefix.Length + buffer.Length),
                Math.Max(0, Console.WindowWidth - 1));
            int top = Math.Min(
                Math.Max(0, promptTop),
                Math.Max(0, Console.BufferHeight - 1));

            Console.SetCursorPosition(left, top);
            Console.CursorVisible = true;
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static bool TryGetNavigationDelta(
        ConsoleKey key,
        out int delta)
    {
        switch (key)
        {
            case ConsoleKey.UpArrow:
            case ConsoleKey.LeftArrow:
                delta = -1;
                return true;

            case ConsoleKey.DownArrow:
            case ConsoleKey.RightArrow:
                delta = 1;
                return true;

            default:
                delta = 0;
                return false;
        }
    }

    private static bool TryGetNumericSelection(
        ConsoleKey key,
        int optionCount,
        out int selectedIndex)
    {
        int number = key switch
        {
            ConsoleKey.D1 or ConsoleKey.NumPad1 => 1,
            ConsoleKey.D2 or ConsoleKey.NumPad2 => 2,
            ConsoleKey.D3 or ConsoleKey.NumPad3 => 3,
            ConsoleKey.D4 or ConsoleKey.NumPad4 => 4,
            ConsoleKey.D5 or ConsoleKey.NumPad5 => 5,
            ConsoleKey.D6 or ConsoleKey.NumPad6 => 6,
            ConsoleKey.D7 or ConsoleKey.NumPad7 => 7,
            ConsoleKey.D8 or ConsoleKey.NumPad8 => 8,
            ConsoleKey.D9 or ConsoleKey.NumPad9 => 9,
            _ => 0
        };

        selectedIndex = number - 1;
        return number >= 1 && number <= optionCount;
    }

    private static int WrapSelection(
        int value,
        int count) =>
        ((value % count) + count) % count;

    private static void DrawKeyHint(string text)
    {
        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.Cyan;
        Console.Write($"[{text}]");
        Console.ResetColor();
    }

    private static void ImportChaseDownload(
        string? initialCsvPath = null)
    {
        var buffer = new StringBuilder();

        DrawImportPage();

        if (!string.IsNullOrWhiteSpace(initialCsvPath))
        {
            string path = NormalizePathInput(initialCsvPath);
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("> ");
            Console.WriteLine(path);
            Console.WriteLine();
            DrawImportPageEntry(ImportOneChaseFile(path));
            Console.WriteLine();
        }

        DrawChaseImportPrompt();
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
                    Console.CursorVisible = false;
                    Console.ResetColor();
                    return;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (buffer.Length == 0)
                        continue;

                    string path = NormalizePathInput(buffer.ToString());
                    Console.WriteLine();
                    Console.WriteLine();
                    DrawImportPageEntry(ImportOneChaseFile(path));
                    buffer.Clear();
                    Console.WriteLine();
                    DrawChaseImportPrompt();
                    lastTextInputTime = DateTime.MinValue;
                    continue;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        lastTextInputTime = DateTime.UtcNow;
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                    lastTextInputTime = DateTime.UtcNow;
                }
            }

            // Same idle-submit behavior as the main menu: a dropped CSV is
            // imported as soon as the full path has arrived.
            if (buffer.Length > 0 &&
                (DateTime.UtcNow - lastTextInputTime)
                    .TotalMilliseconds >= CsvPasteIdleMilliseconds &&
                TryGetExistingCsvPath(
                    buffer.ToString(),
                    out string? droppedCsvPath))
            {
                Console.WriteLine();
                Console.WriteLine();
                DrawImportPageEntry(ImportOneChaseFile(droppedCsvPath!));
                buffer.Clear();
                Console.WriteLine();
                DrawChaseImportPrompt();
                lastTextInputTime = DateTime.MinValue;
                continue;
            }

            Thread.Sleep(10);
        }
    }

    private static void DrawImportPage()
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();

        // DrawHeader intentionally leaves the cursor at the end of the divider.
        // The first WriteLine finishes that row; the second creates exactly one
        // empty row between the divider and this page's title.
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Import Chase Download");
        Console.WriteLine();
    }

    private static void DrawChaseImportPrompt()
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        if (Console.WindowWidth >= 72)
            Console.Write("Drag a Chase .csv here, or type/paste its path.  ");
        WriteKeyBadge("[Enter]");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.Write(" Import    ");
        WriteKeyBadge("[Esc]");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(" Back");

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("> ");
        Console.ResetColor();
        Console.CursorVisible = true;
    }

    private static ImportPageEntry ImportOneChaseFile(
        string path)
    {
        try
        {
            DatabaseBackupService.CreateVerifiedBackup();

            ChaseImportResult result =
                ChaseCsvImporter.Import(path);

            if (result.FileAlreadyImported)
            {
                return new ImportPageEntry(
                    "This exact Chase download has already been imported.",
                    ConsoleColor.Yellow,
                    [result.FileName]);
            }

            return new ImportPageEntry(
                "Import successful.",
                ConsoleColor.Green,
                [
                    $"File:                  {result.FileName}",
                    $"Account:               {result.AccountLast4}",
                    $"Format:                {result.FormatName}",
                    $"Rows read:             {result.RowsRead:N0}",
                    $"New transactions:      {result.NewTransactions:N0}",
                    $"Existing transactions: {result.ReusedTransactions:N0}",
                    $"Unparsed descriptions: {result.UnparsedDescriptions:N0}"
                ]);
        }
        catch (Exception exception)
        {
            var detailLines = new List<string>
            {
                exception.Message
            };

#if DEBUG
            detailLines.Add(string.Empty);
            detailLines.Add(exception.ToString());
#endif

            return new ImportPageEntry(
                "Import failed.",
                ConsoleColor.Red,
                detailLines);
        }
    }

    private static void DrawImportPageEntry(
        ImportPageEntry entry)
    {
        Console.ForegroundColor = entry.StatusColor;
        Console.WriteLine(entry.Status);

        if (entry.DetailLines.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine();
            foreach (string line in entry.DetailLines)
                Console.WriteLine(line);
        }

        Console.ResetColor();
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

    private static void RunAccountingWorksheetMenu()
    {
        AccountingYearOption[] options =
            GetAccountingYearOptions();

        int? selectedIndex =
            WaitForAccountingYearSelection(options);

        if (selectedIndex is null)
            return;

        AccountingYearOption option =
            options[selectedIndex.Value];

        RunAccountingWorkbookSession(option);
    }

    private static AccountingYearOption[] GetAccountingYearOptions()
    {
        int currentYear = DateTime.Today.Year;
        var options = new List<AccountingYearOption>();

        for (int year = currentYear;
             year >= FirstAccountingYear;
             year--)
        {
            AppPaths.EnsureAccountingYearDirectoriesExist(year);
            string workbookPath =
                AccountingWorkbookService.GetWorkbookPath(year);

            AccountingWorkbookAction action;

            if (!File.Exists(workbookPath))
            {
                action = AccountingWorkbookAction.Generate;
            }
            else
            {
                bool databaseBacked;
                try
                {
                    databaseBacked =
                        AccountingWorkbookService.IsDatabaseBackedWorkbook(
                            year,
                            workbookPath);
                }
                catch (IOException)
                {
                    // If the workbook cannot currently be inspected (for
                    // example because another application has it locked), err
                    // toward Update rather than incorrectly claiming it is current.
                    databaseBacked = false;
                }

                bool databaseIsNewer = databaseBacked &&
                    (
                        AccountingLedgerService.HasUnlinkedSourceTransactions(year) ||
                        AccountingLedgerService.HasAccountingChangesAfter(
                            year,
                            new DateTimeOffset(
                                File.GetLastWriteTimeUtc(workbookPath)))
                    );

                action =
                    !databaseBacked || databaseIsNewer
                        ? AccountingWorkbookAction.Update
                        : AccountingWorkbookAction.Open;
            }

            string label = action switch
            {
                AccountingWorkbookAction.Generate =>
                    $"Generate {year} Spreadsheet",
                AccountingWorkbookAction.Update =>
                    $"Update {year} Spreadsheet",
                AccountingWorkbookAction.Open =>
                    $"Open {year} Spreadsheet",
                _ => throw new InvalidOperationException()
            };

            options.Add(
                new AccountingYearOption(
                    year,
                    action,
                    workbookPath,
                    label));
        }

        return options.ToArray();
    }

    private static int? WaitForAccountingYearSelection(
        IReadOnlyList<AccountingYearOption> options)
    {
        int selectedIndex = 0;
        AccountingMenuLayout layout =
            DrawAccountingWorksheetMenu(options, selectedIndex);

        int drawnWidth = layout.WindowWidth;
        int drawnHeight = layout.WindowHeight;
        int latestWidth = drawnWidth;
        int latestHeight = drawnHeight;
        DateTime lastResizeTime = DateTime.MinValue;

        while (true)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key =
                    Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape)
                    return null;

                if (TryGetNavigationDelta(key.Key, out int delta))
                {
                    selectedIndex = WrapSelection(
                        selectedIndex + delta,
                        options.Count);

                    RedrawMenuOptions(
                        layout.OptionsTop,
                        options.Select(option => option.Label).ToArray(),
                        selectedIndex);
                    continue;
                }

                if (TryGetNumericSelection(
                        key.Key,
                        options.Count,
                        out int numericSelection))
                {
                    selectedIndex = numericSelection;

                    RedrawMenuOptions(
                        layout.OptionsTop,
                        options.Select(option => option.Label).ToArray(),
                        selectedIndex);
                    continue;
                }

                if (key.Key == ConsoleKey.Enter)
                    return selectedIndex;
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
                layout = DrawAccountingWorksheetMenu(
                    options,
                    selectedIndex);
                drawnWidth = layout.WindowWidth;
                drawnHeight = layout.WindowHeight;
                latestWidth = drawnWidth;
                latestHeight = drawnHeight;
            }

            Thread.Sleep(10);
        }
    }

    private static AccountingMenuLayout DrawAccountingWorksheetMenu(
        IReadOnlyList<AccountingYearOption> options,
        int selectedIndex)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Accounting Spreadsheet");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(
            "Choose an accounting year. The action shown reflects the current spreadsheet/database state.");
        Console.WriteLine();

        int optionsTop = Console.CursorTop;
        for (int i = 0; i < options.Count; i++)
        {
            DrawMenuOptionLine(
                options[i].Label,
                i == selectedIndex,
                Console.WindowWidth);
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Use ");
        DrawKeyHint("↑/↓/←/→");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" or ");
        DrawKeyHint($"1-{options.Count}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" to Navigate, ");
        DrawKeyHint("Enter");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" to Select, or ");
        DrawKeyHint("Esc");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to go back.");
        Console.ResetColor();

        return new AccountingMenuLayout(
            optionsTop,
            Console.WindowWidth,
            Console.WindowHeight);
    }

    private static void RunAccountingWorkbookSession(
        AccountingYearOption option)
    {
        int year = option.Year;
        DrawAccountingProcessingPage(year, option.Action);

        try
        {
            AppPaths.EnsureAccountingYearDirectoriesExist(year);
            string workbookPath = option.WorkbookPath;

            DatabaseBackupResult? backup =
                DatabaseBackupService.CreateVerifiedBackup();

            AccountingWorkbookSyncResult? preSync = null;
            AccountingGenerationResult generated = new(0, 0, 0);
            AccountingWorkbookResult result;

            if (option.Action == AccountingWorkbookAction.Open)
            {
                if (!File.Exists(workbookPath))
                    throw new FileNotFoundException("The accounting workbook no longer exists.", workbookPath);

                result = AccountingWorkbookService.ReadCurrentState(year, workbookPath);
            }
            else
            {
                if (option.Action == AccountingWorkbookAction.Update && File.Exists(workbookPath))
                {
                    bool legacyWorkbook =
                        !AccountingWorkbookService.IsDatabaseBackedWorkbook(
                            year,
                            workbookPath);
                    preSync = legacyWorkbook
                        ? AccountingWorkbookService.ImportLegacyWorkbookEdits(year, workbookPath)
                        : AccountingWorkbookService.ImportWorkbookEdits(year, workbookPath);
                }

                generated = AccountingLedgerService.GenerateMissingEntries(year);
                result = AccountingWorkbookService.Generate(year, workbookPath);
            }

            AccountingWorkbookService.OpenWorkbook(
                workbookPath);

            ConsoleKey next = WaitForEnterOrEscapeWithResize(
                () => DrawAccountingWorkbookReadyPage(
                    year,
                    option.Action,
                    result,
                    generated,
                    preSync,
                    backup));

            if (next == ConsoleKey.Escape)
                return;

            DrawAccountingSyncingPage(year);

            AccountingWorkbookSyncResult sync =
                AccountingWorkbookService.ImportWorkbookEdits(
                    year,
                    result.WorkbookPath);

            AccountingWorkbookService.Generate(
                year,
                result.WorkbookPath);

            WaitForEscapeWithResize(
                () => DrawAccountingSyncResultPage(
                    year,
                    sync));
        }
        catch (Exception exception)
        {
            WaitForEscapeWithResize(
                () => DrawAccountingErrorPage(
                    year,
                    exception));
        }
    }

    private static void DrawAccountingProcessingPage(
        int year,
        AccountingWorkbookAction action)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(action switch
        {
            AccountingWorkbookAction.Generate => $"Generate {year} Accounting Spreadsheet",
            AccountingWorkbookAction.Update => $"Update {year} Accounting Spreadsheet",
            AccountingWorkbookAction.Open => $"Open {year} Accounting Spreadsheet",
            _ => throw new InvalidOperationException()
        });
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(action == AccountingWorkbookAction.Open
            ? $"Opening the existing {year} accounting workbook..."
            : $"Updating the {year} accounting ledger from SoloPractice.db...");
        Console.ResetColor();
    }

    private static void DrawAccountingWorkbookReadyPage(
        int year,
        AccountingWorkbookAction action,
        AccountingWorkbookResult result,
        AccountingGenerationResult generated,
        AccountingWorkbookSyncResult? preSync,
        DatabaseBackupResult? backup)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(action switch
        {
            AccountingWorkbookAction.Generate =>
                $"{year} accounting workbook generated successfully.",
            AccountingWorkbookAction.Update =>
                $"{year} accounting workbook updated successfully.",
            AccountingWorkbookAction.Open =>
                $"{year} accounting workbook opened successfully.",
            _ => throw new InvalidOperationException()
        });

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();

        if (backup is not null)
        {
            Console.WriteLine(
                $"Verified database backup: {backup.BackupPath}");
            Console.WriteLine();
        }

        Console.WriteLine(
            $"Checking rows:    {result.CheckingRows:N0}");
        Console.WriteLine(
            $"Savings rows:     {result.SavingsRows:N0}");
        Console.WriteLine(
            $"Credit-card rows: {result.CreditCardRows:N0}");
        Console.WriteLine(
            $"Rows to review:   {result.ReviewRows:N0}");
        Console.WriteLine(
            $"New ledger rows:  {generated.EntriesCreated:N0}");

        if (preSync is not null)
        {
            Console.WriteLine(
                $"Saved edits imported: {preSync.RowsUpdated:N0} updated, " +
                $"{preSync.RowsInserted:N0} inserted");
        }

        Console.WriteLine();
        Console.WriteLine(result.WorkbookPath);

        if (result.ReviewRows > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine(
                "Rows highlighted in yellow still need an accounting decision.");
        }

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();
        Console.WriteLine(
            "Edit the workbook in Excel and save it.");
        Console.WriteLine();
        Console.Write("Press ");
        WriteKeyBadge("[Enter]");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.Write(" to sync saved spreadsheet changes back into SoloPractice.  Press ");
        WriteKeyBadge("[Esc]");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(
            " to return without syncing now.");
        Console.ResetColor();
    }

    private static void DrawAccountingSyncingPage(
        int year)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(
            $"Sync {year} Accounting Spreadsheet");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(
            "Reading the saved workbook...");
        Console.ResetColor();
    }

    private static void DrawAccountingSyncResultPage(
        int year,
        AccountingWorkbookSyncResult sync)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(
            $"{year} spreadsheet changes synced successfully.");

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();
        Console.WriteLine(
            $"Rows inserted:    {sync.RowsInserted:N0}");
        Console.WriteLine(
            $"Rows updated:     {sync.RowsUpdated:N0}");
        Console.WriteLine(
            $"Categories added: {sync.CategoriesAdded:N0}");
        Console.WriteLine(
            $"Unresolved rows:  {sync.UnresolvedRows:N0}");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.Write("Press ");
        WriteKeyBadge("[Esc]");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to go back.");
        Console.ResetColor();
    }

    private static void DrawAccountingErrorPage(
        int year,
        Exception exception)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(
            $"Could not generate or sync the {year} accounting workbook.");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(exception.Message);

#if DEBUG
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.WriteLine(exception);
#endif

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.Write("Press ");
        WriteKeyBadge("[Esc]");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to go back.");
        Console.ResetColor();
    }

    private static ConsoleKey WaitForEnterOrEscapeWithResize(
        Action drawPage)
    {
        drawPage();

        int drawnWidth = Console.WindowWidth;
        int drawnHeight = Console.WindowHeight;
        int latestWidth = drawnWidth;
        int latestHeight = drawnHeight;
        DateTime lastResizeTime = DateTime.MinValue;

        while (true)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKey key =
                    Console.ReadKey(intercept: true).Key;

                if (key is ConsoleKey.Enter or ConsoleKey.Escape)
                    return key;
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
                drawPage();
                drawnWidth = Console.WindowWidth;
                drawnHeight = Console.WindowHeight;
                latestWidth = drawnWidth;
                latestHeight = drawnHeight;
            }

            Thread.Sleep(10);
        }
    }

    private static void WaitForEscapeWithResize(
        Action drawPage)
    {
        while (WaitForEnterOrEscapeWithResize(drawPage) !=
               ConsoleKey.Escape)
        {
        }
    }

    private static void WriteKeyBadge(string text)
    {
        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.Cyan;
        Console.Write(text);
        Console.BackgroundColor = ConsoleColor.Black;
    }

    private static void RunDocumentArchive()
    {
        DrawDocumentArchivePage();
        var buffer = new StringBuilder();
        bool backupCreated = false;
        DateTime lastTextInputTime = DateTime.MinValue;
        DrawDocumentArchivePrompt();

        while (true)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape)
                {
                    Console.WriteLine();
                    Console.CursorVisible = false;
                    Console.ResetColor();
                    return;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (buffer.Length == 0)
                        continue;

                    Console.WriteLine();
                    ProcessDocumentInput(buffer.ToString(), ref backupCreated);
                    buffer.Clear();
                    Console.WriteLine();
                    DrawDocumentArchivePrompt();
                    lastTextInputTime = DateTime.MinValue;
                    continue;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");
                        lastTextInputTime = DateTime.UtcNow;
                    }
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
                (DateTime.UtcNow - lastTextInputTime).TotalMilliseconds >= CsvPasteIdleMilliseconds &&
                TryParseExistingDocumentPaths(buffer.ToString(), out _))
            {
                Console.WriteLine();
                ProcessDocumentInput(buffer.ToString(), ref backupCreated);
                buffer.Clear();
                Console.WriteLine();
                DrawDocumentArchivePrompt();
                lastTextInputTime = DateTime.MinValue;
            }

            Thread.Sleep(10);
        }
    }

    private static void DrawDocumentArchivePage()
    {
        ClearForRedraw();
        Console.CursorVisible = false;
        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Upload / Archive Documents");
        Console.WriteLine();
    }

    private static void DrawDocumentArchivePrompt()
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        if (Console.WindowWidth >= 60)
            Console.Write("Drop/paste document paths here.  ");
        WriteKeyBadge("[Enter]");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.Write(" Add    ");
        WriteKeyBadge("[Esc]");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(" Back");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("> ");
        Console.ResetColor();
        Console.CursorVisible = true;
    }

    private static void ProcessDocumentInput(
        string input,
        ref bool backupCreated)
    {
        IReadOnlyList<string> paths = ParseDocumentPaths(input);
        if (paths.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No document paths were recognized.");
            Console.ResetColor();
            return;
        }

        foreach (string path in paths)
        {
            Console.WriteLine();
            ProcessOneDocument(path, ref backupCreated);
        }
    }

    private static void ProcessOneDocument(
        string path,
        ref bool backupCreated)
    {
        DocumentArchiveCandidate candidate;
        try
        {
            candidate = DocumentArchiveService.Prepare(path);
        }
        catch (Exception exception)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Could not archive document.");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(exception.Message);
            Console.ResetColor();
            return;
        }

        if (candidate.ExistingArchivedPath is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Already archived.");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"File:     {candidate.OriginalFileName}");
            Console.WriteLine($"Archived: {candidate.ExistingArchivedPath}");
            Console.ResetColor();
            return;
        }

        DocumentArchivePlan? plan =
            RunDocumentArchiveWizard(candidate);

        if (plan is null)
        {
            WriteDocumentCancelled(candidate.OriginalFileName);
            return;
        }

        try
        {
            if (!backupCreated)
            {
                DatabaseBackupService.CreateVerifiedBackup();
                backupCreated = true;
            }

            DocumentArchiveResult result = DocumentArchiveService.Archive(
                candidate,
                plan.DocumentType,
                plan.Year,
                plan.Month,
                plan.DisplayName);
            WriteDocumentResult(result);
        }
        catch (Exception exception)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Could not archive document.");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(exception.Message);
            Console.ResetColor();
        }
    }

    private static DocumentArchivePlan? RunDocumentArchiveWizard(
        DocumentArchiveCandidate candidate)
    {
        EnterAlternateScreen();

        try
        {
            string[] typeLabels =
            [
                "Receipt",
                "Additional Receipt",
                "Tax Form",
                "Insurance Statement"
            ];

            int suggestedType =
                (int)SuggestDocumentType(candidate.OriginalFileName) - 1;

            int? selectedType = WaitForDocumentSelectionPage(
                candidate.OriginalFileName,
                "What kind of document is this?",
                typeLabels,
                suggestedType,
                selectedTypeLabel: null,
                selectedYear: null,
                selectedMonth: null,
                useGrid: false);

            if (selectedType is null)
                return null;

            ArchivedDocumentType documentType =
                (ArchivedDocumentType)(selectedType.Value + 1);
            string typeLabel =
                DocumentArchiveService.GetTypeLabel(documentType);

            int[] years = Enumerable.Range(
                    FirstAccountingYear,
                    DateTime.Today.Year - FirstAccountingYear + 1)
                .Reverse()
                .ToArray();

            int suggestedYear = SuggestYear(candidate.OriginalFileName);
            int yearIndex = Array.IndexOf(years, suggestedYear);
            if (yearIndex < 0)
                yearIndex = 0;

            int? selectedYear = WaitForDocumentSelectionPage(
                candidate.OriginalFileName,
                "Choose the accounting year:",
                years.Select(value =>
                    value.ToString(CultureInfo.InvariantCulture)).ToArray(),
                yearIndex,
                typeLabel,
                selectedYear: null,
                selectedMonth: null,
                useGrid: false);

            if (selectedYear is null)
                return null;

            int year = years[selectedYear.Value];
            int? month = null;

            if (documentType == ArchivedDocumentType.Receipt)
            {
                string[] monthNames = CultureInfo.CurrentCulture
                    .DateTimeFormat.MonthNames
                    .Take(12)
                    .ToArray();

                int suggestedMonth =
                    SuggestMonth(candidate.OriginalFileName, year);

                int? selectedMonth = WaitForDocumentSelectionPage(
                    candidate.OriginalFileName,
                    "Choose the receipt month:",
                    monthNames,
                    suggestedMonth - 1,
                    typeLabel,
                    year,
                    selectedMonth: null,
                    useGrid: true);

                if (selectedMonth is null)
                    return null;

                month = selectedMonth.Value + 1;
            }

            string suggestedName = SuggestDisplayName(
                candidate.OriginalFileName,
                documentType);

            string? displayName = ReadDocumentNamePage(
                candidate.OriginalFileName,
                typeLabel,
                year,
                month,
                suggestedName);

            if (displayName is null)
                return null;

            return new DocumentArchivePlan(
                documentType,
                year,
                month,
                displayName);
        }
        finally
        {
            ExitAlternateScreen();
        }
    }

    private static int? WaitForDocumentSelectionPage(
        string fileName,
        string prompt,
        IReadOnlyList<string> options,
        int selectedIndex,
        string? selectedTypeLabel,
        int? selectedYear,
        int? selectedMonth,
        bool useGrid)
    {
        if (options.Count == 0)
            throw new ArgumentException("At least one option is required.", nameof(options));

        selectedIndex = Math.Clamp(selectedIndex, 0, options.Count - 1);
        var numberBuffer = new StringBuilder();
        DateTime lastNumberKeyTime = DateTime.MinValue;

        DocumentSelectionLayout layout = DrawDocumentSelectionPage(
            fileName,
            prompt,
            options,
            selectedIndex,
            selectedTypeLabel,
            selectedYear,
            selectedMonth,
            useGrid);

        int drawnWidth = layout.WindowWidth;
        int drawnHeight = layout.WindowHeight;
        int latestWidth = drawnWidth;
        int latestHeight = drawnHeight;
        DateTime lastResizeTime = DateTime.MinValue;

        while (true)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape)
                    return null;

                if (key.Key == ConsoleKey.Enter)
                    return selectedIndex;

                int next = selectedIndex;
                bool changed = false;

                if (TryGetDocumentNavigationDelta(
                        key.Key,
                        layout.Columns,
                        out int delta))
                {
                    numberBuffer.Clear();
                    next = WrapSelection(selectedIndex + delta, options.Count);
                    changed = next != selectedIndex;
                }
                else if (TryGetDigitKey(key.Key, out int digit))
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastNumberKeyTime).TotalMilliseconds > 800)
                        numberBuffer.Clear();

                    numberBuffer.Append((char)('0' + digit));
                    lastNumberKeyTime = now;

                    if (numberBuffer.Length > 2)
                    {
                        char last = numberBuffer[^1];
                        numberBuffer.Clear();
                        numberBuffer.Append(last);
                    }

                    if (int.TryParse(
                            numberBuffer.ToString(),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out int number) &&
                        number >= 1 &&
                        number <= options.Count)
                    {
                        next = number - 1;
                        changed = next != selectedIndex;
                    }
                    else if (!HasNumericOptionPrefix(
                                 numberBuffer.ToString(),
                                 options.Count))
                    {
                        numberBuffer.Clear();
                    }
                }

                if (!changed)
                    continue;

                selectedIndex = next;
                RedrawDocumentOptions(
                    layout.OptionsTop,
                    options,
                    selectedIndex,
                    layout.Columns,
                    layout.CellWidth);
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
                layout = DrawDocumentSelectionPage(
                    fileName,
                    prompt,
                    options,
                    selectedIndex,
                    selectedTypeLabel,
                    selectedYear,
                    selectedMonth,
                    useGrid);

                drawnWidth = layout.WindowWidth;
                drawnHeight = layout.WindowHeight;
                latestWidth = drawnWidth;
                latestHeight = drawnHeight;
            }

            Thread.Sleep(10);
        }
    }

    private static DocumentSelectionLayout DrawDocumentSelectionPage(
        string fileName,
        string prompt,
        IReadOnlyList<string> options,
        int selectedIndex,
        string? selectedTypeLabel,
        int? selectedYear,
        int? selectedMonth,
        bool useGrid)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Upload / Archive Documents");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"File:  {fileName}");
        if (!string.IsNullOrWhiteSpace(selectedTypeLabel))
            Console.WriteLine($"Type:  {selectedTypeLabel}");
        if (selectedYear.HasValue)
            Console.WriteLine($"Year:  {selectedYear.Value}");
        if (selectedMonth.HasValue)
        {
            Console.WriteLine(
                $"Month: {CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(selectedMonth.Value)}");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(prompt);
        Console.WriteLine();

        int columns = useGrid
            ? GetDocumentGridColumns(Console.WindowWidth, options.Count)
            : 1;
        int cellWidth = Math.Max(
            1,
            Math.Max(1, Console.WindowWidth - 1) / columns);
        int optionsTop = Console.CursorTop;

        DrawDocumentOptions(
            options,
            selectedIndex,
            columns,
            cellWidth);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Use ");
        DrawKeyHint("↑/↓/←/→");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" or ");
        DrawKeyHint($"1-{options.Count}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" to choose    ");
        DrawKeyHint("Enter");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" Select    ");
        DrawKeyHint("Esc");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" Cancel");
        Console.ResetColor();

        return new DocumentSelectionLayout(
            optionsTop,
            columns,
            cellWidth,
            Console.WindowWidth,
            Console.WindowHeight);
    }

    private static void DrawDocumentOptions(
        IReadOnlyList<string> options,
        int selectedIndex,
        int columns,
        int cellWidth)
    {
        int rows = (options.Count + columns - 1) / columns;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                if (index >= options.Count)
                {
                    Console.Write(new string(' ', cellWidth));
                    continue;
                }

                DrawDocumentOptionCell(
                    index,
                    options[index],
                    index == selectedIndex,
                    cellWidth);
            }

            Console.WriteLine();
        }
    }

    private static void RedrawDocumentOptions(
        int optionsTop,
        IReadOnlyList<string> options,
        int selectedIndex,
        int columns,
        int cellWidth)
    {
        Console.CursorVisible = false;
        int rows = (options.Count + columns - 1) / columns;

        try
        {
            for (int row = 0; row < rows; row++)
            {
                Console.SetCursorPosition(0, optionsTop + row);

                for (int column = 0; column < columns; column++)
                {
                    int index = row * columns + column;
                    if (index >= options.Count)
                    {
                        Console.Write(new string(' ', cellWidth));
                        continue;
                    }

                    DrawDocumentOptionCell(
                        index,
                        options[index],
                        index == selectedIndex,
                        cellWidth);
                }

                int usedWidth = Math.Min(
                    Console.WindowWidth,
                    columns * cellWidth);
                int remainder = Math.Max(
                    0,
                    Console.WindowWidth - usedWidth - 1);
                if (remainder > 0)
                    Console.Write(new string(' ', remainder));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void DrawDocumentOptionCell(
        int index,
        string label,
        bool selected,
        int cellWidth)
    {
        string numberedLabel =
            $"{index + 1,2}. {label}";
        int contentWidth = Math.Max(1, cellWidth - 3);

        if (numberedLabel.Length > contentWidth)
        {
            numberedLabel = contentWidth <= 1
                ? numberedLabel[..contentWidth]
                : numberedLabel[..(contentWidth - 1)] + "…";
        }

        Console.ResetColor();
        Console.ForegroundColor = selected
            ? ConsoleColor.Magenta
            : ConsoleColor.DarkGray;
        Console.Write(selected ? " > " : "   ");

        if (selected)
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.Magenta;
            Console.Write(numberedLabel);
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(numberedLabel);
        }

        int padding = Math.Max(
            0,
            cellWidth - 3 - numberedLabel.Length);
        if (padding > 0)
            Console.Write(new string(' ', padding));

        Console.ResetColor();
    }

    private static int GetDocumentGridColumns(
        int windowWidth,
        int optionCount)
    {
        if (optionCount <= 4)
            return 1;

        if (windowWidth >= 76)
            return 4;

        if (windowWidth >= 54)
            return 3;

        return 2;
    }

    private static bool TryGetDocumentNavigationDelta(
        ConsoleKey key,
        int columns,
        out int delta)
    {
        delta = key switch
        {
            ConsoleKey.LeftArrow => -1,
            ConsoleKey.RightArrow => 1,
            ConsoleKey.UpArrow => -columns,
            ConsoleKey.DownArrow => columns,
            _ => 0
        };

        return delta != 0;
    }

    private static bool TryGetDigitKey(
        ConsoleKey key,
        out int digit)
    {
        digit = key switch
        {
            ConsoleKey.D0 or ConsoleKey.NumPad0 => 0,
            ConsoleKey.D1 or ConsoleKey.NumPad1 => 1,
            ConsoleKey.D2 or ConsoleKey.NumPad2 => 2,
            ConsoleKey.D3 or ConsoleKey.NumPad3 => 3,
            ConsoleKey.D4 or ConsoleKey.NumPad4 => 4,
            ConsoleKey.D5 or ConsoleKey.NumPad5 => 5,
            ConsoleKey.D6 or ConsoleKey.NumPad6 => 6,
            ConsoleKey.D7 or ConsoleKey.NumPad7 => 7,
            ConsoleKey.D8 or ConsoleKey.NumPad8 => 8,
            ConsoleKey.D9 or ConsoleKey.NumPad9 => 9,
            _ => -1
        };

        return digit >= 0;
    }

    private static bool HasNumericOptionPrefix(
        string prefix,
        int optionCount)
    {
        if (prefix.Length == 0)
            return true;

        for (int number = 1; number <= optionCount; number++)
        {
            if (number.ToString(CultureInfo.InvariantCulture)
                .StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadDocumentNamePage(
        string fileName,
        string typeLabel,
        int year,
        int? month,
        string suggestedName)
    {
        var buffer = new StringBuilder();
        DrawDocumentNamePage(
            fileName,
            typeLabel,
            year,
            month,
            suggestedName,
            buffer.ToString());

        int drawnWidth = Console.WindowWidth;
        int drawnHeight = Console.WindowHeight;
        int latestWidth = drawnWidth;
        int latestHeight = drawnHeight;
        DateTime lastResizeTime = DateTime.MinValue;

        while (true)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape)
                    return null;

                if (key.Key == ConsoleKey.Enter)
                {
                    string entered = buffer.ToString().Trim();
                    return entered.Length == 0
                        ? suggestedName
                        : entered;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
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
                DrawDocumentNamePage(
                    fileName,
                    typeLabel,
                    year,
                    month,
                    suggestedName,
                    buffer.ToString());
                drawnWidth = Console.WindowWidth;
                drawnHeight = Console.WindowHeight;
                latestWidth = drawnWidth;
                latestHeight = drawnHeight;
            }

            Thread.Sleep(10);
        }
    }

    private static void DrawDocumentNamePage(
        string fileName,
        string typeLabel,
        int year,
        int? month,
        string suggestedName,
        string inputBuffer)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Upload / Archive Documents");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"File:  {fileName}");
        Console.WriteLine($"Type:  {typeLabel}");
        Console.WriteLine($"Year:  {year}");
        if (month.HasValue)
        {
            Console.WriteLine(
                $"Month: {CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month.Value)}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "Choose a short, recognizable name for the archived file.");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Press ");
        DrawKeyHint("Enter");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" to keep the suggested name, or ");
        DrawKeyHint("Esc");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to cancel.");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.Write($"Name [{suggestedName}]: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(inputBuffer);
        Console.ResetColor();
        Console.CursorVisible = true;
    }

    private static void EnterAlternateScreen()
    {
        Console.CursorVisible = false;
        Console.Write("\x1b[?1049h");
        Console.Out.Flush();
    }

    private static void ExitAlternateScreen()
    {
        Console.ResetColor();
        Console.CursorVisible = false;
        Console.Write("\x1b[?1049l");
        Console.Out.Flush();
    }

    private static void WriteDocumentResult(DocumentArchiveResult result)
    {
        Console.ForegroundColor = result.AlreadyArchived
            ? ConsoleColor.Yellow
            : ConsoleColor.Green;
        Console.WriteLine(result.AlreadyArchived
            ? "Already archived."
            : "Archived successfully.");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"File:     {result.OriginalFileName}");
        Console.WriteLine($"Type:     {DocumentArchiveService.GetTypeLabel(result.DocumentType)}");
        Console.WriteLine($"Year:     {result.Year}");
        if (result.Month.HasValue)
            Console.WriteLine($"Month:    {CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(result.Month.Value)}");
        Console.WriteLine($"Archived: {result.ArchivedPath}");
        Console.ResetColor();
    }

    private static void WriteDocumentCancelled(string fileName)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Document skipped.");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"File: {fileName}");
        Console.ResetColor();
    }

    private static ArchivedDocumentType SuggestDocumentType(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (Regex.IsMatch(stem, @"(^|[^A-Z0-9])(W-?2|1099|TAX)([^A-Z0-9]|$)", RegexOptions.IgnoreCase))
            return ArchivedDocumentType.TaxForm;
        if (Regex.IsMatch(stem, @"BCBS|AETNA|CIGNA|UNITED\s*HEALTH\s*CARE|UNITEDHEALTHCARE|UMR", RegexOptions.IgnoreCase))
            return ArchivedDocumentType.InsuranceStatement;
        return ArchivedDocumentType.Receipt;
    }

    private static int SuggestYear(string fileName)
    {
        Match match = Regex.Match(fileName, @"(?<!\d)(20\d{2})(?!\d)");
        return match.Success &&
               int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int year) &&
               year >= FirstAccountingYear && year <= DateTime.Today.Year
            ? year
            : DateTime.Today.Year;
    }

    private static int SuggestMonth(string fileName, int year)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        Match yearMonth = Regex.Match(
            stem,
            $@"(?<!\d){year}[-_. ](0?[1-9]|1[0-2])(?!\d)");
        if (yearMonth.Success && int.TryParse(
                yearMonth.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int month))
        {
            return month;
        }

        Match monthDayYear = Regex.Match(
            stem,
            @"(?<!\d)(0?[1-9]|1[0-2])[-_. ](?:0?[1-9]|[12]\d|3[01])[-_. ]20\d{2}(?!\d)");
        if (monthDayYear.Success && int.TryParse(
                monthDayYear.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out month))
        {
            return month;
        }

        Match compactYearMonth = Regex.Match(
            stem,
            @"(?<!\d)20\d{2}(0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])(?!\d)");
        if (compactYearMonth.Success && int.TryParse(
                compactYearMonth.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out month))
        {
            return month;
        }

        for (int candidate = 1; candidate <= 12; candidate++)
        {
            string full = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(candidate);
            string abbreviated = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(candidate);
            if (stem.Contains(full, StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(stem, $@"(^|[^A-Za-z]){Regex.Escape(abbreviated)}([^A-Za-z]|$)", RegexOptions.IgnoreCase))
            {
                return candidate;
            }
        }

        return DateTime.Today.Month;
    }

    private static string SuggestDisplayName(
        string fileName,
        ArchivedDocumentType documentType)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (Regex.IsMatch(stem, @"^(scan|document|img)[ _-]*\d*$", RegexOptions.IgnoreCase))
        {
            return documentType switch
            {
                ArchivedDocumentType.Receipt => "Receipt_001",
                ArchivedDocumentType.AdditionalReceipt => "Additional_Receipt_001",
                ArchivedDocumentType.TaxForm => "Tax_Form_001",
                ArchivedDocumentType.InsuranceStatement => "Insurance_Statement_001",
                _ => "Document_001"
            };
        }

        stem = Regex.Replace(
            stem,
            @"^20\d{2}[-_. ]+(?:0[1-9]|1[0-2])[-_. ]+",
            string.Empty);
        stem = Regex.Replace(stem, @"^20\d{2}[-_. ]+", string.Empty);
        stem = Regex.Replace(stem, @"[-_. ]+20\d{2}$", string.Empty);
        string suggested = Regex.Replace(stem, @"\s+", "_");
        return suggested.Length == 0 ? "Document_001" : suggested;
    }

    private static IReadOnlyList<string> ParseDocumentPaths(string input)
    {
        string wholePath = NormalizePathInput(input);
        if (File.Exists(wholePath))
            return [Path.GetFullPath(wholePath)];

        var paths = new List<string>();
        foreach (Match match in Regex.Matches(input, "\"([^\"]+)\"|'([^']+)'|(\\S+)"))
        {
            string value = match.Groups[1].Success
                ? match.Groups[1].Value
                : match.Groups[2].Success
                    ? match.Groups[2].Value
                    : match.Groups[3].Value;
            if (!string.IsNullOrWhiteSpace(value))
                paths.Add(Path.GetFullPath(NormalizePathInput(value)));
        }
        return paths;
    }

    private static bool TryParseExistingDocumentPaths(
        string input,
        out IReadOnlyList<string> paths)
    {
        paths = ParseDocumentPaths(input);
        return paths.Count > 0 && paths.All(File.Exists);
    }

    private static void RunAbout()
    {
        WaitForEscapeWithResize(DrawAboutPage);
    }

    private static void DrawAboutPage()
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("About SoloPractice");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(
            "SoloPractice is a local accounting and document-organization tool designed to make the practice's routine bookkeeping easier.");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("What SoloPractice does");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("  - Imports Chase CSV downloads into the SoloPractice database.");
        Console.WriteLine("  - Generates, updates, opens, and syncs yearly accounting spreadsheets.");
        Console.WriteLine("  - Archives receipts, tax forms, and insurance statements into year-based folders.");
        Console.WriteLine("  - Detects duplicate archived documents and creates verified database backups before write operations.");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("Where your files are kept");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"  Data folder: {AppPaths.ApplicationDirectory}");
        Console.WriteLine($"  Database:    {AppPaths.DatabasePath}");
        Console.WriteLine($"  Backups:     {AppPaths.BackupsDirectory}");
        Console.WriteLine();
        Console.WriteLine(
            "SoloPractice works with local files; the data folder can be changed with the SOLOPRACTICE_DATA_DIRECTORY environment variable.");

        Version? version = typeof(Program).Assembly.GetName().Version;
        if (version is not null)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"Version {version}");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Press ");
        WriteKeyBadge("[Esc]");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" Back");
        Console.ResetColor();
    }

    private static void NotImplemented(
        string feature)
    {
        WaitForEscapeWithResize(
            () => DrawNotImplementedPage(feature));
    }

    private static void DrawNotImplementedPage(
        string feature)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(
            $"{feature} is not implemented yet.");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Press ");
        WriteKeyBadge("[Esc]");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to go back.");
        Console.ResetColor();
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
        Console.ResetColor();
        Console.CursorVisible = false;

        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            Console.Write("\x1b[2J\x1b[H");
        }
        catch (PlatformNotSupportedException)
        {
            Console.Write("\x1b[2J\x1b[H");
        }

        Console.Out.Flush();
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

    private readonly record struct DocumentSelectionLayout(
        int OptionsTop,
        int Columns,
        int CellWidth,
        int WindowWidth,
        int WindowHeight);

    private sealed record DocumentArchivePlan(
        ArchivedDocumentType DocumentType,
        int Year,
        int? Month,
        string DisplayName);

    private readonly record struct MainMenuLayout(
        int OptionsTop,
        int PromptTop,
        int WindowWidth,
        int WindowHeight);

    private sealed record ImportPageEntry(
        string Status,
        ConsoleColor StatusColor,
        IReadOnlyList<string> DetailLines);

    private readonly record struct AccountingMenuLayout(
        int OptionsTop,
        int WindowWidth,
        int WindowHeight);

    private enum AccountingWorkbookAction
    {
        Generate,
        Update,
        Open
    }

    private sealed record AccountingYearOption(
        int Year,
        AccountingWorkbookAction Action,
        string WorkbookPath,
        string Label);

    private enum MainMenuAction
    {
        ImportChase,
        AccountingWorksheet,
        DocumentArchive,
        About,
        Exit
    }

    private readonly record struct MainMenuSelection(
        MainMenuAction Action,
        string? CsvPath = null);
}
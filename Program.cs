using SoloPractice.Data;
using SoloPractice.Services;
using SoloPractice.Utilities;
using System.Text;

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
        (MainMenuAction.ReceiptScans, "Upload Receipt Scans"),
        (MainMenuAction.InsuranceAndTaxForms, "Upload Insurance Company Statements and Tax Forms"),
        (MainMenuAction.About, "About")
    ];

    private static void Main(string[] args)
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
                        "CSV > ",
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
                        "CSV > ",
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
            

            SoloPractice is a C# program meant to turn CSV files downloaded from Chase's website into a flexible database of transactions. This database can then be used to automate and streamline accounting by automatically filling in most of the cells in a spreadsheet. There are also tools for organizing scans of receipts, insurance company statements, and tax forms.
            
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
        DrawKeyHint("1-5");
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
        Console.Write("CSV > ");
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
        var history = new List<ImportPageEntry>();
        var buffer = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(initialCsvPath))
        {
            history.Add(
                ImportOneChaseFile(
                    NormalizePathInput(initialCsvPath)));
        }

        ImportPageLayout layout =
            DrawImportPage(history, buffer.ToString());

        int drawnWidth = layout.WindowWidth;
        int drawnHeight = layout.WindowHeight;
        int latestWidth = drawnWidth;
        int latestHeight = drawnHeight;
        DateTime lastResizeTime = DateTime.MinValue;
        DateTime lastTextInputTime = DateTime.MinValue;

        while (true)
        {
            bool redrawn = false;

            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key =
                    Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape)
                {
                    Console.CursorVisible = false;
                    Console.ResetColor();
                    return;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (buffer.Length == 0)
                        continue;

                    history.Add(
                        ImportOneChaseFile(
                            NormalizePathInput(buffer.ToString())));
                    buffer.Clear();

                    layout = DrawImportPage(
                        history,
                        buffer.ToString());
                    drawnWidth = layout.WindowWidth;
                    drawnHeight = layout.WindowHeight;
                    latestWidth = drawnWidth;
                    latestHeight = drawnHeight;
                    lastTextInputTime = DateTime.MinValue;
                    redrawn = true;
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        lastTextInputTime = DateTime.UtcNow;
                        layout = DrawImportPage(
                            history,
                            buffer.ToString());
                        drawnWidth = layout.WindowWidth;
                        drawnHeight = layout.WindowHeight;
                        latestWidth = drawnWidth;
                        latestHeight = drawnHeight;
                        redrawn = true;
                        break;
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

            if (redrawn)
                continue;

            // Same idle-submit behavior as the main menu: a dropped CSV is
            // imported as soon as the full path has arrived.
            if (buffer.Length > 0 &&
                (DateTime.UtcNow - lastTextInputTime)
                    .TotalMilliseconds >= CsvPasteIdleMilliseconds &&
                TryGetExistingCsvPath(
                    buffer.ToString(),
                    out string? droppedCsvPath))
            {
                history.Add(
                    ImportOneChaseFile(droppedCsvPath!));
                buffer.Clear();

                layout = DrawImportPage(
                    history,
                    buffer.ToString());
                drawnWidth = layout.WindowWidth;
                drawnHeight = layout.WindowHeight;
                latestWidth = drawnWidth;
                latestHeight = drawnHeight;
                lastTextInputTime = DateTime.MinValue;
                continue;
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
                layout = DrawImportPage(
                    history,
                    buffer.ToString());
                drawnWidth = layout.WindowWidth;
                drawnHeight = layout.WindowHeight;
                latestWidth = drawnWidth;
                latestHeight = drawnHeight;
            }

            Thread.Sleep(10);
        }
    }

    private static ImportPageLayout DrawImportPage(
        IReadOnlyList<ImportPageEntry> history,
        string inputBuffer)
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

        if (history.Count > 0)
        {
            // Keep the controls and prompt close to the cursor. Show as many of
            // the newest import results as fit above them, rather than allowing
            // old results to push the controls off-screen.
            int availableHistoryRows = Math.Max(
                0,
                Console.WindowHeight - Console.CursorTop - 7);

            var visible = new List<ImportPageEntry>();
            int usedRows = 0;

            for (int i = history.Count - 1; i >= 0; i--)
            {
                int entryRows = GetImportPageEntryLineCount(history[i]) + 1;
                if (usedRows + entryRows > availableHistoryRows)
                    break;

                visible.Add(history[i]);
                usedRows += entryRows;
            }

            visible.Reverse();
            int hiddenCount = history.Count - visible.Count;

            if (hiddenCount > 0 && availableHistoryRows > usedRows)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(
                    $"… {hiddenCount:N0} earlier import result(s) hidden …");
                usedRows++;
            }

            foreach (ImportPageEntry entry in visible)
            {
                DrawImportPageEntry(entry);
                Console.WriteLine();
            }
        }

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(
            "Drag a Chase .csv here, or type/paste its path.");

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("Press ");
        WriteKeyBadge("[Enter]");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to import the typed/pasted path.");

        Console.Write("Press ");
        WriteKeyBadge("[Esc]");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" to go back.");

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        int promptTop = Console.CursorTop;
        Console.Write("> ");
        Console.Write(inputBuffer);
        Console.ResetColor();
        Console.CursorVisible = true;

        return new ImportPageLayout(
            promptTop,
            Console.WindowWidth,
            Console.WindowHeight);
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
                    path,
                    "This exact Chase download has already been imported.",
                    ConsoleColor.Yellow,
                    [result.FileName]);
            }

            return new ImportPageEntry(
                path,
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
                path,
                "Import failed.",
                ConsoleColor.Red,
                detailLines);
        }
    }

    private static int GetImportPageEntryLineCount(
        ImportPageEntry entry) =>
        3 +
        (entry.DetailLines.Count > 0
            ? 1 + entry.DetailLines.Count
            : 0);

    private static void DrawImportPageEntry(
        ImportPageEntry entry)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("> ");
        Console.WriteLine(entry.Path);
        Console.WriteLine();

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

        if (option.Action == AccountingWorkbookAction.Open)
        {
            AccountingWorkbookService.OpenWorkbook(option.WorkbookPath);
            return;
        }

        GenerateAccountingWorksheet(option.Year);
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

    private static void GenerateAccountingWorksheet(
        int year)
    {
        DrawAccountingProcessingPage(year);

        try
        {
            AppPaths.EnsureAccountingYearDirectoriesExist(year);
            string workbookPath =
                AccountingWorkbookService.GetWorkbookPath(year);

            DatabaseBackupResult? backup =
                DatabaseBackupService.CreateVerifiedBackup();

            AccountingWorkbookSyncResult? preSync = null;
            bool legacyWorkbook = false;

            if (File.Exists(workbookPath))
            {
                legacyWorkbook =
                    !AccountingWorkbookService.IsDatabaseBackedWorkbook(
                        year,
                        workbookPath);

                if (!legacyWorkbook)
                {
                    preSync =
                        AccountingWorkbookService.ImportWorkbookEdits(
                            year,
                            workbookPath);
                }
            }

            AccountingGenerationResult generated =
                AccountingLedgerService.GenerateMissingEntries(year);

            if (legacyWorkbook)
            {
                preSync =
                    AccountingWorkbookService.ImportLegacyWorkbookEdits(
                        year,
                        workbookPath);
            }

            AccountingWorkbookResult result =
                AccountingWorkbookService.Generate(year);

            AccountingWorkbookService.OpenWorkbook(
                result.WorkbookPath);

            ConsoleKey next = WaitForEnterOrEscapeWithResize(
                () => DrawAccountingWorkbookReadyPage(
                    year,
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
        int year)
    {
        ClearForRedraw();
        Console.CursorVisible = false;

        DrawHeader();
        Console.WriteLine();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(
            $"Generate / Update {year} Accounting Spreadsheet");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(
            $"Updating the {year} accounting ledger from SoloPractice.db...");
        Console.ResetColor();
    }

    private static void DrawAccountingWorkbookReadyPage(
        int year,
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
        Console.WriteLine(
            result.ReplacedExistingWorkbook
                ? $"{year} accounting workbook updated successfully."
                : $"{year} accounting workbook generated successfully.");

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
        Console.WriteLine(
            " to sync saved spreadsheet changes back into SoloPractice.");
        Console.Write("Press ");
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

    private readonly record struct MainMenuLayout(
        int OptionsTop,
        int PromptTop,
        int WindowWidth,
        int WindowHeight);

    private readonly record struct ImportPageLayout(
        int PromptTop,
        int WindowWidth,
        int WindowHeight);

    private sealed record ImportPageEntry(
        string Path,
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
        ReceiptScans,
        InsuranceAndTaxForms,
        About,
        Exit
    }

    private readonly record struct MainMenuSelection(
        MainMenuAction Action,
        string? CsvPath = null);
}

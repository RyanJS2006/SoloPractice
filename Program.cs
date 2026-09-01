using SoloPractice.Data;
using SoloPractice.Utilities;
using SoloPractice.Services;

namespace SoloPractice;

internal static class Program
{
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

            ConsoleKey key = WaitForMenuKey();

            switch (key)
            {
                case ConsoleKey.D1:
                case ConsoleKey.NumPad1:
                    ImportChaseDownload();
                    break;

                case ConsoleKey.D2:
                case ConsoleKey.NumPad2:
                    NotImplemented("Workbook generation");
                    break;

                case ConsoleKey.D3:
                case ConsoleKey.NumPad3:
                    NotImplemented("Receipt scans");
                    break;

                case ConsoleKey.Escape:
                    return;

                default:
                    continue;
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


              1. Import Chase download
              2. Generate/update/open accounting worksheet
              3. Upload receipt scans

            """);

        Console.ForegroundColor = ConsoleColor.DarkGray; Console.Write("Press ");
        Console.ForegroundColor = ConsoleColor.Cyan; Console.Write("[Esc]");
        Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine(" to exit");
        Console.WriteLine(); Console.ForegroundColor = ConsoleColor.White;
        Console.Write("> "); Console.CursorVisible = true;
    }

    private static void WriteCenteredRainbowBlock(
    string[] lines,
    ConsoleColor[] colors,
    int windowWidth)
    {
        int blockWidth = lines.Max(x => x.Length);

        int left = Math.Max(0, (windowWidth - blockWidth) / 2);

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
        string[] solo = {
        "███████╗ ██████╗ ██╗      ██████╗ ",
        "██╔════╝██╔═══██╗██║     ██╔═══██╗",
        "███████╗██║   ██║██║     ██║   ██║",
        "╚════██║██║   ██║██║     ██║   ██║",
        "███████║╚██████╔╝███████╗╚██████╔╝",
        "╚══════╝ ╚═════╝ ╚══════╝ ╚═════╝ "
        };

        string[] practice = {
        "██████╗ ██████╗  █████╗  ██████╗████████╗██╗ ██████╗███████╗",
        "██╔══██╗██╔══██╗██╔══██╗██╔════╝╚══██╔══╝██║██╔════╝██╔════╝",
        "██████╔╝██████╔╝███████║██║        ██║   ██║██║     █████╗  ",
        "██╔═══╝ ██╔══██╗██╔══██║██║        ██║   ██║██║     ██╔══╝  ",
        "██║     ██║  ██║██║  ██║╚██████╗   ██║   ██║╚██████╗███████╗",
        "╚═╝     ╚═╝  ╚═╝╚═╝  ╚═╝ ╚═════╝   ╚═╝   ╚═╝ ╚═════╝╚══════╝"
        };

        ConsoleColor[] soloRainbow = { ConsoleColor.Red, ConsoleColor.DarkYellow, ConsoleColor.Green, ConsoleColor.Cyan, ConsoleColor.Blue, ConsoleColor.Magenta };

        const int gap = 1;
        int windowWidth = Console.WindowWidth;
        int soloWidth = solo.Max(line => line.Length);
        int practiceWidth = practice.Max(line => line.Length);
        int combinedWidth = soloWidth + gap + practiceWidth;
        bool useWideLayout = combinedWidth + 4 <= windowWidth;

        Console.WriteLine();

        if (useWideLayout) {
            int left = Math.Max(0, (windowWidth - combinedWidth) / 2);
            for (int i = 0; i < solo.Length; i++) {
                Console.Write(new string(' ', left));
                Console.ForegroundColor = soloRainbow[i];
                Console.Write(solo[i].PadRight(soloWidth));
                Console.Write(new string(' ', gap));
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(practice[i]);
            }
        } else {
            WriteCenteredRainbowBlock(solo, soloRainbow, windowWidth);
            Console.WriteLine();
            WriteCenteredBlock(practice, ConsoleColor.White, windowWidth);
        }
        Console.WriteLine();
        DrawFullWidthDivider();
        Console.ResetColor();
    }

    private static void DrawFullWidthDivider()
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        int width = Console.WindowWidth;
        if (width <= 0)
            return;
        Console.Write(new string('─', width));
    }

    private static void ClearForRedraw()
    {
        Console.Write("\x1b[2J\x1b[3J\x1b[H");
    }

    private static void WriteCenteredBlock(string[] lines, ConsoleColor color, int windowWidth)
    {
        int blockWidth = lines.Max(line => line.Length);
        int left = Math.Max(0, (windowWidth - blockWidth) / 2);

        Console.ForegroundColor = color;
        foreach (string line in lines)
        {
            Console.Write(new string(' ', left));
            Console.WriteLine(line);
        }
    }

    private static void NotImplemented(string feature)
    {
        Console.WriteLine();
        Console.WriteLine($"{feature} is not implemented yet.");
        Pause();
    }

    private static void Pause()
    {
        Console.WriteLine();
        Console.WriteLine("Press Enter to continue...");
        Console.ReadLine();
    }

    private static ConsoleKey WaitForMenuKey()
    {
        int drawnWidth = Console.WindowWidth;
        int drawnHeight = Console.WindowHeight;

        int latestWidth = drawnWidth;
        int latestHeight = drawnHeight;

        DateTime lastResizeTime = DateTime.MinValue;

        const int resizeDebounceMilliseconds = 100;

        while (true)
        {
            // Handle keyboard input without blocking.
            if (Console.KeyAvailable)
            {
                return Console.ReadKey(intercept: true).Key;
            }

            int currentWidth = Console.WindowWidth;
            int currentHeight = Console.WindowHeight;

            // Detect a new resize.
            if (currentWidth != latestWidth ||
                currentHeight != latestHeight)
            {
                latestWidth = currentWidth;
                latestHeight = currentHeight;

                lastResizeTime = DateTime.UtcNow;
            }

            // Wait until resizing has stopped briefly before redrawing.
            bool sizeChanged =
                latestWidth != drawnWidth ||
                latestHeight != drawnHeight;

            bool resizeSettled =
                (DateTime.UtcNow - lastResizeTime)
                .TotalMilliseconds >= resizeDebounceMilliseconds;

            if (sizeChanged && resizeSettled)
            {
                DrawMainMenu();

                drawnWidth = latestWidth;
                drawnHeight = latestHeight;
            }

            Thread.Sleep(10);
        }
    }

    private static void ImportChaseDownload()
    {
        Console.Clear();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Import Chase Download");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("=====================");
        Console.ResetColor();

        Console.WriteLine();
        Console.WriteLine("Enter the path to a Chase CSV.");
        Console.WriteLine("You can also drag the CSV file into this window.");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write("> ");

        string? input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
        {
            Console.WriteLine();
            Console.WriteLine("Import cancelled.");
            Pause();
            return;
        }

        string path = input.Trim().Trim('"');

        Console.WriteLine();

        try
        {
            ChaseImportResult result =
                ChaseCsvImporter.Import(path);

            if (result.FileAlreadyImported)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("This exact Chase download has already been imported.");

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine();
                Console.WriteLine(result.FileName);

                Console.ResetColor();
                Pause();
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

        Pause();
    }
}


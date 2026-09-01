using SoloPractice.Data;
using SoloPractice.Utilities;

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
            Console.Error.WriteLine(
                "Failed to initialize the accounting database.");

            Console.Error.WriteLine();
            Console.Error.WriteLine(exception);

            return;
        }

        RunMainMenu();
    }

    private static void RunMainMenu()
    {
        while (true)
        {
            /*  ____        _       ____                 _   _
             * / ___|  ___ | | ___ |  _ \ _ __ __ _  ___| |_(_) ___ ___
             * \___ \ / _ \| |/ _ \| |_) | '__/ _` |/ __| __| |/ __/ _ \
             *  ___) | (_) | | (_) |  __/| | | (_| | (__| |_| | (_|  __/
             * |____/ \___/|_|\___/|_|   |_|  \__,_|\___|\__|_|\___\___|
             */
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Green; Console.Write("  ____        _      "); Console.ForegroundColor = ConsoleColor.White; Console.WriteLine(" ____                 _   _");
            Console.ForegroundColor = ConsoleColor.Green; Console.Write(" / ___|  ___ | | ___ "); Console.ForegroundColor = ConsoleColor.White; Console.WriteLine("|  _ \\ _ __ __ _  ___| |_(_) ___ ___");
            Console.ForegroundColor = ConsoleColor.Green; Console.Write(" \\___ \\ / _ \\| |/ _ \\"); Console.ForegroundColor = ConsoleColor.White; Console.WriteLine("""| |_) | '__/ _` |/ __| __| |/ __/ _ \""");
            Console.ForegroundColor = ConsoleColor.Green; Console.Write("  ___) | (_) | | (_) "); Console.ForegroundColor = ConsoleColor.White; Console.WriteLine("|  __/| | | (_| | (__| |_| | (_|  __/");
            Console.ForegroundColor = ConsoleColor.Green; Console.Write(" |____/ \\___/|_|\\___/"); Console.ForegroundColor = ConsoleColor.White; Console.WriteLine("""
                |_|   |_|  \__,_|\___|\__|_|\___\___|

                """);
            Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine("==============================================================================");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("""
                
                    1. Import Chase download
                    2. Generate / update accounting worksheet
                    3. Open accounting worksheet

                """);
            Console.ForegroundColor = ConsoleColor.DarkGray; Console.Write("Press ");
            Console.ForegroundColor = ConsoleColor.Cyan; Console.Write("[Esc] ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("""
                to Exit

                """);
            Console.ForegroundColor = ConsoleColor.White; Console.Write("> ");



            ConsoleKey key = Console.ReadKey(intercept: true).Key;

            switch (key)
            {
                case ConsoleKey.D1:
                case ConsoleKey.NumPad1:
                    NotImplemented("Chase import");
                    break;

                case ConsoleKey.D2:
                case ConsoleKey.NumPad2:
                    NotImplemented("Workbook generation");
                    break;

                case ConsoleKey.D3:
                case ConsoleKey.NumPad3:
                    NotImplemented("Open workbook");
                    break;

                case ConsoleKey.Escape:
                    return;

                default:
                    Console.WriteLine();
                    Console.WriteLine("Invalid option.");
                    Pause();
                    break;
            }
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
}
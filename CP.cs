namespace Ew.Tools.DuplicateFileLinker;

/// <summary>
/// CP Console Printer - Thread-safe console output helpers with simple formatting.
/// </summary>
internal static class CP
{
    private static readonly object _lock = new();
    private const int SEP_WIDTH = 110;

    /// <summary>Prints a header surrounded by separator lines.</summary>
    public static void PrintHeader(string title)
    {
        lock (_lock)
        {
            PrintSeperator();
            Console.WriteLine(title);
            PrintSeperator();
        }
    }

    /// <summary>Prints a separator line.</summary>
    public static void PrintSeperator()
    {
        lock (_lock)
        {
            Console.WriteLine(new string('-', SEP_WIDTH));
        }
    }

    /// <summary>
    /// Prints a single-line progress message (overwrites the current line).
    /// </summary>
    public static void PrintProgress(string message)
    {
        lock (_lock)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(message);
            Console.Write('\r');
            Console.ForegroundColor = old;
        }
    }

    /// <summary>Prints a titled section surrounded by separator lines.</summary>
    public static void PrintSection(string title)
    {
        lock (_lock)
        {
            Console.WriteLine();
            PrintSeperator();
            Console.WriteLine(title);
            PrintSeperator();
        }
    }

    /// <summary>Prints a plain info line.</summary>
    public static void PrintInfo(string message)
    {
        lock (_lock) { Console.WriteLine(message); }
    }

    /// <summary>Prints a warning line to stderr in yellow.</summary>
    public static void PrintWarn(string message)
    {
        lock (_lock)
        {
            var old = Console.ForegroundColor;
            try { Console.ForegroundColor = ConsoleColor.Yellow; Console.Error.WriteLine(message); }
            finally { Console.ForegroundColor = old; }
        }
    }

    /// <summary>Prints an error line to stderr in red.</summary>
    public static void PrintError(string message)
    {
        lock (_lock)
        {
            var old = Console.ForegroundColor;
            try { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine(message); }
            finally { Console.ForegroundColor = old; }
        }
    }
}

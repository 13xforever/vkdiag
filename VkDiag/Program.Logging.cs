using System;

namespace VkDiag;

internal static partial class Program
{
    private static readonly ConsoleColor defaultFgColor = Console.ForegroundColor;
    private static readonly object theDoor = new();
    
    private static void WriteLogLine(ConsoleColor statusColor, string status, string description)
    {
        var val = description.TrimStart();
        var prefix = "";
        if (val.Length < description.Length)
            prefix = description.Substring(0, description.Length - val.Length);

        lock (theDoor)
        {
            Console.Write(prefix + '[');
            Console.ForegroundColor = statusColor;
            Console.Write(status);
            Console.ForegroundColor = defaultFgColor;
            Console.WriteLine("] " + val);
        }
    }

    private static void WriteLogLine(ConsoleColor statusColor, string line)
    {
        lock (theDoor)
        {
            Console.ForegroundColor = statusColor;
            Console.WriteLine(line);
            Console.ForegroundColor = defaultFgColor;
        }
    }

    private static void WriteLogLine(string line)
    {
        lock (theDoor) Console.WriteLine(line);
    }

    private static void WriteLogLine()
    {
        lock (theDoor) Console.WriteLine('\u200b'); // zero width space to workaround bug with emitted \r instead of \r\n
    }
}
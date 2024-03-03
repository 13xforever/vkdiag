using System;

namespace VkDiag;

internal static partial class Program
{
    private static readonly ConsoleColor DefaultFgColor = Console.ForegroundColor;
    private static readonly object TheDoor = new();
    
    private static void WriteLogLine(ConsoleColor statusColor, string status, string description)
    {
        var val = description.TrimStart();
        var prefix = "";
        if (val.Length < description.Length)
            prefix = description.Substring(0, description.Length - val.Length);

        lock (TheDoor)
        {
            Console.Write(prefix + '[');
            Console.ForegroundColor = statusColor;
            Console.Write(status);
            Console.ForegroundColor = DefaultFgColor;
            Console.WriteLine("] " + val);
        }
    }

    private static void WriteLogLine(ConsoleColor statusColor, string line)
    {
        lock (TheDoor)
        {
            Console.ForegroundColor = statusColor;
            Console.WriteLine(line);
            Console.ForegroundColor = DefaultFgColor;
        }
    }

    private static void WriteLogLine(string line)
    {
        lock (TheDoor) Console.WriteLine(line);
    }

    private static void WriteLogLine()
    {
        lock (TheDoor) Console.WriteLine('\u200b'); // zero width space to workaround bug with emitted \r instead of \r\n
    }
}
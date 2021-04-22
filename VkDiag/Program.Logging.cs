using System;

namespace VkDiag
{
    internal static partial class Program
    {
        private static readonly ConsoleColor defaultFgColor = Console.ForegroundColor;

        private static void WriteLogLine(ConsoleColor statusColor, string status, string description)
        {
            description = description ?? "???";
            var val = description.TrimStart();
            string prefix = "";
            if (val.Length < description.Length)
                prefix = description.Substring(0, description.Length - val.Length);
            Console.Write(prefix + '[');
            Console.ForegroundColor = statusColor;
            Console.Write(status);
            Console.ForegroundColor = defaultFgColor;
            Console.WriteLine("] " + val);
        }
    }
}
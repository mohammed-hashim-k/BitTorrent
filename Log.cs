using System;
using System.IO;

namespace BitTorrent
{
    public static class Log
    {
        public static bool IsVerbose { get; set; } =
            string.Equals(Environment.GetEnvironmentVariable("BITTORRENT_VERBOSE"), "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("BITTORRENT_VERBOSE"), "true", StringComparison.OrdinalIgnoreCase);

        public static void Debug(string message)
        {
            if (!IsVerbose)
                return;

            Write(Console.Out, "DEBUG", message);
        }

        public static void Debug(object? context, string message)
        {
            if (!IsVerbose)
                return;

            Write(Console.Out, "DEBUG", Format(context, message));
        }

        public static void Info(string message)
        {
            Write(Console.Out, "INFO", message);
        }

        public static void Info(object? context, string message)
        {
            Write(Console.Out, "INFO", Format(context, message));
        }

        public static void Error(string message)
        {
            Write(Console.Error, "ERROR", message);
        }

        public static void Error(object? context, string message)
        {
            Write(Console.Error, "ERROR", Format(context, message));
        }

        private static void Write(TextWriter writer, string level, string message)
        {
            writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {level} {message}");
        }

        private static string Format(object? context, string message)
        {
            return context == null ? message : $"[{context}] {message}";
        }
    }
}

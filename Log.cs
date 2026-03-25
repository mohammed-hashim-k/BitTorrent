using System;
using System.IO;

namespace BitTorrent
{
    /// <summary>
    /// Provides lightweight console logging with optional verbose protocol traces.
    /// </summary>
    public static class Log
    {
        public static bool IsVerbose { get; set; } =
            string.Equals(Environment.GetEnvironmentVariable("BITTORRENT_VERBOSE"), "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("BITTORRENT_VERBOSE"), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Writes a debug message when verbose logging is enabled.
        /// </summary>
        /// <param name="message">The message to log.</param>
        public static void Debug(string message)
        {
            if (!IsVerbose)
                return;

            Write(Console.Out, "DEBUG", message);
        }

        /// <summary>
        /// Writes a contextual debug message when verbose logging is enabled.
        /// </summary>
        /// <param name="context">The object to include as log context.</param>
        /// <param name="message">The message to log.</param>
        public static void Debug(object? context, string message)
        {
            if (!IsVerbose)
                return;

            Write(Console.Out, "DEBUG", Format(context, message));
        }

        /// <summary>
        /// Writes an informational message to standard output.
        /// </summary>
        /// <param name="message">The message to log.</param>
        public static void Info(string message)
        {
            Write(Console.Out, "INFO", message);
        }

        /// <summary>
        /// Writes an informational message with an object-derived context prefix.
        /// </summary>
        /// <param name="context">The object to include as log context.</param>
        /// <param name="message">The message to log.</param>
        public static void Info(object? context, string message)
        {
            Write(Console.Out, "INFO", Format(context, message));
        }

        /// <summary>
        /// Writes an error message to standard error.
        /// </summary>
        /// <param name="message">The message to log.</param>
        public static void Error(string message)
        {
            Write(Console.Error, "ERROR", message);
        }

        /// <summary>
        /// Writes an error message with an object-derived context prefix.
        /// </summary>
        /// <param name="context">The object to include as log context.</param>
        /// <param name="message">The message to log.</param>
        public static void Error(object? context, string message)
        {
            Write(Console.Error, "ERROR", Format(context, message));
        }

        /// <summary>
        /// Writes a formatted log line to the specified text writer.
        /// </summary>
        /// <param name="writer">The destination writer.</param>
        /// <param name="level">The log severity label.</param>
        /// <param name="message">The message to output.</param>
        private static void Write(TextWriter writer, string level, string message)
        {
            writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {level} {message}");
        }

        /// <summary>
        /// Formats a log message by prefixing it with a context object when one is provided.
        /// </summary>
        /// <param name="context">The optional log context.</param>
        /// <param name="message">The message to format.</param>
        /// <returns>The formatted log message.</returns>
        private static string Format(object? context, string message)
        {
            return context == null ? message : $"[{context}] {message}";
        }
    }
}

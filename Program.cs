using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using BitTorrent;

namespace Program
{
    /// <summary>
    /// Hosts the console application entry point for the BitTorrent tools.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Dispatches client, tracker, and torrent creation commands.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        public static void Main(string[] args)
        {
            if (args.Length == 3 && Int32.TryParse(args[0], out var legacyPort) && File.Exists(args[1]))
            {
                RunClient(legacyPort, args[1], args[2]);
                return;
            }

            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "client":
                    RunClientCommand(args);
                    break;
                case "tracker":
                    RunTrackerCommand(args);
                    break;
                case "create":
                    RunCreateCommand(args);
                    break;
                default:
                    PrintUsage();
                    break;
            }
        }

        private static void RunClientCommand(string[] args)
        {
            if ((args.Length != 4 && args.Length != 5) || !Int32.TryParse(args[1], out var port) || !File.Exists(args[2]))
            {
                Log.Error("client requires: client <port> <torrent-file> <download-directory> [announce-port]");
                Environment.ExitCode = 1;
                return;
            }

            int? announcePort = null;
            if (args.Length == 5)
            {
                if (!Int32.TryParse(args[4], out var parsedAnnouncePort))
                {
                    Log.Error("announce-port must be an integer");
                    Environment.ExitCode = 1;
                    return;
                }

                announcePort = parsedAnnouncePort;
            }

            RunClient(port, args[2], args[3], announcePort);
        }

        private static void RunClient(int port, string torrentPath, string downloadDirectory, int? announcePort = null)
        {
            var client = new Client(port, torrentPath, downloadDirectory, announcePort);
            using var shutdown = new ManualResetEventSlim(false);
            var stopped = false;

            void StopClient()
            {
                if (stopped)
                    return;

                stopped = true;
                try
                {
                    client.Stop();
                }
                finally
                {
                    shutdown.Set();
                }
            }

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                StopClient();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => StopClient();

            client.Start();
            Log.Info($"client listening on port {port}, announcing port {client.AnnouncePort}");
            shutdown.Wait();
        }

        private static void RunTrackerCommand(string[] args)
        {
            if (args.Length < 2 || args.Length > 3)
            {
                Log.Error("tracker requires: tracker <listen-prefix> [advertised-ip]");
                Environment.ExitCode = 1;
                return;
            }

            IPAddress? advertisedAddress = null;
            if (args.Length == 3 && !IPAddress.TryParse(args[2], out advertisedAddress))
            {
                Log.Error("advertised-ip must be a valid IPv4 address");
                Environment.ExitCode = 1;
                return;
            }

            using var cancellation = new CancellationTokenSource();
            using var tracker = new HttpTrackerServer(args[1], advertisedAddress);

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => cancellation.Cancel();

            tracker.RunAsync(cancellation.Token).GetAwaiter().GetResult();
        }

        private static void RunCreateCommand(string[] args)
        {
            if (args.Length < 3 || args.Length > 5)
            {
                Log.Error("create requires: create <source-path> <announce-url> [output-torrent-file] [piece-size]");
                Environment.ExitCode = 1;
                return;
            }

            string sourcePath = args[1];
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                Log.Error("source-path must be an existing file or directory");
                Environment.ExitCode = 1;
                return;
            }

            int pieceSize = 32768;
            if (args.Length == 5 && !Int32.TryParse(args[4], out pieceSize))
            {
                Log.Error("piece-size must be an integer number of bytes");
                Environment.ExitCode = 1;
                return;
            }

            string outputPath = args.Length >= 4
                ? args[3]
                : Path.Combine(Environment.CurrentDirectory, GetTorrentName(sourcePath) + ".torrent");

            Torrent torrent = Torrent.Create(
                sourcePath,
                trackers: new List<string> { args[2] },
                pieceSize: pieceSize,
                comment: "Created by BitTorrent deployment CLI");

            Torrent.SaveToFile(torrent, outputPath);
            Log.Info($"created torrent: {outputPath}");
        }

        private static string GetTorrentName(string sourcePath)
        {
            string normalized = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFileName(normalized);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  BitTorrent <port> <torrent-file> <download-directory>");
            Console.WriteLine("  BitTorrent client <port> <torrent-file> <download-directory> [announce-port]");
            Console.WriteLine("  BitTorrent tracker <listen-prefix> [advertised-ip]");
            Console.WriteLine("  BitTorrent create <source-path> <announce-url> [output-torrent-file] [piece-size]");
        }
    }
}

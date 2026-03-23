using System;
using System.IO;
using BitTorrent;
namespace Program
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            if (args.Length != 3 || !Int32.TryParse(args[0], out var port) || !File.Exists(args[1]))
            {
                Log.Error("requires port, torrent file and download directory as first, second and third arguments");
                return;
            }

            var client = new Client(port, args[1], args[2]);

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                client.Stop();
            };

            client.Start();
            Console.ReadLine();
        }
    }
}

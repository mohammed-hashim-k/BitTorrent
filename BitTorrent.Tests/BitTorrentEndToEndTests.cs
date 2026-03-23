using System.Net;
using System.Net.Sockets;
using System.Text;
using BitTorrent;

namespace BitTorrent.Tests;

public class BitTorrentEndToEndTests
{
    [Fact]
    public void CreatesAndLoadsAWorkingTorrentFile()
    {
        string root = CreateTempDirectory();

        try
        {
            TorrentFixture fixture = CreateFixture(root, "http://127.0.0.1:65535/announce/");

            Assert.True(File.Exists(fixture.TorrentFilePath));

            Torrent loaded = Torrent.LoadFromFile(fixture.TorrentFilePath, fixture.SeedDirectory);

            Assert.Equal("payload.bin", loaded.Name);
            Assert.Single(loaded.Trackers);
            Assert.Equal("http://127.0.0.1:65535/announce/", loaded.Trackers[0].Address);
            Assert.Equal(fixture.Data.LongLength, loaded.TotalSize);
            Assert.True(loaded.IsCompleted);
            Assert.True(loaded.PieceCount >= 4);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void TransfersTorrentDataBetweenSeederAndLeecher()
    {
        string root = CreateTempDirectory();
        IPAddress advertisedAddress = GetLocalIPv4();
        int trackerPort = GetFreePort();

        using MockTracker tracker = new MockTracker(trackerPort, advertisedAddress);
        TorrentFixture fixture = CreateFixture(root, tracker.AnnounceUrl);

        int seederPort = GetFreePort();
        int leecherPort = GetFreePort();

        Client? seeder = null;
        Client? leecher = null;

        try
        {
            seeder = new Client(seederPort, fixture.TorrentFilePath, fixture.SeedDirectory);
            leecher = new Client(leecherPort, fixture.TorrentFilePath, fixture.LeechDirectory);

            seeder.Start();
            Assert.True(WaitUntil(() => tracker.HasPeer(seederPort), TimeSpan.FromSeconds(5)), "Seeder never announced to the tracker.");

            leecher.Start();

            Assert.True(
                WaitUntil(() => leecher.Torrent.IsCompleted, TimeSpan.FromSeconds(30)),
                "Leecher did not complete the download within the timeout.");

            string leecherFilePath = Path.Combine(fixture.LeechDirectory, "payload.bin");
            Assert.True(File.Exists(leecherFilePath));
            Assert.Equal(fixture.Data, File.ReadAllBytes(leecherFilePath));
        }
        finally
        {
            leecher?.Stop();
            seeder?.Stop();
            Thread.Sleep(500);
            TryDeleteDirectory(root);
        }
    }

    private static TorrentFixture CreateFixture(string root, string announceUrl)
    {
        Directory.CreateDirectory(root);

        string seedDirectory = Path.Combine(root, "seed");
        string leechDirectory = Path.Combine(root, "leech");
        Directory.CreateDirectory(seedDirectory);
        Directory.CreateDirectory(leechDirectory);

        string filePath = Path.Combine(seedDirectory, "payload.bin");
        byte[] data = CreatePayload();
        File.WriteAllBytes(filePath, data);

        string originalCurrentDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = root;

            Torrent torrent = Torrent.Create(
                filePath,
                trackers: new List<string> { announceUrl },
                pieceSize: 32768,
                comment: "Integration test torrent");

            Torrent.SaveToFile(torrent);
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
        }

        string torrentFilePath = Path.Combine(root, "payload.bin.torrent");
        return new TorrentFixture(root, seedDirectory, leechDirectory, torrentFilePath, data);
    }

    private static byte[] CreatePayload()
    {
        byte[] data = new byte[131072 + 123];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)((i * 31 + 17) % 251);

        return data;
    }

    private static int GetFreePort()
    {
        using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IPAddress GetLocalIPv4()
    {
        return Dns.GetHostEntry(Dns.GetHostName())
            .AddressList
            .First(ip => ip.AddressFamily == AddressFamily.InterNetwork);
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            Thread.Sleep(100);
        }

        return condition();
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "BitTorrent.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Leave artifacts behind when cleanup fails so the test output can still be inspected.
        }
    }

    private sealed record TorrentFixture(
        string RootDirectory,
        string SeedDirectory,
        string LeechDirectory,
        string TorrentFilePath,
        byte[] Data);

    private sealed class MockTracker : IDisposable
    {
        private readonly object sync = new();
        private readonly Dictionary<int, IPEndPoint> peersByPort = new();
        private readonly HttpListener listener = new();
        private readonly Task processingTask;
        private readonly CancellationTokenSource cancellation = new();

        public MockTracker(int port, IPAddress advertisedAddress)
        {
            AnnounceUrl = $"http://127.0.0.1:{port}/announce/";
            AdvertisedAddress = advertisedAddress;

            listener.Prefixes.Add(AnnounceUrl);
            listener.Start();
            processingTask = Task.Run(ProcessLoopAsync);
        }

        public string AnnounceUrl { get; }
        public IPAddress AdvertisedAddress { get; }

        public bool HasPeer(int port)
        {
            lock (sync)
            {
                return peersByPort.ContainsKey(port);
            }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            listener.Close();

            try
            {
                processingTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore shutdown races from the listener task.
            }

            cancellation.Dispose();
        }

        private async Task ProcessLoopAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }

                await HandleContextAsync(context).ConfigureAwait(false);
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context)
        {
            int requesterPort = Int32.Parse(context.Request.QueryString["port"] ?? throw new InvalidOperationException("Tracker request missing port."));
            string? trackerEvent = context.Request.QueryString["event"];

            lock (sync)
            {
                if (string.Equals(trackerEvent, "stopped", StringComparison.OrdinalIgnoreCase))
                {
                    peersByPort.Remove(requesterPort);
                }
                else
                {
                    peersByPort[requesterPort] = new IPEndPoint(AdvertisedAddress, requesterPort);
                }
            }

            byte[] peerBytes;
            lock (sync)
            {
                peerBytes = peersByPort
                    .Where(x => x.Key != requesterPort)
                    .SelectMany(x => EncodeCompactPeer(x.Value))
                    .ToArray();
            }

            Dictionary<string, object> response = new Dictionary<string, object>
            {
                ["interval"] = 1L,
                ["peers"] = peerBytes
            };

            byte[] data = BEncoding.Encode(response);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = data.Length;
            await context.Response.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            context.Response.Close();
        }

        private static IEnumerable<byte> EncodeCompactPeer(IPEndPoint endPoint)
        {
            byte[] addressBytes = endPoint.Address.MapToIPv4().GetAddressBytes();
            yield return addressBytes[0];
            yield return addressBytes[1];
            yield return addressBytes[2];
            yield return addressBytes[3];
            yield return (byte)(endPoint.Port >> 8);
            yield return (byte)(endPoint.Port & 0xFF);
        }
    }
}

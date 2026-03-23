using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BitTorrent
{
    public class Client
    {
        private static readonly TimeSpan TrackerInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ProcessingInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(30);

        private static int maxLeechers = 5;
        private static int maxSeeders = 5;
        private static int maxUploadBytesPerSecond = 16384;
        private static int maxDownloadBytesPerSecond = 16384;

        private readonly Random random = new Random();
        private readonly Throttle uploadThrottle = new Throttle(maxUploadBytesPerSecond, TimeSpan.FromSeconds(1));
        private readonly Throttle downloadThrottle = new Throttle(maxDownloadBytesPerSecond, TimeSpan.FromSeconds(1));

        private bool isStopping;
        private int isProcessPeers;
        private int isProcessUploads;
        private int isProcessDownloads;
        private TcpListener? listener;

        public Client(int port, string torrentPath, string downloadPath)
        {
            Id = GenerateClientId();
            Port = port;

            Torrent = Torrent.LoadFromFile(torrentPath, downloadPath);
            Torrent.PieceVerified += HandlePieceVerified;
            Torrent.PeerListUpdated += HandlePeerListUpdated;
        }

        public int Port { get; }
        public Torrent Torrent { get; }
        public string Id { get; }

        public ConcurrentDictionary<string, Peer> Peers { get; } = new ConcurrentDictionary<string, Peer>();
        public ConcurrentDictionary<string, Peer> Seeders { get; } = new ConcurrentDictionary<string, Peer>();
        public ConcurrentDictionary<string, Peer> Leechers { get; } = new ConcurrentDictionary<string, Peer>();

        private ConcurrentQueue<DataRequest> OutgoingBlocks { get; } = new ConcurrentQueue<DataRequest>();
        private ConcurrentQueue<DataPackage> IncomingBlocks { get; } = new ConcurrentQueue<DataPackage>();

        private static IPAddress LocalIPAddress
        {
            get
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                        return ip;
                }

                throw new Exception("Local IP Address Not Found!");
            }
        }

        public void Start()
        {
            isStopping = false;
            EnablePeerConnections();

            StartBackgroundLoop(() => Torrent.UpdateTrackersAsync(TrackerEvent.Started, Id, Port).GetAwaiter().GetResult(), TrackerInterval);
            StartBackgroundLoop(ProcessPeers, ProcessingInterval);
            StartBackgroundLoop(ProcessUploads, ProcessingInterval);
            StartBackgroundLoop(ProcessDownloads, ProcessingInterval);
        }

        public void Stop()
        {
            if (isStopping)
                return;

            isStopping = true;
            DisablePeerConnections();
            Torrent.UpdateTrackersAsync(TrackerEvent.Stopped, Id, Port).GetAwaiter().GetResult();
        }

        private string GenerateClientId()
        {
            return string.Concat(Enumerable.Range(0, 20).Select(_ => random.Next(0, 10)));
        }

        private void StartBackgroundLoop(Action action, TimeSpan interval)
        {
            new Thread(() =>
            {
                // Keep each background concern isolated so tracker, peer, upload,
                // and download work can make progress independently.
                while (!isStopping)
                {
                    action();
                    Thread.Sleep(interval);
                }
            })
            {
                IsBackground = true
            }.Start();
        }

        private void HandlePeerListUpdated(object? sender, List<IPEndPoint> endPoints)
        {
            IPAddress local = LocalIPAddress;

            foreach (var endPoint in endPoints)
            {
                if (endPoint.Address.Equals(local) && endPoint.Port == Port)
                    continue;

                AddPeer(new Peer(Torrent, Id, endPoint));
            }
        }

        private void EnablePeerConnections()
        {
            listener = new TcpListener(new IPEndPoint(IPAddress.Any, Port));
            listener.Start();
            listener.BeginAcceptTcpClient(HandleNewConnection, null);
        }

        private void HandleNewConnection(IAsyncResult ar)
        {
            if (listener == null)
                return;

            TcpClient client;
            try
            {
                client = listener.EndAcceptTcpClient(ar);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            listener.BeginAcceptTcpClient(HandleNewConnection, null);
            AddPeer(new Peer(Torrent, Id, client));
        }

        private void DisablePeerConnections()
        {
            if (listener == null)
                return;

            listener.Stop();
            listener = null;

            foreach (var peer in Peers.Values)
                peer.Disconnect();
        }

        private void AddPeer(Peer peer)
        {
            peer.BlockRequested += HandleBlockRequested;
            peer.BlockCancelled += HandleBlockCancelled;
            peer.BlockReceived += HandleBlockReceived;
            peer.Disconnected += HandlePeerDisconnected;
            peer.StateChanged += HandlePeerStateChanged;

            peer.Connect();

            if (!Peers.TryAdd(peer.Key, peer))
                peer.Disconnect();
        }

        private void HandlePeerDisconnected(object? sender, EventArgs args)
        {
            if (sender is not Peer peer)
                return;

            peer.BlockRequested -= HandleBlockRequested;
            peer.BlockCancelled -= HandleBlockCancelled;
            peer.BlockReceived -= HandleBlockReceived;
            peer.Disconnected -= HandlePeerDisconnected;
            peer.StateChanged -= HandlePeerStateChanged;

            Peers.TryRemove(peer.Key, out _);
            Seeders.TryRemove(peer.Key, out _);
            Leechers.TryRemove(peer.Key, out _);
        }

        private void HandlePeerStateChanged(object? sender, EventArgs args)
        {
            ProcessPeers();
        }

        private void HandlePieceVerified(object? sender, int index)
        {
            ProcessPeers();

            foreach (var peer in Peers.Values)
            {
                if (!peer.IsHandshakeReceived || !peer.IsHandshakeSent)
                    continue;

                peer.SendHave(index);
            }
        }

        private void ProcessPeers()
        {
            // State changes can trigger this method while the periodic loop is also
            // running, so use a cheap guard instead of allowing overlapping passes.
            if (Interlocked.Exchange(ref isProcessPeers, 1) == 1)
                return;

            foreach (var peer in Peers.OrderByDescending(x => x.Value.PiecesRequiredAvailable))
            {
                if (DateTime.UtcNow > peer.Value.LastActive.Add(PeerTimeout))
                {
                    peer.Value.Disconnect();
                    continue;
                }

                if (!peer.Value.IsHandshakeSent || !peer.Value.IsHandshakeReceived)
                    continue;

                if (Torrent.IsCompleted)
                    peer.Value.SendNotInterested();
                else
                    peer.Value.SendInterested();

                if (peer.Value.IsCompleted && Torrent.IsCompleted)
                {
                    peer.Value.Disconnect();
                    continue;
                }

                peer.Value.SendKeepAlive();

                if (Torrent.IsStarted && Leechers.Count < maxLeechers)
                {
                    if (peer.Value.IsInterestedReceived && peer.Value.IsChokeSent)
                        peer.Value.SendUnchoke();
                }

                if (!Torrent.IsCompleted && Seeders.Count <= maxSeeders)
                {
                    if (!peer.Value.IsChokeReceived)
                        Seeders.TryAdd(peer.Key, peer.Value);
                }
            }

            Interlocked.Exchange(ref isProcessPeers, 0);
        }

        private void HandleBlockRequested(object? sender, DataRequest block)
        {
            OutgoingBlocks.Enqueue(block);
            ProcessUploads();
        }

        private void HandleBlockCancelled(object? sender, DataRequest block)
        {
            foreach (var item in OutgoingBlocks)
            {
                if (item.Peer != block.Peer || item.Piece != block.Piece || item.Begin != block.Begin || item.Length != block.Length)
                    continue;

                item.IsCancelled = true;
            }

            ProcessUploads();
        }

        private void ProcessUploads()
        {
            if (Interlocked.Exchange(ref isProcessUploads, 1) == 1)
                return;

            while (!uploadThrottle.IsThrottled && OutgoingBlocks.TryDequeue(out var block))
            {
                if (block.IsCancelled)
                    continue;

                if (!Torrent.IsPieceVerified[block.Piece])
                    continue;

                byte[]? data = Torrent.ReadBlock(block.Piece, block.Begin, block.Length);
                if (data == null)
                    continue;

                block.Peer.SendPiece(block.Piece, block.Begin, data);
                uploadThrottle.Add(block.Length);
                Torrent.Uploaded += block.Length;
            }

            Interlocked.Exchange(ref isProcessUploads, 0);
        }

        private void HandleBlockReceived(object? sender, DataPackage args)
        {
            IncomingBlocks.Enqueue(args);

            args.Peer.IsBlockRequested[args.Piece][args.Block] = false;

            foreach (var peer in Peers.Values)
            {
                if (!peer.IsBlockRequested[args.Piece][args.Block])
                    continue;

                peer.SendCancel(args.Piece, args.Block * Torrent.BlockSize, Torrent.BlockSize);
                peer.IsBlockRequested[args.Piece][args.Block] = false;
            }

            ProcessDownloads();
        }

        private void ProcessDownloads()
        {
            if (Interlocked.Exchange(ref isProcessDownloads, 1) == 1)
                return;

            while (IncomingBlocks.TryDequeue(out var incomingBlock))
                Torrent.WriteBlock(incomingBlock.Piece, incomingBlock.Block, incomingBlock.Data);

            if (Torrent.IsCompleted)
            {
                Interlocked.Exchange(ref isProcessDownloads, 0);
                return;
            }

            foreach (var piece in GetRankedPieces())
            {
                if (Torrent.IsPieceVerified[piece])
                    continue;

                foreach (var peer in GetRankedSeeders())
                {
                    if (!peer.IsPieceDownloaded[piece])
                        continue;

                    for (int block = 0; block < Torrent.GetBlockCount(piece); block++)
                    {
                        if (downloadThrottle.IsThrottled)
                            continue;

                        if (Torrent.IsBlockAcquired[piece][block])
                            continue;

                        if (peer.BlocksRequested > 0)
                            continue;

                        // Only one peer should own a given block request at a time.
                        if (Peers.Count(x => x.Value.IsBlockRequested[piece][block]) > 0)
                            continue;

                        int size = Torrent.GetBlockSize(piece, block);
                        peer.SendRequest(piece, block * Torrent.BlockSize, size);
                        downloadThrottle.Add(size);
                        peer.IsBlockRequested[piece][block] = true;
                    }
                }
            }

            Interlocked.Exchange(ref isProcessDownloads, 0);
        }

        private Peer[] GetRankedSeeders()
        {
            return Seeders.Values.OrderBy(_ => random.Next(0, 100)).ToArray();
        }

        private int[] GetRankedPieces()
        {
            var indexes = Enumerable.Range(0, Torrent.PieceCount).ToArray();
            var scores = indexes.Select(GetPieceScore).ToArray();

            Array.Sort(scores, indexes);
            Array.Reverse(indexes);

            return indexes;
        }

        private double GetPieceScore(int piece)
        {
            double progress = GetPieceProgress(piece);
            double rarity = GetPieceRarity(piece);

            // Prefer pieces that are both rare and already in progress, with a tiny
            // random offset so equally scored pieces do not always tie the same way.
            if (progress == 1.0)
                progress = 0;

            double rand = random.Next(0, 100) / 1000.0;
            return progress + rarity + rand;
        }

        private double GetPieceProgress(int index)
        {
            return Torrent.IsBlockAcquired[index].Average(x => x ? 1.0 : 0.0);
        }

        private double GetPieceRarity(int index)
        {
            if (Peers.Count < 1)
                return 0.0;

            return Peers.Average(x => x.Value.IsPieceDownloaded[index] ? 0.0 : 1.0);
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BitTorrent
{
    /// <summary>
    /// Coordinates trackers, peers, uploads, downloads, and overall torrent session state.
    /// </summary>
    public class Client
    {
        private static readonly TimeSpan TrackerInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ProcessingInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(30);

        private static int maxLeechers = 5;
        private static int maxSeeders = 5;
        private static int maxUploadBytesPerSecond = 16384; // 16 KB/s
        private static int maxDownloadBytesPerSecond = 16384; // 16 KB/s

        private readonly Random random = new Random();
        private readonly Throttle uploadThrottle = new Throttle(maxUploadBytesPerSecond, TimeSpan.FromSeconds(1));
        private readonly Throttle downloadThrottle = new Throttle(maxDownloadBytesPerSecond, TimeSpan.FromSeconds(1));

        private bool isStopping;
        private int isProcessPeers;
        private int isProcessUploads;
        private int isProcessDownloads;
        private TcpListener? listener;

        /// <summary>
        /// Creates a torrent client bound to a listening port and torrent metadata file.
        /// </summary>
        /// <param name="port">The port to listen on for inbound peer connections.</param>
        /// <param name="torrentPath">The path to the <c>.torrent</c> metadata file.</param>
        /// <param name="downloadPath">The directory that contains or will contain the payload.</param>
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

        /// <summary>
        /// Starts listening for peers and launches the background tracker, peer, upload, and download loops.
        /// </summary>
        public void Start()
        {
            isStopping = false;
            EnablePeerConnections();

            StartBackgroundLoop(() => Torrent.UpdateTrackersAsync(TrackerEvent.Started, Id, Port).GetAwaiter().GetResult(), TrackerInterval);
            StartBackgroundLoop(ProcessPeers, ProcessingInterval);
            StartBackgroundLoop(ProcessUploads, ProcessingInterval);
            StartBackgroundLoop(ProcessDownloads, ProcessingInterval);
        }

        /// <summary>
        /// Stops listening, disconnects peers, and sends a stopped announce to trackers.
        /// </summary>
        public void Stop()
        {
            if (isStopping)
                return;

            isStopping = true;
            DisablePeerConnections();
            Torrent.UpdateTrackersAsync(TrackerEvent.Stopped, Id, Port).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Generates the 20-character numeric peer id used in tracker announces and handshakes.
        /// </summary>
        /// <returns>The generated peer id.</returns>
        private string GenerateClientId()
        {
            return string.Concat(Enumerable.Range(0, 20).Select(_ => random.Next(0, 10)));
        }

        /// <summary>
        /// Runs the supplied action repeatedly on its own background thread until the client stops.
        /// </summary>
        /// <param name="action">The work to perform each iteration.</param>
        /// <param name="interval">The delay between iterations.</param>
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

        /// <summary>
        /// Adds tracker-discovered peers while filtering out the local client endpoint.
        /// </summary>
        /// <param name="sender">The torrent that raised the peer update.</param>
        /// <param name="endPoints">The remote peers returned by a tracker.</param>
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

        /// <summary>
        /// Opens the listening socket and begins accepting inbound peer connections.
        /// </summary>
        private void EnablePeerConnections()
        {
            listener = new TcpListener(new IPEndPoint(IPAddress.Any, Port));
            listener.Start();
            listener.BeginAcceptTcpClient(HandleNewConnection, null);
        }

        /// <summary>
        /// Accepts a newly connected inbound peer and starts waiting for the next one.
        /// </summary>
        /// <param name="ar">The async accept result.</param>
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

        /// <summary>
        /// Stops listening for new peers and disconnects any active peer connections.
        /// </summary>
        private void DisablePeerConnections()
        {
            if (listener == null)
                return;

            listener.Stop();
            listener = null;

            foreach (var peer in Peers.Values)
                peer.Disconnect();
        }

        /// <summary>
        /// Wires peer events, initiates the connection, and stores the peer if its endpoint is unique.
        /// </summary>
        /// <param name="peer">The peer to add to the session.</param>
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

        /// <summary>
        /// Removes a disconnected peer from all tracking collections and unsubscribes its events.
        /// </summary>
        /// <param name="sender">The peer that disconnected.</param>
        /// <param name="args">Unused event data.</param>
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

        /// <summary>
        /// Re-evaluates peer state immediately after a protocol state transition.
        /// </summary>
        /// <param name="sender">The peer whose state changed.</param>
        /// <param name="args">Unused event data.</param>
        private void HandlePeerStateChanged(object? sender, EventArgs args)
        {
            ProcessPeers();
        }

        /// <summary>
        /// Broadcasts a newly verified piece to connected peers and refreshes peer interest and choke decisions.
        /// </summary>
        /// <param name="sender">The torrent that verified the piece.</param>
        /// <param name="index">The verified piece index.</param>
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

        /// <summary>
        /// Applies high-level peer policy such as timeouts, interest, keep-alives, and seeder/leecher selection.
        /// </summary>
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

        /// <summary>
        /// Queues an inbound block request so the upload loop can serve it.
        /// </summary>
        /// <param name="sender">The peer that requested the block.</param>
        /// <param name="block">The requested block details.</param>
        private void HandleBlockRequested(object? sender, DataRequest block)
        {
            OutgoingBlocks.Enqueue(block);
            ProcessUploads();
        }

        /// <summary>
        /// Marks matching queued upload requests as cancelled so they are skipped when processed.
        /// </summary>
        /// <param name="sender">The peer that cancelled the request.</param>
        /// <param name="block">The cancelled block details.</param>
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

        /// <summary>
        /// Serves queued upload requests while respecting throttling and local verification state.
        /// </summary>
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

        /// <summary>
        /// Queues a received block, clears request ownership, and cancels duplicate in-flight requests to other peers.
        /// </summary>
        /// <param name="sender">The peer that delivered the block.</param>
        /// <param name="args">The received block data.</param>
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

        /// <summary>
        /// Flushes received blocks to disk and issues new download requests based on piece and peer ranking.
        /// </summary>
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

                        if (peer.BlocksRequested > 0) // only request one block at a time from each peer to keep pieces flowing in from multiple peers instead of saturating on a single peer
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

        /// <summary>
        /// Returns the current seeders in randomized order to spread block requests across peers.
        /// </summary>
        /// <returns>The ranked seeders for the next download pass.</returns>
        private Peer[] GetRankedSeeders()
        {
            return Seeders.Values.OrderBy(_ => random.Next(0, 100)).ToArray();
        }

        /// <summary>
        /// Returns piece indexes sorted by the client's desirability heuristic.
        /// </summary>
        /// <returns>The ranked piece indexes.</returns>
        private int[] GetRankedPieces()
        {
            var indexes = Enumerable.Range(0, Torrent.PieceCount).ToArray();
            var scores = indexes.Select(GetPieceScore).ToArray();

            Array.Sort(scores, indexes);
            Array.Reverse(indexes);

            return indexes;
        }

        /// <summary>
        /// Calculates the desirability score for a piece using progress, rarity, and a small random tie-breaker.
        /// </summary>
        /// <param name="piece">The piece index to score.</param>
        /// <returns>The ranking score for the piece.</returns>
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

        /// <summary>
        /// Returns how much of a piece has already been acquired locally.
        /// </summary>
        /// <param name="index">The piece index to inspect.</param>
        /// <returns>A ratio between 0 and 1 representing completion progress.</returns>
        private double GetPieceProgress(int index)
        {
            return Torrent.IsBlockAcquired[index].Average(x => x ? 1.0 : 0.0);
        }

        /// <summary>
        /// Returns how scarce a piece is across the currently known peers.
        /// </summary>
        /// <param name="index">The piece index to inspect.</param>
        /// <returns>A ratio where higher values mean fewer peers advertise the piece.</returns>
        private double GetPieceRarity(int index)
        {
            if (Peers.Count < 1)
                return 0.0;

            return Peers.Average(x => x.Value.IsPieceDownloaded[index] ? 0.0 : 1.0);
        }
    }
}

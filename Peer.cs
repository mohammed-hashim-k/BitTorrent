using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.IO;

namespace BitTorrent
{
    /// <summary>
    /// Represents a queued upload request from a remote peer.
    /// </summary>
    public class DataRequest
    {
        public Peer Peer { get; init; } = null!;
        public int Piece { get; init; }
        public int Begin { get; init; }
        public int Length { get; init; }
        public bool IsCancelled { get; set; }
    }

    /// <summary>
    /// Represents a downloaded block received from a remote peer.
    /// </summary>
    public class DataPackage
    {
        public Peer Peer { get; init; } = null!;
        public int Piece { get; init; }
        public int Block { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Identifies the supported BitTorrent wire message types.
    /// </summary>
    public enum MessageType : int
    {
        Unknown = -3,
        Handshake = -2,
        KeepAlive = -1,
        Choke = 0,
        Unchoke = 1,
        Interested = 2,
        NotInterested = 3,
        Have = 4,
        Bitfield = 5,
        Request = 6,
        Piece = 7,
        Cancel = 8,
        Port = 9,
    }

    /// <summary>
    /// Implements the BitTorrent wire protocol state machine for a single remote peer.
    /// </summary>
    public class Peer
    {
        public event EventHandler? Disconnected;
        public event EventHandler? StateChanged;
        public event EventHandler<DataRequest>? BlockRequested;
        public event EventHandler<DataRequest>? BlockCancelled;
        public event EventHandler<DataPackage>? BlockReceived;

        public string LocalId { get; set; }
        public string Id { get; set; } = string.Empty;

        public Torrent Torrent { get; private set; }

        public IPEndPoint IPEndPoint { get; private set; } = null!;
        public string Key { get { return IPEndPoint.ToString(); } }

        private TcpClient? TcpClient { get; set; }
        private NetworkStream? stream { get; set; }
        private const int bufferSize = 256;
        private readonly byte[] streamBuffer = new byte[bufferSize];
        private readonly List<byte> data = new List<byte>();

        public bool[] IsPieceDownloaded = Array.Empty<bool>();
        public string PiecesDownloaded { get { return String.Join("", IsPieceDownloaded.Select(x => Convert.ToInt32(x))); } }
        public int PiecesRequiredAvailable { get { return IsPieceDownloaded.Select((x, i) => x && !Torrent.IsPieceVerified[i]).Count(x => x); } }
        public int PiecesDownloadedCount { get { return IsPieceDownloaded.Count(x => x); } }
        public bool IsCompleted { get { return PiecesDownloadedCount == Torrent.PieceCount; } }
        public override string ToString() { return Key; }

        public bool IsDisconnected;

        public bool IsHandshakeSent;
        public bool IsChokeSent = true;
        public bool IsInterestedSent = false;

        public bool IsHandshakeReceived;
        public bool IsChokeReceived = true;
        public bool IsInterestedReceived = false;

        public bool[][] IsBlockRequested = Array.Empty<bool[]>();
        public int BlocksRequested { get { return IsBlockRequested.Sum(x => x.Count(y => y)); } }

        public DateTime LastActive;
        public DateTime LastKeepAlive = DateTime.MinValue;

        public long Uploaded;
        public long Downloaded;

        #region Constructors

        /// <summary>
        /// Creates a peer wrapper for an already-accepted inbound TCP connection.
        /// </summary>
        /// <param name="torrent">The torrent being shared with the peer.</param>
        /// <param name="localId">The local peer identifier.</param>
        /// <param name="client">The connected TCP client.</param>
        public Peer(Torrent torrent, string localId, TcpClient client) : this(torrent, localId)
        {
            TcpClient = client;
            IPEndPoint = client.Client.RemoteEndPoint as IPEndPoint
                ?? throw new InvalidOperationException("Peer connection must have a remote endpoint.");
        }

        /// <summary>
        /// Creates a peer wrapper for an outbound connection to a known endpoint.
        /// </summary>
        /// <param name="torrent">The torrent being shared with the peer.</param>
        /// <param name="localId">The local peer identifier.</param>
        /// <param name="endPoint">The remote endpoint to connect to.</param>
        public Peer(Torrent torrent, string localId, IPEndPoint endPoint) : this(torrent, localId)
        {
            IPEndPoint = endPoint;
        }

        /// <summary>
        /// Initializes shared per-peer state used by both inbound and outbound connection flows.
        /// </summary>
        /// <param name="torrent">The torrent being shared with the peer.</param>
        /// <param name="localId">The local peer identifier.</param>
        private Peer(Torrent torrent, string localId)
        {
            LocalId = localId;
            Torrent = torrent;

            LastActive = DateTime.UtcNow;
            IsPieceDownloaded = new bool[Torrent.PieceCount];
            IsBlockRequested = new bool[Torrent.PieceCount][];
            for (int i = 0; i < Torrent.PieceCount; i++)
                IsBlockRequested[i] = new bool[Torrent.GetBlockCount(i)];
        }
        #endregion

        #region Methods
        /// <summary>
        /// Reads a 32-bit big-endian integer from a byte array.
        /// </summary>
        /// <param name="bytes">The source bytes.</param>
        /// <param name="offset">The offset where the integer begins.</param>
        /// <returns>The decoded integer value.</returns>
        private static int ReadInt32BigEndian(byte[] bytes, int offset = 0)
        {
            return BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, sizeof(int)));
        }

        /// <summary>
        /// Encodes a 32-bit integer using big-endian byte order.
        /// </summary>
        /// <param name="value">The value to encode.</param>
        /// <returns>The encoded bytes.</returns>
        private static byte[] WriteInt32BigEndian(int value)
        {
            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }

        /// <summary>
        /// Opens or adopts the socket connection, sends the initial handshake, and starts the async read loop.
        /// </summary>
        public void Connect()
        {
            if (TcpClient == null)
            {
                TcpClient = new TcpClient();
                try
                {
                    TcpClient.Connect(IPEndPoint);
                }
                catch (SocketException)
                {
                    Disconnect();
                    return;
                }
            }
            Log.Info(this, "connected");

            stream = TcpClient.GetStream();
            SendHandshake();
            stream.BeginRead(streamBuffer, 0, Peer.bufferSize, new AsyncCallback(HandleRead), null);
        }

        /// <summary>
        /// Closes the peer connection and raises the disconnection event once.
        /// </summary>
        public void Disconnect()
        {
            if (IsDisconnected)
                return;

            IsDisconnected = true;
            Log.Info(this, "disconnected, down " + Downloaded + ", up " + Uploaded);

            stream?.Dispose();
            TcpClient?.Close();
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Writes raw bytes to the peer stream, disconnecting on any transport failure.
        /// </summary>
        /// <param name="bytes">The protocol bytes to send.</param>
        private void SendBytes(byte[] bytes)
        {
            if (IsDisconnected || stream == null)
                return;

            try
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception)
            {
                Disconnect();
            }
        }

        /// <summary>
        /// Handles an async socket read, buffers incoming data, and dispatches complete messages.
        /// </summary>
        /// <param name="ar">The async read result.</param>
        private void HandleRead(IAsyncResult ar)
        {
            if (stream == null)
                return;

            int bytes = 0;
            try
            {
                bytes = stream.EndRead(ar);
            }
            catch (Exception)
            {
                Disconnect();
                return;
            }

            if (bytes == 0)
            {
                Disconnect();
                return;
            }

            data.AddRange(streamBuffer.Take(bytes));

            // One socket read may contain partial data or multiple protocol messages,
            // so unread bytes stay buffered until a full message is available.
            int messageLength = GetMessageLength(data);
            while (data.Count >= messageLength)
            {
                HandleMessage(data.Take(messageLength).ToArray());
                data.RemoveRange(0, messageLength);

                messageLength = GetMessageLength(data);
            }

            try
            {
                stream.BeginRead(streamBuffer, 0, Peer.bufferSize, new AsyncCallback(HandleRead), null);
            }
            catch (Exception)
            {
                Disconnect();
            }
        }

        /// <summary>
        /// Determines how many bytes are needed before the next complete message can be processed.
        /// </summary>
        /// <param name="data">The currently buffered unread bytes.</param>
        /// <returns>The expected message length in bytes.</returns>
        private int GetMessageLength(List<byte> data)
        {
            // The handshake is the only fixed-size message; everything after that is length-prefixed.
            if (!IsHandshakeReceived)
                return 68;

            if (data.Count < 4)
                return int.MaxValue;

            return ReadInt32BigEndian(data.Take(4).ToArray()) + 4; 
        }

        /// <summary>
        /// Parses a handshake message and extracts the advertised info hash and peer id.
        /// </summary>
        /// <param name="bytes">The raw handshake bytes.</param>
        /// <param name="hash">When this method returns, contains the info hash from the handshake.</param>
        /// <param name="id">When this method returns, contains the remote peer id.</param>
        /// <returns><see langword="true"/> if the handshake is valid; otherwise, <see langword="false"/>.</returns>
        public static bool DecodeHandshake(byte[] bytes, out byte[] hash, out string id)
        {
            hash = new byte[20];
            id = "";

            if (bytes.Length != 68 || bytes[0] != 19)
            {
                Log.Error("invalid handshake, must be of length 68 and first byte must equal 19");
                return false;
            }

            if (Encoding.UTF8.GetString(bytes.Skip(1).Take(19).ToArray()) != "BitTorrent protocol")
            {
                Log.Error("invalid handshake, protocol must equal \"BitTorrent protocol\"");
                return false;
            }

            // flags
            //byte[] flags = bytes.Skip(20).Take(8).ToArray();

            hash = bytes.Skip(28).Take(20).ToArray();

            id = Encoding.UTF8.GetString(bytes.Skip(48).Take(20).ToArray());

            return true;
        }

        /// <summary>
        /// Builds a BitTorrent handshake message for the given torrent and local peer id.
        /// </summary>
        /// <param name="hash">The torrent info hash.</param>
        /// <param name="id">The local peer id.</param>
        /// <returns>The encoded handshake message.</returns>
        public static byte[] EncodeHandshake(byte[] hash, string id)
        {
            byte[] message = new byte[68];
            message[0] = 19;
            Buffer.BlockCopy(Encoding.UTF8.GetBytes("BitTorrent protocol"), 0, message, 1, 19);
            Buffer.BlockCopy(hash, 0, message, 28, 20);
            Buffer.BlockCopy(Encoding.UTF8.GetBytes(id), 0, message, 48, 20);

            return message;
        }

        /// <summary>
        /// Validates that a message is a correctly formed keep-alive frame.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <returns><see langword="true"/> if the message is valid; otherwise, <see langword="false"/>.</returns>
        public static bool DecodeKeepAlive(byte[] bytes)
        {
            if (bytes.Length != 4 || ReadInt32BigEndian(bytes) != 0)
            {
                Log.Error("invalid keep alive");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Builds a keep-alive frame.
        /// </summary>
        /// <returns>The encoded keep-alive bytes.</returns>
        public static byte[] EncodeKeepAlive()
        {
            return WriteInt32BigEndian(0);
        }

        /// <summary>
        /// Validates a choke message.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <returns><see langword="true"/> if the message is valid.</returns>
        public static bool DecodeChoke(byte[] bytes)
        {
            return DecodeState(bytes, MessageType.Choke);
        }

        /// <summary>
        /// Validates an unchoke message.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <returns><see langword="true"/> if the message is valid.</returns>
        public static bool DecodeUnchoke(byte[] bytes)
        {
            return DecodeState(bytes, MessageType.Unchoke);
        }

        /// <summary>
        /// Validates an interested message.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <returns><see langword="true"/> if the message is valid.</returns>
        public static bool DecodeInterested(byte[] bytes)
        {
            return DecodeState(bytes, MessageType.Interested);
        }

        /// <summary>
        /// Validates a not-interested message.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <returns><see langword="true"/> if the message is valid.</returns>
        public static bool DecodeNotInterested(byte[] bytes)
        {
            return DecodeState(bytes, MessageType.NotInterested);
        }

        /// <summary>
        /// Validates one of the one-byte state-change messages that share a common frame shape.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <param name="type">The expected message type.</param>
        /// <returns><see langword="true"/> if the frame matches the expected type.</returns>
        public static bool DecodeState(byte[] bytes, MessageType type)
        {
            if (bytes.Length != 5 || ReadInt32BigEndian(bytes) != 1 || bytes[4] != (byte)type)
            {
                Log.Debug("invalid " + Enum.GetName(typeof(MessageType), type));
                return false;
            }
            return true;
        }

        /// <summary>
        /// Builds a choke message.
        /// </summary>
        /// <returns>The encoded choke frame.</returns>
        public static byte[] EncodeChoke()
        {
            return EncodeState(MessageType.Choke);
        }

        /// <summary>
        /// Builds an unchoke message.
        /// </summary>
        /// <returns>The encoded unchoke frame.</returns>
        public static byte[] EncodeUnchoke()
        {
            return EncodeState(MessageType.Unchoke);
        }

        /// <summary>
        /// Builds an interested message.
        /// </summary>
        /// <returns>The encoded interested frame.</returns>
        public static byte[] EncodeInterested()
        {
            return EncodeState(MessageType.Interested);
        }

        /// <summary>
        /// Builds a not-interested message.
        /// </summary>
        /// <returns>The encoded not-interested frame.</returns>
        public static byte[] EncodeNotInterested()
        {
            return EncodeState(MessageType.NotInterested);
        }

        /// <summary>
        /// Builds a one-byte state-change message.
        /// </summary>
        /// <param name="type">The message type to encode.</param>
        /// <returns>The encoded frame.</returns>
        public static byte[] EncodeState(MessageType type)
        {
            byte[] message = new byte[5];
            Buffer.BlockCopy(WriteInt32BigEndian(1), 0, message, 0, 4);
            message[4] = (byte)type;
            return message;
        }


        /// <summary>
        /// Parses a have message and extracts the piece index being advertised.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <param name="index">When this method returns, contains the advertised piece index.</param>
        /// <returns><see langword="true"/> if the message is valid.</returns>
        public static bool DecodeHave(byte[] bytes, out int index)
        {
            index = -1;

            if (bytes.Length != 9 || ReadInt32BigEndian(bytes) != 5)
            {
                Log.Error("invalid have, first byte must equal 0x2");
                return false;
            }

            index = ReadInt32BigEndian(bytes, 5);

            return true;
        }

        /// <summary>
        /// Parses a bitfield message into a per-piece availability map.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <param name="pieces">The total number of pieces in the torrent.</param>
        /// <param name="isPieceDownloaded">When this method returns, contains the advertised piece availability.</param>
        /// <returns><see langword="true"/> if the message is valid.</returns>
        public static bool DecodeBitfield(byte[] bytes, int pieces, out bool[] isPieceDownloaded)
        {
            isPieceDownloaded = new bool[pieces];

            int expectedLength = Convert.ToInt32(Math.Ceiling(pieces / 8.0)) + 1; 

            if (bytes.Length != expectedLength + 4 || ReadInt32BigEndian(bytes) != expectedLength)
            {
                Log.Error("invalid bitfield, first byte must equal " + expectedLength);
                return false;
            }

            // BitTorrent encodes piece availability with the most significant bit first.
            BitArray bitfield = new BitArray(bytes.Skip(5).ToArray());

            for (int i = 0; i < pieces; i++)
                isPieceDownloaded[i] = bitfield[bitfield.Length - 1 - i];

            return true;
        }

        /// <summary>
        /// Builds a have message for a verified piece.
        /// </summary>
        /// <param name="index">The piece index being advertised.</param>
        /// <returns>The encoded have frame.</returns>
        public static byte[] EncodeHave(int index)
        {
            byte[] message = new byte[9];
            Buffer.BlockCopy(WriteInt32BigEndian(5), 0, message, 0, 4);
            message[4] = (byte)MessageType.Have;
            Buffer.BlockCopy(WriteInt32BigEndian(index), 0, message, 5, 4);

            return message;
        }

        /// <summary>
        /// Builds a bitfield message describing which pieces are already available locally.
        /// </summary>
        /// <param name="isPieceDownloaded">The local per-piece availability map.</param>
        /// <returns>The encoded bitfield frame.</returns>
        public static byte[] EncodeBitfield(bool[] isPieceDownloaded)
        {
            int numPieces = isPieceDownloaded.Length;
            int numBytes = Convert.ToInt32(Math.Ceiling(numPieces / 8.0));
            int numBits = numBytes * 8;

            int length = numBytes + 1; // +1 for the message ID byte

            byte[] message = new byte[length + 4];
            Buffer.BlockCopy(WriteInt32BigEndian(length), 0, message, 0, 4);
            message[4] = (byte)MessageType.Bitfield;

            bool[] downloaded = new bool[numBits];
            for (int i = 0; i < numPieces; i++)
                downloaded[i] = isPieceDownloaded[i];

            BitArray bitfield = new BitArray(downloaded);
            BitArray reversed = new BitArray(numBits);
            for (int i = 0; i < numBits; i++)
                reversed[i] = bitfield[numBits - i - 1];

            reversed.CopyTo(message, 5);

            return message;
        }

        /// <summary>
        /// Parses a block request message.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <param name="index">When this method returns, contains the requested piece index.</param>
        /// <param name="begin">When this method returns, contains the block offset within the piece.</param>
        /// <param name="length">When this method returns, contains the requested byte count.</param>
        /// <returns><see langword="true"/> if the message is valid.</returns>
        public static bool DecodeRequest(byte[] bytes, out int index, out int begin, out int length)
        {
            index = -1;
            begin = -1;
            length = -1;

            if (bytes.Length != 17 || ReadInt32BigEndian(bytes) != 13)
            {
                Log.Error("invalid request message, must be of length 17");
                return false;
            }

            index = ReadInt32BigEndian(bytes, 5);
            begin = ReadInt32BigEndian(bytes, 9);
            length = ReadInt32BigEndian(bytes, 13);

            return true;
        }

        /// <summary>
        /// Parses a piece message containing block payload bytes.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <param name="index">When this method returns, contains the piece index.</param>
        /// <param name="begin">When this method returns, contains the block offset within the piece.</param>
        /// <param name="data">When this method returns, contains the block payload.</param>
        /// <returns><see langword="true"/> if the message is valid.</returns>
        public static bool DecodePiece(byte[] bytes, out int index, out int begin, out byte[] data)
        {
            index = -1;
            begin = -1;
            data = new byte[0];

            if (bytes.Length < 13)
            {
                Log.Error("invalid piece message");
                return false;
            }

            int length = ReadInt32BigEndian(bytes) - 9;
            index = ReadInt32BigEndian(bytes, 5);
            begin = ReadInt32BigEndian(bytes, 9);

            data = new byte[length];
            Buffer.BlockCopy(bytes, 13, data, 0, length);

            return true;
        }

        /// <summary>
        /// Parses a cancel message for a previously requested block.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        /// <param name="index">When this method returns, contains the piece index.</param>
        /// <param name="begin">When this method returns, contains the block offset within the piece.</param>
        /// <param name="length">When this method returns, contains the block length.</param>
        /// <returns><see langword="true"/> if the message is valid.</returns>
        public static bool DecodeCancel(byte[] bytes, out int index, out int begin, out int length)
        {
            index = -1;
            begin = -1;
            length = -1;

            if (bytes.Length != 17 || ReadInt32BigEndian(bytes) != 13)
            {
                Log.Error("invalid cancel message, must be of length 17");
                return false;
            }

            index = ReadInt32BigEndian(bytes, 5);
            begin = ReadInt32BigEndian(bytes, 9);
            length = ReadInt32BigEndian(bytes, 13);

            return true;
        }

        /// <summary>
        /// Builds a block request message.
        /// </summary>
        /// <param name="index">The piece index to request from.</param>
        /// <param name="begin">The byte offset within the piece.</param>
        /// <param name="length">The number of bytes requested.</param>
        /// <returns>The encoded request frame.</returns>
        public static byte[] EncodeRequest(int index, int begin, int length)
        {
            byte[] message = new byte[17];
            Buffer.BlockCopy(WriteInt32BigEndian(13), 0, message, 0, 4);
            message[4] = (byte)MessageType.Request;
            Buffer.BlockCopy(WriteInt32BigEndian(index), 0, message, 5, 4);
            Buffer.BlockCopy(WriteInt32BigEndian(begin), 0, message, 9, 4);
            Buffer.BlockCopy(WriteInt32BigEndian(length), 0, message, 13, 4);

            return message;
        }

        /// <summary>
        /// Builds a piece message containing a block payload.
        /// </summary>
        /// <param name="index">The piece index being sent.</param>
        /// <param name="begin">The byte offset within the piece.</param>
        /// <param name="data">The block payload.</param>
        /// <returns>The encoded piece frame.</returns>
        public static byte[] EncodePiece(int index, int begin, byte[] data)
        {
            int length = data.Length + 9;

            byte[] message = new byte[length + 4];
            Buffer.BlockCopy(WriteInt32BigEndian(length), 0, message, 0, 4);
            message[4] = (byte)MessageType.Piece;
            Buffer.BlockCopy(WriteInt32BigEndian(index), 0, message, 5, 4);
            Buffer.BlockCopy(WriteInt32BigEndian(begin), 0, message, 9, 4);
            Buffer.BlockCopy(data, 0, message, 13, data.Length);

            return message;
        }

        /// <summary>
        /// Builds a cancel message for a previously requested block.
        /// </summary>
        /// <param name="index">The piece index.</param>
        /// <param name="begin">The byte offset within the piece.</param>
        /// <param name="length">The number of bytes being cancelled.</param>
        /// <returns>The encoded cancel frame.</returns>
        public static byte[] EncodeCancel(int index, int begin, int length)
        {
            byte[] message = new byte[17];
            Buffer.BlockCopy(WriteInt32BigEndian(13), 0, message, 0, 4);
            message[4] = (byte)MessageType.Cancel;
            Buffer.BlockCopy(WriteInt32BigEndian(index), 0, message, 5, 4);
            Buffer.BlockCopy(WriteInt32BigEndian(begin), 0, message, 9, 4);
            Buffer.BlockCopy(WriteInt32BigEndian(length), 0, message, 13, 4);

            return message;
        }

        /// <summary>
        /// Sends the initial BitTorrent handshake if it has not already been sent.
        /// </summary>
        private void SendHandshake()
        {
            if (IsHandshakeSent)
                return;

            Log.Debug(this, "-> handshake");
            SendBytes(EncodeHandshake(Torrent.Infohash, LocalId));
            IsHandshakeSent = true;
        }

        /// <summary>
        /// Sends a keep-alive if enough time has passed since the last one.
        /// </summary>
        public void SendKeepAlive()
        {
            if (LastKeepAlive > DateTime.UtcNow.AddSeconds(-30))
                return;

            Log.Debug(this, "-> keep alive");
            SendBytes(EncodeKeepAlive());
            LastKeepAlive = DateTime.UtcNow;
        }

        /// <summary>
        /// Sends a choke message if the peer is not already choked.
        /// </summary>
        public void SendChoke()
        {
            if (IsChokeSent)
                return;

            Log.Debug(this, "-> choke");
            SendBytes(EncodeChoke());
            IsChokeSent = true;
        }

        /// <summary>
        /// Sends an unchoke message if the peer is currently choked.
        /// </summary>
        public void SendUnchoke()
        {
            if (!IsChokeSent)
                return;

            Log.Debug(this, "-> unchoke");
            SendBytes(EncodeUnchoke());
            IsChokeSent = false;
        }

        /// <summary>
        /// Sends an interested message if one has not already been sent.
        /// </summary>
        public void SendInterested()
        {
            if (IsInterestedSent)
                return;

            Log.Debug(this, "-> interested");
            SendBytes(EncodeInterested());
            IsInterestedSent = true;
        }

        /// <summary>
        /// Sends a not-interested message if we previously advertised interest.
        /// </summary>
        public void SendNotInterested()
        {
            if (!IsInterestedSent)
                return;

            Log.Debug(this, "-> not interested");
            SendBytes(EncodeNotInterested());
            IsInterestedSent = false;
        }

        /// <summary>
        /// Notifies the peer that a piece has been verified locally.
        /// </summary>
        /// <param name="index">The piece index that became available.</param>
        public void SendHave(int index)
        {
            Log.Debug(this, "-> have " + index);
            SendBytes(EncodeHave(index));
        }

        /// <summary>
        /// Sends the current local piece availability bitfield.
        /// </summary>
        /// <param name="isPieceDownloaded">The local per-piece availability map.</param>
        public void SendBitfield(bool[] isPieceDownloaded)
        {
            Log.Debug(this, "-> bitfield " + String.Join("", isPieceDownloaded.Select(x => x ? 1 : 0)));
            SendBytes(EncodeBitfield(isPieceDownloaded));
        }

        /// <summary>
        /// Requests a block from the remote peer.
        /// </summary>
        /// <param name="index">The piece index to request from.</param>
        /// <param name="begin">The byte offset within the piece.</param>
        /// <param name="length">The number of bytes requested.</param>
        public void SendRequest(int index, int begin, int length)
        {
            Log.Debug(this, "-> request " + index + ", " + begin + ", " + length);
            SendBytes(EncodeRequest(index, begin, length));
        }

        /// <summary>
        /// Sends a block payload to the remote peer and updates upload counters.
        /// </summary>
        /// <param name="index">The piece index being sent.</param>
        /// <param name="begin">The byte offset within the piece.</param>
        /// <param name="data">The block payload.</param>
        public void SendPiece(int index, int begin, byte[] data)
        {
            Log.Debug(this, "-> piece " + index + ", " + begin + ", " + data.Length);
            SendBytes(EncodePiece(index, begin, data));
            Uploaded += data.Length;
        }

        /// <summary>
        /// Cancels a previously requested block.
        /// </summary>
        /// <param name="index">The piece index.</param>
        /// <param name="begin">The byte offset within the piece.</param>
        /// <param name="length">The number of bytes being cancelled.</param>
        public void SendCancel(int index, int begin, int length)
        {
            Log.Debug(this, "-> cancel");
            SendBytes(EncodeCancel(index, begin, length));
        }

        /// <summary>
        /// Determines the protocol message type represented by a fully buffered frame.
        /// </summary>
        /// <param name="bytes">The raw frame bytes.</param>
        /// <returns>The detected message type.</returns>
        private MessageType GetMessageType(byte[] bytes)
        {
            if (!IsHandshakeReceived)
                return MessageType.Handshake;

            if (bytes.Length == 4 && ReadInt32BigEndian(bytes) == 0)
                return MessageType.KeepAlive;

            if (bytes.Length > 4 && Enum.IsDefined(typeof(MessageType), (int)bytes[4]))
                return (MessageType)bytes[4];

            return MessageType.Unknown;
        }


        /// <summary>
        /// Dispatches a fully buffered message to the corresponding protocol handler.
        /// </summary>
        /// <param name="bytes">The raw message bytes.</param>
        private void HandleMessage(byte[] bytes)
        {
            LastActive = DateTime.UtcNow;

            MessageType type = GetMessageType(bytes);

            if (type == MessageType.Unknown)
            {
                return;
            }
            else if (type == MessageType.Handshake)
            {
                byte[] hash;
                string id;
                if (DecodeHandshake(bytes, out hash, out id))
                {
                    HandleHandshake(hash, id);
                    return;
                }
            }
            else if (type == MessageType.KeepAlive && DecodeKeepAlive(bytes))
            {
                HandleKeepAlive();
                return;
            }
            else if (type == MessageType.Choke && DecodeChoke(bytes))
            {
                HandleChoke();
                return;
            }
            else if (type == MessageType.Unchoke && DecodeUnchoke(bytes))
            {
                HandleUnchoke();
                return;
            }
            else if (type == MessageType.Interested && DecodeInterested(bytes))
            {
                HandleInterested();
                return;
            }
            else if (type == MessageType.NotInterested && DecodeNotInterested(bytes))
            {
                HandleNotInterested();
                return;
            }
            else if (type == MessageType.Have)
            {
                int index;
                if (DecodeHave(bytes, out index))
                {
                    HandleHave(index);
                    return;
                }
            }
            else if (type == MessageType.Bitfield)
            {
                bool[] isPieceDownloaded;
                if (DecodeBitfield(bytes, IsPieceDownloaded.Length, out isPieceDownloaded))
                {
                    HandleBitfield(isPieceDownloaded);
                    return;
                }
            }
            else if (type == MessageType.Request)
            {
                int index;
                int begin;
                int length;
                if (DecodeRequest(bytes, out index, out begin, out length))
                {
                    HandleRequest(index, begin, length);
                    return;
                }
            }
            else if (type == MessageType.Piece)
            {
                int index;
                int begin;
                byte[] data;
                if (DecodePiece(bytes, out index, out begin, out data))
                {
                    HandlePiece(index, begin, data);
                    return;
                }
            }
            else if (type == MessageType.Cancel)
            {
                int index;
                int begin;
                int length;
                if (DecodeCancel(bytes, out index, out begin, out length))
                {
                    HandleCancel(index, begin, length);
                    return;
                }
            }
            else if (type == MessageType.Port)
            {
                Log.Debug(this, "<- port: " + String.Join("", bytes.Select(x => x.ToString("x2"))));
                return;
            }

            Log.Debug(this, "Unhandled incoming message " + String.Join("", bytes.Select(x => x.ToString("x2"))));
            Disconnect();
        }

        /// <summary>
        /// Validates the incoming handshake, records the remote peer id, and advertises local piece availability.
        /// </summary>
        /// <param name="hash">The info hash advertised by the remote peer.</param>
        /// <param name="id">The remote peer id.</param>
        private void HandleHandshake(byte[] hash, string id)
        {
            Log.Debug(this, "<- handshake");

            // Drop peers from a different swarm before advertising our state.
            if (!Torrent.Infohash.SequenceEqual(hash))
            {
                Log.Debug(this, "invalid handshake, incorrect torrent hash: expecting=" + Torrent.HexStringInfohash + ", received =" + String.Join("", hash.Select(x => x.ToString("x2"))));
                Disconnect();
                return;
            }

            Id = id;

            IsHandshakeReceived = true;
            SendBitfield(Torrent.IsPieceVerified); // immediately advertise which pieces we have available, so the peer can make informed decisions about which pieces to request
        }

        /// <summary>
        /// Records receipt of a keep-alive message.
        /// </summary>
        private void HandleKeepAlive()
        {
            Log.Debug(this, "<- keep alive");
        }

        /// <summary>
        /// Handles an incoming DHT port message.
        /// </summary>
        /// <param name="port">The announced port.</param>
        private void HandlePort(int port)
        {
            Log.Debug(this, "<- port");
        }

        /// <summary>
        /// Updates state after the remote peer chokes us.
        /// </summary>
        private void HandleChoke()
        {
            Log.Debug(this, "<- choke");
            IsChokeReceived = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Updates state after the remote peer unchokes us.
        /// </summary>
        private void HandleUnchoke()
        {
            Log.Debug(this, "<- unchoke");
            IsChokeReceived = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Updates state after the remote peer signals interest in our data.
        /// </summary>
        private void HandleInterested()
        {
            Log.Debug(this, "<- interested");
            IsInterestedReceived = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Updates state after the remote peer signals it is no longer interested.
        /// </summary>
        private void HandleNotInterested()
        {
            Log.Debug(this, "<- not interested");
            IsInterestedReceived = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Marks a single piece as available on the remote peer.
        /// </summary>
        /// <param name="index">The advertised piece index.</param>
        private void HandleHave(int index) 
        {
            IsPieceDownloaded[index] = true;
            Log.Debug(this, "<- have " + index + " - " + PiecesDownloadedCount + " available (" + PiecesDownloaded + ")");
            StateChanged?.Invoke(this, EventArgs.Empty); 
        }

        /// <summary>
        /// Merges the remote peer's advertised bitfield into the local availability map.
        /// </summary>
        /// <param name="isPieceDownloaded">The availability map decoded from the bitfield message.</param>
        private void HandleBitfield(bool[] isPieceDownloaded)
        {   
            for (int i = 0; i < Torrent.PieceCount; i++)
                IsPieceDownloaded[i] = IsPieceDownloaded[i] || isPieceDownloaded[i]; 

            Log.Debug(this, "<- bitfield " + PiecesDownloadedCount + " available (" + PiecesDownloaded + ")");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Raises an upload request event for a block requested by the remote peer.
        /// </summary>
        /// <param name="index">The requested piece index.</param>
        /// <param name="begin">The byte offset within the piece.</param>
        /// <param name="length">The requested block length.</param>
        private void HandleRequest(int index, int begin, int length)
        {
            Log.Debug(this, "<- request " + index + ", " + begin + ", " + length);

            BlockRequested?.Invoke(this, new DataRequest()
            {
                Peer = this,
                Piece = index,
                Begin = begin,
                Length = length
            });
        }

        /// <summary>
        /// Raises a download event for a received block and updates the download counter.
        /// </summary>
        /// <param name="index">The piece index containing the block.</param>
        /// <param name="begin">The byte offset within the piece.</param>
        /// <param name="data">The received block payload.</param>
        private void HandlePiece(int index, int begin, byte[] data)
        {
            Log.Debug(this, "<- piece " + index + ", " + begin + ", " + data.Length);
            Downloaded += data.Length;

            // The wire format uses a byte offset; internally we track the block index.
            BlockReceived?.Invoke(this, new DataPackage()
            {
                Peer = this,
                Piece = index,
                Block = begin / Torrent.BlockSize,
                Data = data
            });
        }

        /// <summary>
        /// Raises a cancellation event for a block the remote peer no longer wants.
        /// </summary>
        /// <param name="index">The piece index.</param>
        /// <param name="begin">The byte offset within the piece.</param>
        /// <param name="length">The cancelled block length.</param>
        private void HandleCancel(int index, int begin, int length)
        {
            Log.Debug(this, "<- cancel");

            BlockCancelled?.Invoke(this, new DataRequest()
            {
                Peer = this,
                Piece = index,
                Begin = begin,
                Length = length
            });
        }


        
        #endregion
    }


}

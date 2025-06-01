using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;


namespace BitTorrent
{
    public class FileItem
    {
        public string Path;
        public long Size;
        public long Offset;
        public string FormattedSize { get { return Torrent.BytesToString(Size); } }
    }
    public class Torrent
    {

        #region Properties
        public string Name { get; private set; }
        public bool? IsPrivate { get; private set; }
        public List<FileItem> Files { get; private set; } = new List<FileItem>();
        public string FileDirectory { get { return (Files.Count > 1 ? Name + Path.DirectorySeparatorChar : ""); } }
        public string DownloadDirectory { get; private set; }
        public List<Tracker> Trackers { get; } = new List<Tracker>();
        public string Comment { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreationDate { get; set; }
        public Encoding Encoding { get; set; }
        public int BlockSize { get; private set; }
        public int PieceSize { get; private set; }
        public long TotalSize { get { return Files.Sum(x => x.Size); } }

        public string FormattedPieceSize { get { return BytesToString(PieceSize); } }
        public string FormattedTotalSize { get { return BytesToString(TotalSize); } }

        public int PieceCount { get { return PieceHashes.Length; } }


        public byte[][] PieceHashes { get; private set; }
        public bool[] IsPieceVerified { get; private set; }
        public bool[][] IsBlockAcquired { get; private set; }

        public string VerifiedPiecesString { get { return String.Join("", IsPieceVerified.Select(x => x ? 1 : 0)); } }
        public int VerifiedPiecesCount { get { return IsPieceVerified.Count(x => x); } }
        public double VerifiedRatio { get { return VerifiedPiecesCount / (double)PieceCount; } }
        public bool IsCompleted { get { return VerifiedPiecesCount == PieceCount; } }
        public bool IsStarted { get { return VerifiedPiecesCount > 0; } }

        public long Uploaded { get; set; } = 0;
        public long Donwnloaded { get { return PieceSize * VerifiedPiecesCount; } }
        public long Left { get { return TotalSize - Donwnloaded; } }



        public byte[] Infohash { get; private set; } = new byte[20];
        public string HexStringInfohash { get { return String.Join("", this.Infohash.Select(x => x.ToString("x2"))); } }
        public string UrlSafeStringInfohash { get { return Encoding.UTF8.GetString(WebUtility.UrlEncodeToBytes(this.Infohash, 0, 20)); } }
        #endregion
        
        public int GetPieceSize(int piece)
        {
            if (piece == PieceCount - 1)
            {
                int remainder = Convert.ToInt32(TotalSize % PieceSize);
                if (remainder != 0)
                {
                    return remainder;
                }

            }
            return PieceSize;
        }

        public int GetBlockSize(int piece, int block)
        {
            if (block == GetBlockCount(piece) - 1)
            {
                int remainder = Convert.ToInt32(GetPieceSize(piece) % BlockSize);
                if (remainder != 0)
                {
                    return remainder;
                }

            }
            return BlockSize;
        }

        public int GetBlockCount(int piece)
        {
            return Convert.ToInt32(Math.Ceiling(GetPieceSize(piece) / (double)BlockSize));
        }



    }
}
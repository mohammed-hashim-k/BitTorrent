using System;
using System.Linq;
using System.Net;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using MiscUtil.Conversion;

namespace BitTorrent
{
    public class Tracker
    {
        public event EventHandler<List<IPEndPoint>> PeerListUpdated;
        public string Address { get; private set; }
        public Tracker(string address)
        {
            Address = address;
        }

    }
}

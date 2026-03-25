using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace BitTorrent
{
    /// <summary>
    /// Describes the announce lifecycle event being sent to a tracker.
    /// </summary>
    public enum TrackerEvent
    {
        Started,
        Paused,
        Stopped
    }

    /// <summary>
    /// Manages announces to a single tracker and publishes any peers returned in the response.
    /// </summary>
    public class Tracker
    {
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        public event EventHandler<List<IPEndPoint>>? PeerListUpdated;
        public string Address { get; }

        public DateTime LastPeerRequest { get; private set; } = DateTime.MinValue;
        public TimeSpan PeerRequestInterval { get; private set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Initializes a tracker wrapper for the provided announce URL.
        /// </summary>
        /// <param name="address">The tracker announce URL.</param>
        public Tracker(string address)
        {
            Address = address;
        }

        /// <summary>
        /// Announces the torrent state to the tracker unless the current request interval says to wait.
        /// </summary>
        /// <param name="torrent">The torrent whose counters and info hash should be announced.</param>
        /// <param name="ev">The announce event to send.</param>
        /// <param name="id">The local peer identifier.</param>
        /// <param name="port">The listening port for incoming peer connections.</param>
        /// <returns>A task that completes once the announce attempt finishes.</returns>
        public async Task UpdateAsync(Torrent torrent, TrackerEvent ev, string id, int port)
        {
            // The client ticks regularly, but trackers tell us how often we are allowed
            // to ask for fresh peers, so honor their interval for started announces.
            if (ev == TrackerEvent.Started && DateTime.UtcNow < LastPeerRequest.Add(PeerRequestInterval))
                return;

            LastPeerRequest = DateTime.UtcNow;

            string url = BuildAnnounceUrl(torrent, ev, id, port);
            await RequestAsync(url).ConfigureAwait(false);
        }

        /// <summary>
        /// Clears the last announce timestamp so the next update can run immediately.
        /// </summary>
        public void ResetLastRequest()
        {
            LastPeerRequest = DateTime.MinValue;
        }

        /// <summary>
        /// Builds the tracker announce URL using the torrent counters and peer identity.
        /// </summary>
        /// <param name="torrent">The torrent being announced.</param>
        /// <param name="ev">The announce event to encode.</param>
        /// <param name="id">The local peer identifier.</param>
        /// <param name="port">The listening port to advertise.</param>
        /// <returns>The completed tracker URL.</returns>
        private string BuildAnnounceUrl(Torrent torrent, TrackerEvent ev, string id, int port)
        {
            return string.Format(
                "{0}?info_hash={1}&peer_id={2}&port={3}&uploaded={4}&downloaded={5}&left={6}&event={7}&compact=1",
                Address,
                torrent.UrlSafeStringInfohash,
                Uri.EscapeDataString(id),
                port,
                torrent.Uploaded,
                torrent.Downloaded,
                torrent.Left,
                ev.ToString().ToLowerInvariant());
        }

        /// <summary>
        /// Performs the HTTP GET request for a tracker announce and processes the response body.
        /// </summary>
        /// <param name="url">The full announce URL to request.</param>
        /// <returns>A task that completes once the response has been handled.</returns>
        private async Task RequestAsync(string url)
        {
            try
            {
                using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Log.Error(this, "error reaching tracker: " + response.StatusCode + " " + response.ReasonPhrase);
                    return;
                }

                byte[] data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                HandleResponse(data);
            }
            catch (Exception ex)
            {
                Log.Error(this, "error reaching tracker: " + ex.Message);
            }
        }

        /// <summary>
        /// Decodes a tracker response, updates the announce interval, and raises the discovered peer list.
        /// </summary>
        /// <param name="data">The raw bencoded tracker response.</param>
        private void HandleResponse(byte[] data)
        {
            var info = BEncoding.Decode(data) as Dictionary<string, object>;
            if (info == null)
            {
                Log.Error(this, "unable to decode tracker announce response");
                return;
            }

            if (!info.TryGetValue("interval", out var intervalValue) || intervalValue is not long intervalSeconds)
            {
                Log.Error(this, "tracker announce response missing interval");
                return;
            }

            if (!info.TryGetValue("peers", out var peerValue) || peerValue is not byte[] peerInfo)
            {
                Log.Error(this, "tracker announce response missing compact peer list");
                return;
            }

            PeerRequestInterval = TimeSpan.FromSeconds(intervalSeconds);

            // Compact peer lists encode each peer as 4 bytes of IPv4 address followed by 2 bytes of port.
            List<IPEndPoint> peers = new List<IPEndPoint>(peerInfo.Length / 6);
            for (int i = 0; i + 5 < peerInfo.Length; i += 6)
            {
                string address = $"{peerInfo[i]}.{peerInfo[i + 1]}.{peerInfo[i + 2]}.{peerInfo[i + 3]}";
                int port = (peerInfo[i + 4] << 8) | peerInfo[i + 5];
                peers.Add(new IPEndPoint(IPAddress.Parse(address), port));
            }

            PeerListUpdated?.Invoke(this, peers);
        }

        /// <summary>
        /// Returns the tracker address for logging and display purposes.
        /// </summary>
        /// <returns>The tracker announce URL.</returns>
        public override string ToString()
        {
            return Address;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BitTorrent
{
    /// <summary>
    /// Provides a compact HTTP tracker suitable for small demos and self-hosted transfers.
    /// </summary>
    public sealed class HttpTrackerServer : IDisposable
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, Dictionary<string, IPEndPoint>> peersBySwarm = new Dictionary<string, Dictionary<string, IPEndPoint>>();
        private readonly HttpListener listener = new HttpListener();
        private readonly IPAddress? advertisedAddress;
        private bool disposed;

        public HttpTrackerServer(string listenPrefix, IPAddress? advertisedAddress = null)
        {
            ListenPrefix = listenPrefix;
            this.advertisedAddress = advertisedAddress?.MapToIPv4();
            listener.Prefixes.Add(listenPrefix);
        }

        public string ListenPrefix { get; }

        /// <summary>
        /// Starts the tracker and processes announce requests until cancellation.
        /// </summary>
        /// <param name="cancellationToken">Stops the listener when cancelled.</param>
        /// <returns>A task that completes when the listener exits.</returns>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(Stop);

            listener.Start();
            Log.Info(this, $"listening on {ListenPrefix}");

            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (IsExpectedShutdown(ex, cancellationToken))
                {
                    return;
                }

                _ = Task.Run(() => HandleContextAsync(context), cancellationToken);
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            listener.Close();
        }

        public override string ToString()
        {
            return "tracker";
        }

        private async Task HandleContextAsync(HttpListenerContext context)
        {
            try
            {
                if (!Int32.TryParse(context.Request.QueryString["port"], out var requesterPort))
                {
                    await WriteErrorAsync(context, 400, "missing or invalid port").ConfigureAwait(false);
                    return;
                }

                string swarmKey = context.Request.QueryString["info_hash"] ?? "";
                string eventName = context.Request.QueryString["event"] ?? "";
                IPAddress requesterAddress = GetRequesterAddress(context);
                string requesterKey = requesterAddress + ":" + requesterPort;
                IPEndPoint requester = new IPEndPoint(requesterAddress, requesterPort);

                byte[] peerBytes;
                lock (sync)
                {
                    if (!peersBySwarm.TryGetValue(swarmKey, out var swarm))
                    {
                        swarm = new Dictionary<string, IPEndPoint>();
                        peersBySwarm[swarmKey] = swarm;
                    }

                    if (string.Equals(eventName, "stopped", StringComparison.OrdinalIgnoreCase))
                        swarm.Remove(requesterKey);
                    else
                        swarm[requesterKey] = requester;

                    peerBytes = swarm
                        .Where(x => x.Key != requesterKey)
                        .SelectMany(x => EncodeCompactPeer(x.Value))
                        .ToArray();
                }

                Dictionary<string, object> response = new Dictionary<string, object>
                {
                    ["interval"] = 10L,
                    ["peers"] = peerBytes
                };

                byte[] data = BEncoding.Encode(response);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = data.Length;
                await context.Response.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                context.Response.Close();

                Log.Debug(this, $"announce from {requester}, returned {peerBytes.Length / 6} peers");
            }
            catch (Exception ex)
            {
                Log.Error(this, "announce failed: " + ex.Message);
                TryClose(context);
            }
        }

        private IPAddress GetRequesterAddress(HttpListenerContext context)
        {
            IPAddress address = context.Request.RemoteEndPoint?.Address.MapToIPv4() ?? IPAddress.Loopback;

            if (advertisedAddress != null && IsPrivateOrLoopback(address))
                return advertisedAddress;

            return address;
        }

        private static bool IsPrivateOrLoopback(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
                return true;

            byte[] bytes = address.MapToIPv4().GetAddressBytes();
            return bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
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

        private static async Task WriteErrorAsync(HttpListenerContext context, int statusCode, string message)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(message);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = data.Length;
            await context.Response.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            context.Response.Close();
        }

        private static bool IsExpectedShutdown(Exception ex, CancellationToken cancellationToken)
        {
            return cancellationToken.IsCancellationRequested ||
                ex is ObjectDisposedException ||
                ex is HttpListenerException ||
                ex is InvalidOperationException;
        }

        private void Stop()
        {
            if (listener.IsListening)
                listener.Stop();
        }

        private static void TryClose(HttpListenerContext context)
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // The connection may already be gone.
            }
        }
    }
}

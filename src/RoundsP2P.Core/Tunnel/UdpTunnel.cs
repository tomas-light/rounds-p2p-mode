using System;
using System.Net;
using System.Net.Sockets;

namespace RoundsP2P.Core.Tunnel
{
    /// <summary>
    /// Single-threaded, nonblocking bridge between loopback UDP and framed messages.
    /// The outer Steam connection must authenticate the peer before calling ReceiveFrame.
    /// Local proxy endpoints are trusted within the current machine, not authenticated users.
    /// </summary>
    public sealed class UdpTunnel : IDisposable
    {
        private readonly Socket[] sockets;
        private readonly IPEndPoint?[] clients = new IPEndPoint?[2];
        private readonly Action<byte[]> sendFrame;
        private readonly bool host;
        private readonly byte[] receiveBuffer = new byte[DatagramFrame.MaxPayloadLength + 1];
        private bool disposed;
        private int nextRoute;

        private UdpTunnel(bool host, int backendMasterPort, int backendGamePort, Action<byte[]> sendFrame)
        {
            this.sendFrame = sendFrame ?? throw new ArgumentNullException(nameof(sendFrame));
            this.host = host;
            sockets = new Socket[2];
            try
            {
                sockets[0] = CreateSocket(host ? backendMasterPort : 0);
                sockets[1] = CreateSocket(host ? backendGamePort : 0);
                MasterPort = ((IPEndPoint)sockets[0].LocalEndPoint).Port;
                GamePort = ((IPEndPoint)sockets[1].LocalEndPoint).Port;
            }
            catch
            {
                foreach (Socket? socket in sockets)
                    socket?.Dispose();
                throw;
            }
        }

        public int MasterPort { get; }
        public int GamePort { get; }

        public static UdpTunnel CreateClient(Action<byte[]> sendFrame) => new UdpTunnel(false, 0, 0, sendFrame);

        public static UdpTunnel CreateHost(int backendMasterPort, int backendGamePort, Action<byte[]> sendFrame)
        {
            ValidatePort(backendMasterPort, nameof(backendMasterPort));
            ValidatePort(backendGamePort, nameof(backendGamePort));
            if (backendMasterPort == backendGamePort)
                throw new ArgumentException("Master and game backend ports must differ.", nameof(backendGamePort));
            return new UdpTunnel(true, backendMasterPort, backendGamePort, sendFrame);
        }

        /// <summary>Consumes at most maxDatagrams, sharing the budget fairly between both routes.</summary>
        public void Pump(int maxDatagrams = 64)
        {
            ThrowIfDisposed();
            if (maxDatagrams < 1 || maxDatagrams > 4096)
                throw new ArgumentOutOfRangeException(nameof(maxDatagrams));

            int consumed = 0;
            int emptyRoutes = 0;
            while (consumed < maxDatagrams && emptyRoutes < 2)
            {
                int index = nextRoute;
                nextRoute = 1 - nextRoute;
                if (!TryReceive(index, out int length))
                {
                    emptyRoutes++;
                    continue;
                }
                emptyRoutes = 0;
                consumed++;
                if (length <= 0 || length > DatagramFrame.MaxPayloadLength)
                    continue;

                var payload = new byte[length];
                Buffer.BlockCopy(receiveBuffer, 0, payload, 0, length);
                sendFrame(DatagramFrame.Encode(index == 0 ? DatagramRoute.Master : DatagramRoute.Game, payload));
            }
        }

        /// <returns>True only when a valid frame was forwarded to a local UDP endpoint.</returns>
        public bool ReceiveFrame(byte[] frame)
        {
            ThrowIfDisposed();
            if (!DatagramFrame.TryDecode(frame, out DatagramRoute route, out byte[] payload))
                return false;
            int index = route == DatagramRoute.Master ? 0 : 1;
            if (!host && clients[index] == null)
                return false;
            try
            {
                int sent = host ? sockets[index].Send(payload) : sockets[index].SendTo(payload, clients[index]!);
                return sent == payload.Length;
            }
            catch (SocketException exception) when (IsTransientDatagramError(exception.SocketErrorCode))
            {
                // Photon already owns packet loss/retry semantics; do not add an unbounded queue.
                return false;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            foreach (Socket socket in sockets)
                socket.Dispose();
        }

        private static Socket CreateSocket(int backendPort)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                if (backendPort != 0)
                    socket.Connect(new IPEndPoint(IPAddress.Loopback, backendPort));
                socket.Blocking = false;
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private bool TryReceive(int index, out int length)
        {
            length = 0;
            try
            {
                if (host)
                    length = sockets[index].Receive(receiveBuffer);
                else
                {
                    EndPoint sender = new IPEndPoint(IPAddress.Loopback, 0);
                    length = sockets[index].ReceiveFrom(receiveBuffer, ref sender);
                    // A reconnect creates a new Photon UDP endpoint. Replies follow
                    // the most recent local sender; no remote address is accepted.
                    if (sender is IPEndPoint endpoint && IPAddress.IsLoopback(endpoint.Address))
                        clients[index] = endpoint;
                    else
                        length = 0;
                }
                return true;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.MessageSize)
            {
                // Windows consumes an oversized UDP datagram while reporting this error.
                length = DatagramFrame.MaxPayloadLength + 1;
                return true;
            }
            catch (SocketException exception) when (IsTransientDatagramError(exception.SocketErrorCode))
            {
                // Windows can report an ICMP reset from a previous UDP send after
                // Photon has closed that endpoint. A reconnect must remain usable.
                return false;
            }
        }

        private static bool IsTransientDatagramError(SocketError error) =>
            error == SocketError.WouldBlock || error == SocketError.IOPending || error == SocketError.NoBufferSpaceAvailable ||
            error == SocketError.ConnectionReset || error == SocketError.ConnectionRefused;

        private static void ValidatePort(int port, string name)
        {
            if (port < 1 || port > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(name);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(UdpTunnel));
        }
    }
}

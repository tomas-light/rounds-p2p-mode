using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using RoundsP2P.Core.Tunnel;
using Xunit;

namespace RoundsP2P.Core.Tests.Tunnel
{
    public sealed class UdpTunnelTests
    {
        [Fact]
        public void BothRoutesCarryActualUdpRequestsAndRepliesThroughTwoTunnels()
        {
            using Socket masterBackend = LocalSocket();
            using Socket gameBackend = LocalSocket();
            UdpTunnel? host = null;
            using UdpTunnel guest = UdpTunnel.CreateClient(frame => Assert.True(host!.ReceiveFrame(frame)));
            using (host = UdpTunnel.CreateHost(Port(masterBackend), Port(gameBackend), frame => Assert.True(guest.ReceiveFrame(frame))))
            using (Socket masterPlayer = ConnectedSocket(guest.MasterPort))
            using (Socket gamePlayer = ConnectedSocket(guest.GamePort))
            {
                Assert.Equal(3, masterPlayer.Send(new byte[] { 10, 11, 12 }));
                Assert.Equal(2, gamePlayer.Send(new byte[] { 20, 21 }));
                byte[]? masterReply = null;
                byte[]? gameReply = null;
                WaitUntil(() =>
                {
                    guest.Pump();
                    Echo(masterBackend);
                    Echo(gameBackend);
                    host.Pump();
                    masterReply ??= TryRead(masterPlayer);
                    gameReply ??= TryRead(gamePlayer);
                    return masterReply != null && gameReply != null;
                });
                Assert.Equal(new byte[] { 10, 11, 12 }, masterReply);
                Assert.Equal(new byte[] { 20, 21 }, gameReply);
            }
        }

        [Fact]
        public void PumpBudgetIsGlobalAndFairBetweenRoutes()
        {
            var frames = new List<byte[]>();
            using UdpTunnel tunnel = UdpTunnel.CreateClient(frames.Add);
            using Socket master = ConnectedSocket(tunnel.MasterPort);
            using Socket game = ConnectedSocket(tunnel.GamePort);
            for (int index = 0; index < 5; index++)
                master.Send(new byte[] { (byte)index });
            game.Send(new byte[] { 99 });

            WaitUntil(() => { tunnel.Pump(1); return frames.Count > 0; });
            Assert.Single(frames);
            tunnel.Pump(1);
            Assert.Equal(2, frames.Count);
            Assert.True(DatagramFrame.TryDecode(frames[0], out DatagramRoute first, out _));
            Assert.True(DatagramFrame.TryDecode(frames[1], out DatagramRoute second, out _));
            Assert.Equal(DatagramRoute.Master, first);
            Assert.Equal(DatagramRoute.Game, second);
            WaitUntil(() => { tunnel.Pump(2); return frames.Count == 6; });
        }

        [Fact]
        public void ReconnectRepliesFollowNewestLocalPhotonEndpoint()
        {
            int requests = 0;
            using UdpTunnel tunnel = UdpTunnel.CreateClient(_ => requests++);
            using Socket oldPlayer = ConnectedSocket(tunnel.MasterPort);
            using Socket newPlayer = ConnectedSocket(tunnel.MasterPort);
            oldPlayer.Send(new byte[] { 1 });
            WaitUntil(() => { tunnel.Pump(); return requests == 1; });
            newPlayer.Send(new byte[] { 2 });
            WaitUntil(() => { tunnel.Pump(); return requests == 2; });

            Assert.True(tunnel.ReceiveFrame(DatagramFrame.Encode(DatagramRoute.Master, new byte[] { 3 })));
            byte[]? reply = null;
            WaitUntil(() => (reply = TryRead(newPlayer)) != null);
            Assert.Equal(new byte[] { 3 }, reply);
            Assert.False(oldPlayer.Poll(0, SelectMode.SelectRead));
        }

        [Fact]
        public void ClosedLocalEndpointDoesNotPreventReconnectAfterDelayedReply()
        {
            int requests = 0;
            using UdpTunnel tunnel = UdpTunnel.CreateClient(_ => requests++);
            using (Socket oldPlayer = ConnectedSocket(tunnel.MasterPort))
            {
                oldPlayer.Send(new byte[] { 1 });
                WaitUntil(() => { tunnel.Pump(); return requests == 1; });
            }
            // On Windows this reply produces an ICMP reset on the proxy socket;
            // the next ReceiveFrom must tolerate it and accept the new endpoint.
            tunnel.ReceiveFrame(DatagramFrame.Encode(DatagramRoute.Master, new byte[] { 2 }));
            tunnel.Pump();
            using Socket newPlayer = ConnectedSocket(tunnel.MasterPort);
            newPlayer.Send(new byte[] { 3 });
            WaitUntil(() => { tunnel.Pump(); return requests == 2; });
            Assert.True(tunnel.ReceiveFrame(DatagramFrame.Encode(DatagramRoute.Master, new byte[] { 4 })));
            byte[]? reply = null;
            WaitUntil(() => (reply = TryRead(newPlayer)) != null);
            Assert.Equal(new byte[] { 4 }, reply);
        }

        [Fact]
        public void MalformedFramesAndRepliesWithoutLocalRequestAreDropped()
        {
            using UdpTunnel tunnel = UdpTunnel.CreateClient(_ => Assert.Fail("No datagram was sent."));
            Assert.False(tunnel.ReceiveFrame(new byte[] { 1, 2, 3 }));
            Assert.False(tunnel.ReceiveFrame(null!));
            Assert.False(tunnel.ReceiveFrame(DatagramFrame.Encode(DatagramRoute.Game, new byte[] { 4 })));
            tunnel.Pump();
        }

        [Fact]
        public void EmptyAndOversizedUdpDatagramsAreConsumedWithoutForwarding()
        {
            var frames = new List<byte[]>();
            using UdpTunnel tunnel = UdpTunnel.CreateClient(frames.Add);
            using Socket sender = ConnectedSocket(tunnel.MasterPort);
            sender.Send(Array.Empty<byte>());
            sender.Send(new byte[4096]);
            sender.Send(new byte[] { 12 });
            WaitUntil(() => { tunnel.Pump(); return frames.Count > 0; });
            Assert.Single(frames);
            Assert.True(DatagramFrame.TryDecode(frames[0], out _, out byte[] payload));
            Assert.Equal(new byte[] { 12 }, payload);
        }

        [Fact]
        public void MaximumPayloadSurvivesActualUdpForwarding()
        {
            byte[]? frame = null;
            using UdpTunnel tunnel = UdpTunnel.CreateClient(value => frame = value);
            using Socket sender = ConnectedSocket(tunnel.GamePort);
            var payload = new byte[DatagramFrame.MaxPayloadLength];
            new Random(81).NextBytes(payload);
            sender.Send(payload);
            WaitUntil(() => { tunnel.Pump(); return frame != null; });
            Assert.True(DatagramFrame.TryDecode(frame!, out DatagramRoute route, out byte[] decoded));
            Assert.Equal(DatagramRoute.Game, route);
            Assert.Equal(payload, decoded);
            Assert.True(tunnel.ReceiveFrame(frame!));
            byte[]? reply = null;
            WaitUntil(() => (reply = TryRead(sender)) != null);
            Assert.Equal(payload, reply);
        }

        [Fact]
        public void DisposeIsIdempotentAndReleasesBothOwnedPorts()
        {
            var tunnel = UdpTunnel.CreateClient(_ => { });
            int masterPort = tunnel.MasterPort;
            int gamePort = tunnel.GamePort;
            tunnel.Dispose();
            tunnel.Dispose();
            using Socket master = LocalSocket(masterPort);
            using Socket game = LocalSocket(gamePort);
            Assert.Throws<ObjectDisposedException>(() => tunnel.Pump());
            Assert.Throws<ObjectDisposedException>(() => tunnel.ReceiveFrame(Array.Empty<byte>()));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(4097)]
        public void RejectsUnboundedOrInvalidPumpBudget(int budget)
        {
            using UdpTunnel tunnel = UdpTunnel.CreateClient(_ => { });
            Assert.Throws<ArgumentOutOfRangeException>(() => tunnel.Pump(budget));
        }

        [Fact]
        public void RejectsInvalidConstructionArguments()
        {
            Assert.Throws<ArgumentNullException>(() => UdpTunnel.CreateClient(null!));
            Assert.Throws<ArgumentNullException>(() => UdpTunnel.CreateHost(5055, 5056, null!));
            Assert.Throws<ArgumentException>(() => UdpTunnel.CreateHost(5055, 5055, _ => { }));
            foreach (int port in new[] { 0, -1, 65536 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => UdpTunnel.CreateHost(port, 5056, _ => { }));
                Assert.Throws<ArgumentOutOfRangeException>(() => UdpTunnel.CreateHost(5055, port, _ => { }));
            }
        }

        private static Socket LocalSocket(int port = 0)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Blocking = false;
            return socket;
        }

        private static Socket ConnectedSocket(int port)
        {
            Socket socket = LocalSocket();
            socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }

        private static int Port(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

        private static byte[]? TryRead(Socket socket)
        {
            var bytes = new byte[DatagramFrame.MaxPayloadLength];
            try
            {
                int length = socket.Receive(bytes);
                Array.Resize(ref bytes, length);
                return bytes;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock)
            {
                return null;
            }
        }

        private static void Echo(Socket socket)
        {
            var bytes = new byte[DatagramFrame.MaxPayloadLength];
            EndPoint sender = new IPEndPoint(IPAddress.Loopback, 0);
            try
            {
                int length = socket.ReceiveFrom(bytes, ref sender);
                socket.SendTo(bytes, 0, length, SocketFlags.None, sender);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock) { }
        }

        private static void WaitUntil(Func<bool> condition)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(deadline.ElapsedMilliseconds < 3000, "Timed out pumping local UDP sockets.");
                Thread.Sleep(1);
            }
        }
    }
}

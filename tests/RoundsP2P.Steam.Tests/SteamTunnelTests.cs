using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using RoundsP2P.Plugin;
using Xunit;

namespace RoundsP2P.Steam.Tests;

[Collection("Native Steam")]
[Trait("Category", "NativeSteam")]
public sealed class SteamTunnelTests
{
    private readonly SteamRuntime runtime;
    public SteamTunnelTests(SteamRuntime runtime) => this.runtime = runtime;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_adapter_preserves_UDP_payload_in_both_directions_on_each_route(bool gameRoute)
    {
        using var master = Bind();
        using var game = Bind();
        SteamDatagramTransport.CreateConnectedPairForTest(Port(master), Port(game), _ => { }, out var host, out var client);
        using (host)
        using (client)
        using (var photon = Bind())
        {
            runtime.WaitUntil(() => host.IsConnected && client.IsConnected, "production session handshake", () => { host.Pump(); client.Pump(); });
            Socket backend = gameRoute ? game : master;
            byte[] request = Enumerable.Range(0, 1000).Select(index => (byte)(index % 251)).ToArray();
            photon.SendTo(request, new IPEndPoint(IPAddress.Loopback, gameRoute ? client.GamePort : client.MasterPort));
            byte[]? received = null;
            EndPoint from = new IPEndPoint(IPAddress.Loopback, 0);
            runtime.WaitUntil(() => TryReceive(backend, ref from, out received), "UDP request crossing actual Steam socket pair", () => { client.Pump(); host.Pump(); });
            Assert.Equal(request, received);
            byte[] response = { 0, 128, 255, 3, 2, 1 };
            backend.SendTo(response, from);
            EndPoint replyFrom = new IPEndPoint(IPAddress.Loopback, 0);
            byte[]? reply = null;
            runtime.WaitUntil(() => TryReceive(photon, ref replyFrom, out reply), "UDP response crossing actual Steam socket pair", () => { host.Pump(); client.Pump(); });
            Assert.Equal(response, reply);
            Assert.Equal(gameRoute ? client.GamePort : client.MasterPort, ((IPEndPoint)replyFrom).Port);
        }
    }

    [Fact]
    public void Disposing_production_transport_twice_releases_its_local_proxy_ports()
    {
        using var master = Bind();
        using var game = Bind();
        SteamDatagramTransport.CreateConnectedPairForTest(Port(master), Port(game), _ => { }, out var host, out var client);
        int masterProxy = client.MasterPort;
        int gameProxy = client.GamePort;
        try
        {
            runtime.WaitUntil(() => host.IsConnected && client.IsConnected, "production session handshake", () => { host.Pump(); client.Pump(); });
            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);
            client.Dispose();
            client.Dispose();
            Assert.False(client.IsConnected);
            client.Pump();
            using var reboundMaster = Bind(masterProxy);
            using var reboundGame = Bind(gameProxy);
            Assert.Equal(masterProxy, Port(reboundMaster));
            Assert.Equal(gameProxy, Port(reboundGame));
        }
        finally { client.Dispose(); host.Dispose(); }
    }

    [Fact]
    public void Different_session_tokens_are_rejected_before_UDP_gameplay_becomes_ready()
    {
        using var master = Bind();
        using var game = Bind();
        SteamDatagramTransport.CreateConnectedPairForTest(Port(master), Port(game), _ => { }, out var host, out var client,
            hostToken: "first-session", clientToken: "different-session");
        using (host)
        using (client)
        {
            runtime.WaitUntil(() => host.IsClosed || client.IsClosed, "mismatched production session handshake rejection", () => { host.Pump(); client.Pump(); });
            Assert.False(host.IsConnected);
            Assert.False(client.IsConnected);
        }
    }

    [Fact]
    public void Both_ends_expose_a_measured_peer_ping_and_clear_it_on_disposal()
    {
        using var master = Bind();
        using var game = Bind();
        SteamDatagramTransport.CreateConnectedPairForTest(Port(master), Port(game), _ => { }, out var host, out var client);
        using (host)
        using (client)
        {
            runtime.WaitUntil(() => host.IsConnected && client.IsConnected &&
                host.PeerPingMilliseconds.HasValue && client.PeerPingMilliseconds.HasValue,
                "Steam peer RTT on both ends", () => { host.Pump(); client.Pump(); });
            Assert.True(host.PeerPingMilliseconds >= 0);
            Assert.True(client.PeerPingMilliseconds >= 0);

            client.Dispose();
            Assert.Null(client.PeerPingMilliseconds);
        }
    }

    private static Socket Bind(int port = 0)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ExclusiveAddressUse = true;
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        socket.Blocking = false;
        return socket;
    }

    private static int Port(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    private static bool TryReceive(Socket socket, ref EndPoint from, out byte[]? payload)
    {
        payload = null;
        var buffer = new byte[1500];
        try
        {
            int count = socket.ReceiveFrom(buffer, ref from);
            payload = buffer.Take(count).ToArray();
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock) { return false; }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using ExitGames.Client.Photon;
using Photon.Realtime;
using RoundsP2P.Core;
using UnityEngine;
using Xunit;

namespace RoundsP2P.Network.Tests;

[Collection("Photon server")]
public sealed class PhotonCompatibilityTests
{
    [Fact]
    public void Production_launcher_survives_log_observer_errors_and_disposes_only_its_process()
    {
        using var first = new PhotonSession(nameof(Production_launcher_survives_log_observer_errors_and_disposes_only_its_process), throwingLogObserver: true);
        using var other = new PhotonSession("independent-server-survives");
        Assert.True(first.IsListening);
        Assert.True(other.IsListening);
        using Process firstProcess = Process.GetProcessById(first.OwnedProcessId);
        TestClient firstClient = first.Connect("host");
        first.CreateRoom(firstClient, "logger-test");

        first.Dispose();
        first.Dispose();

        Assert.True(firstProcess.WaitForExit(5000));
        Assert.True(first.HasExited);
        Assert.False(first.IsListening);
        Assert.False(other.HasExited);
        Assert.True(other.IsListening);
        TestClient survivingClient = other.Connect("host");
        other.CreateRoom(survivingClient, "still-running");
        Assert.True(survivingClient.InRoom);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Production_launcher_refuses_an_occupied_port_without_config_or_listener_side_effects(bool occupyMasterPort)
    {
        using var externalListener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
            ReceiveTimeout = 1000
        };
        externalListener.Bind(new IPEndPoint(IPAddress.Any, 0));
        int occupiedPort = ((IPEndPoint)externalListener.LocalEndPoint!).Port;
        int availablePort = PhotonSession.FreeUdpPort();
        Assert.NotEqual(occupiedPort, availablePort);
        int masterPort = occupyMasterPort ? occupiedPort : availablePort;
        int gamePort = occupyMasterPort ? availablePort : occupiedPort;
        string root = PhotonSession.FindRepositoryRoot();
        string executable = Path.Combine(root, ".cache", "Luxon", "v1.5", "luxon_server.exe");
        string directory = Path.Combine(root, "artifacts", "network-tests", "occupied-port-" + Guid.NewGuid().ToString("N"));
        var options = new PrototypeOptions(PrototypeRole.Host, "127.0.0.1", "127.0.0.1", "occupied-port", masterPort, gamePort);
        LuxonServerProcess? unexpectedServer = null;
        try
        {
            Assert.Throws<SocketException>(() => unexpectedServer = new LuxonServerProcess(executable, directory, options, _ => { }));
        }
        finally
        {
            unexpectedServer?.Dispose();
        }

        Assert.False(Directory.Exists(directory));
        // The preflight must release its first socket when the second bind fails.
        using var availableListener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        availableListener.Bind(new IPEndPoint(IPAddress.Any, availablePort));
        // An unrelated socket must remain usable after the rejected launch.
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        byte[] probe = { 17, 23, 42 };
        Assert.Equal(probe.Length, sender.Send(probe, probe.Length, new IPEndPoint(IPAddress.Loopback, occupiedPort)));
        var received = new byte[probe.Length];
        Assert.Equal(probe.Length, externalListener.Receive(received));
        Assert.Equal(probe, received);
    }

    [Fact]
    public void Two_current_game_clients_join_with_distinct_actor_ids_and_shared_properties()
    {
        using var session = new PhotonSession(nameof(Two_current_game_clients_join_with_distinct_actor_ids_and_shared_properties));
        TestClient host = session.Connect("host");
        session.CreateRoom(host, "join-test");
        TestClient guest = session.Connect("guest");
        session.JoinRoom(guest, "join-test");
        session.WaitUntil(() => host.CurrentRoom.PlayerCount == 2 && guest.CurrentRoom.PlayerCount == 2, "both clients observe two actors");

        Assert.NotEqual(host.LocalPlayer.ActorNumber, guest.LocalPlayer.ActorNumber);
        Assert.Equal(host.LocalPlayer.ActorNumber, host.CurrentRoom.MasterClientId);
        Assert.Equal(host.CurrentRoom.MasterClientId, guest.CurrentRoom.MasterClientId);
        Assert.Equal(2, host.CurrentRoom.MaxPlayers);
        Assert.Equal(2, guest.CurrentRoom.MaxPlayers);
        Assert.Equal(1, guest.CurrentRoom.CustomProperties["prototype"]);

        Assert.True(host.CurrentRoom.SetCustomProperties(new Hashtable { { "ready-generation", 7 } }));
        Assert.True(guest.LocalPlayer.SetCustomProperties(new Hashtable { { "ready", true } }));
        session.WaitUntil(() => Equals(guest.CurrentRoom.CustomProperties["ready-generation"], 7) && host.CurrentRoom.Players[guest.LocalPlayer.ActorNumber].CustomProperties.ContainsKey("ready"), "room and actor properties propagate");
        Assert.Equal(true, host.CurrentRoom.Players[guest.LocalPlayer.ActorNumber].CustomProperties["ready"]);
    }

    [Fact]
    public void Reliable_game_payloads_and_Unity_custom_types_roundtrip_in_both_directions()
    {
        using var session = new PhotonSession(nameof(Reliable_game_payloads_and_Unity_custom_types_roundtrip_in_both_directions));
        TestClient host = session.Connect("host");
        session.CreateRoom(host, "events-test");
        TestClient guest = session.Connect("guest");
        session.JoinRoom(guest, "events-test");

        for (int sequence = 0; sequence < 20; sequence++)
        {
            var payload = new Hashtable
            {
                { "sequence", sequence },
                { "rpc", "prototype-event" },
                { "values", new object[] { 42, 1.5f, true, new byte[] { 0, 127, 255 } } },
                { "position", new Vector2(sequence, -sequence) }
            };
            Assert.True(host.OpRaiseEvent(42, payload, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable));
            Assert.True(guest.OpRaiseEvent(43, sequence, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable));
        }
        session.WaitUntil(() => guest.Events.Count(item => item.Code == 42) == 20 && host.Events.Count(item => item.Code == 43) == 20, "reliable events in both directions");

        Assert.Equal(Enumerable.Range(0, 20), host.Events.Where(item => item.Code == 43).Select(item => Assert.IsType<int>(item.Content)));
        ReceivedEvent[] received = guest.Events.Where(item => item.Code == 42).ToArray();
        for (int sequence = 0; sequence < received.Length; sequence++)
        {
            Assert.Equal(host.LocalPlayer.ActorNumber, received[sequence].Sender);
            Hashtable payload = Assert.IsType<Hashtable>(received[sequence].Content);
            Assert.Equal(sequence, payload["sequence"]);
            Assert.Equal("prototype-event", payload["rpc"]);
            var values = Assert.IsType<object[]>(payload["values"]);
            Assert.Equal(42, values[0]);
            Assert.Equal(1.5f, values[1]);
            Assert.Equal(true, values[2]);
            Assert.Equal(new byte[] { 0, 127, 255 }, Assert.IsType<byte[]>(values[3]));
            Vector2 position = Assert.IsType<Vector2>(payload["position"]);
            Assert.Equal((float)sequence, position.x);
            Assert.Equal((float)-sequence, position.y);
        }
    }

    [Fact]
    public void Cached_events_are_delivered_to_late_joining_player()
    {
        using var session = new PhotonSession(nameof(Cached_events_are_delivered_to_late_joining_player));
        TestClient host = session.Connect("host");
        session.CreateRoom(host, "cache-test");
        Assert.True(host.OpRaiseEvent(44, new object[] { "spawn", 1001 }, new RaiseEventOptions
        {
            Receivers = ReceiverGroup.All,
            CachingOption = EventCaching.AddToRoomCache
        }, SendOptions.SendReliable));
        session.WaitUntil(() => host.Events.Any(item => item.Code == 44), "server echoes cached event before guest joins");

        TestClient guest = session.Connect("guest");
        session.JoinRoom(guest, "cache-test");
        session.WaitUntil(() => guest.Events.Any(item => item.Code == 44), "late joiner receives cached spawn-like event");
        ReceivedEvent cached = Assert.Single(guest.Events, item => item.Code == 44);
        Assert.Equal(host.LocalPlayer.ActorNumber, cached.Sender);
        Assert.Equal(new object[] { "spawn", 1001 }, Assert.IsType<object[]>(cached.Content));
    }

    [Fact]
    public void Third_player_is_rejected_from_two_player_room()
    {
        using var session = new PhotonSession(nameof(Third_player_is_rejected_from_two_player_room));
        TestClient host = session.Connect("host");
        session.CreateRoom(host, "capacity-test");
        TestClient guest = session.Connect("guest");
        session.JoinRoom(guest, "capacity-test");
        TestClient third = session.Connect("third");
        Assert.True(third.OpJoinRoom(new EnterRoomParams { RoomName = "capacity-test" }));
        session.WaitUntil(() => third.Responses.Any(response => response.Code == OperationCode.JoinGame && response.ReturnCode != 0), "server rejects third player");
        ReceivedResponse rejection = Assert.Single(third.Responses, response => response.Code == OperationCode.JoinGame && response.ReturnCode != 0);
        Assert.Equal(ErrorCode.GameFull, rejection.ReturnCode);
        Assert.False(third.InRoom);
        Assert.Equal(2, host.CurrentRoom.PlayerCount);
        Assert.Equal(2, guest.CurrentRoom.PlayerCount);
    }

    [Fact]
    public void Leaving_player_is_removed_and_remaining_client_keeps_room()
    {
        using var session = new PhotonSession(nameof(Leaving_player_is_removed_and_remaining_client_keeps_room));
        TestClient host = session.Connect("host");
        session.CreateRoom(host, "leave-test");
        TestClient guest = session.Connect("guest");
        session.JoinRoom(guest, "leave-test");
        session.WaitUntil(() => host.CurrentRoom.PlayerCount == 2, "host observes guest before disconnect");
        int guestActor = guest.LocalPlayer.ActorNumber;

        guest.Disconnect();
        session.WaitUntil(() => guest.State == ClientState.Disconnected && host.CurrentRoom.PlayerCount == 1, "disconnect cleanup reaches remaining actor");
        Assert.True(host.InRoom);
        Assert.False(host.CurrentRoom.Players.ContainsKey(guestActor));
    }
}

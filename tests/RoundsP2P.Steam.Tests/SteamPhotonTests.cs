using System;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Realtime;
using Xunit;

namespace RoundsP2P.Steam.Tests;

[Collection("Native Steam")]
[Trait("Category", "NativeSteam")]
public sealed class SteamPhotonTests
{
    private readonly SteamRuntime runtime;
    public SteamPhotonTests(SteamRuntime runtime) => this.runtime = runtime;

    [Fact]
    public void Real_Photon_client_switches_master_to_game_server_through_production_Steam_adapter()
    {
        using var scenario = new SteamPhotonScenario(runtime, nameof(Real_Photon_client_switches_master_to_game_server_through_production_Steam_adapter));
        Assert.Equal("127.0.0.1:" + scenario.GuestGamePort, scenario.Guest.CurrentServerAddress);
        Assert.Equal(2, scenario.Host.CurrentRoom.PlayerCount);
        Assert.Equal(2, scenario.Guest.CurrentRoom.PlayerCount);
        Assert.Equal(2, scenario.Guest.CurrentRoom.MaxPlayers);
        Assert.NotEqual(scenario.Host.LocalPlayer.ActorNumber, scenario.Guest.LocalPlayer.ActorNumber);
    }

    [Fact]
    public void Reliable_Photon_events_cross_production_Steam_adapter_in_both_directions()
    {
        using var scenario = new SteamPhotonScenario(runtime, nameof(Reliable_Photon_events_cross_production_Steam_adapter_in_both_directions));
        var options = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        byte[] payload = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
        Assert.True(scenario.Guest.OpRaiseEvent(42, payload, options, SendOptions.SendReliable));
        scenario.WaitUntil(() => scenario.Host.Events.Any(message => message.Code == 42), "fragmented reliable guest event");
        Assert.Equal(payload, Assert.IsType<byte[]>(scenario.Host.Events.Last(message => message.Code == 42).Content));
        Assert.True(scenario.Host.OpRaiseEvent(43, "host-ack", options, SendOptions.SendReliable));
        scenario.WaitUntil(() => scenario.Guest.Events.Any(message => message.Code == 43), "host acknowledgement through Steam");
        Assert.Equal("host-ack", scenario.Guest.Events.Last(message => message.Code == 43).Content);
    }

    [Fact]
    public void Room_property_updates_reach_guest_over_production_Steam_adapter()
    {
        using var scenario = new SteamPhotonScenario(runtime, nameof(Room_property_updates_reach_guest_over_production_Steam_adapter));
        Assert.True(scenario.Host.CurrentRoom.SetCustomProperties(new Hashtable { { "round", 5 }, { "state", "ready" } }));
        scenario.WaitUntil(() => scenario.Guest.CurrentRoom.CustomProperties.ContainsKey("round"), "room property update through Steam");
        Assert.Equal(5, scenario.Guest.CurrentRoom.CustomProperties["round"]);
        Assert.Equal("ready", scenario.Guest.CurrentRoom.CustomProperties["state"]);
    }
}

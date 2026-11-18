using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ExitGames.Client.Photon;
using Photon.Realtime;
using RoundsP2P.Core;
using RoundsP2P.Network.Tests;
using RoundsP2P.Plugin;
using Steamworks;
using Xunit;

namespace RoundsP2P.Steam.Tests;

internal sealed class SteamPhotonScenario : IDisposable
{
    private readonly SteamRuntime runtime;
    private readonly LuxonServerProcess server;
    private readonly SteamDatagramTransport hostTransport;
    private readonly SteamDatagramTransport guestTransport;
    private readonly List<TestClient> clients = new();
    private readonly List<string> log = new();
    private readonly string artifacts;
    private bool disposed;

    public SteamPhotonScenario(SteamRuntime runtime, string testName)
    {
        this.runtime = runtime;
        int master = PhotonSession.FreeUdpPort();
        int game;
        do { game = PhotonSession.FreeUdpPort(); } while (game == master);
        string root = PhotonSession.FindRepositoryRoot();
        artifacts = Path.Combine(root, "artifacts", "steam-tests", testName + "-" + Guid.NewGuid().ToString("N"));
        var options = new PrototypeOptions(PrototypeRole.Host, "127.0.0.1", "127.0.0.1", "steam-test", master, game);
        server = new LuxonServerProcess(Path.Combine(root, ".cache", "Luxon", "v1.5", "luxon_server.exe"), artifacts, options, message => { lock (log) log.Add(message); });
        try
        {
            runtime.WaitUntil(() => server.IsListening, "actual Luxon listeners");
            SteamDatagramTransport.CreateConnectedPairForTest(master, game, message => { lock (log) log.Add(message); }, out hostTransport, out guestTransport);
            runtime.WaitUntil(() => hostTransport.IsConnected && guestTransport.IsConnected, "production Steam session handshake", () => { hostTransport.Pump(); guestTransport.Pump(); });
            Host = Connect("host", master, game);
            Assert.True(Host.OpCreateRoom(new EnterRoomParams
            {
                RoomName = "steam-test",
                RoomOptions = new RoomOptions { MaxPlayers = 2, IsOpen = true, IsVisible = false, PublishUserId = true }
            }));
            WaitUntil(() => Host.InRoom, "host room creation");
            Guest = Connect("guest", guestTransport.MasterPort, guestTransport.GamePort);
            Assert.True(Guest.OpJoinRoom(new EnterRoomParams { RoomName = "steam-test" }));
            WaitUntil(() => Guest.InRoom && Host.CurrentRoom.PlayerCount == 2, "guest room join through Steam tunnel");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public TestClient Host { get; } = null!;
    public TestClient Guest { get; } = null!;
    public int GuestGamePort => guestTransport.GamePort;

    private TestClient Connect(string name, int master, int game)
    {
        var client = new TestClient(name);
        clients.Add(client);
        client.LoadBalancingPeer.MaximumTransferUnit = 1000;
        client.ServerPortOverrides = new PhotonPortDefinition { MasterServerPort = (ushort)master, GameServerPort = (ushort)game };
        Assert.True(client.ConnectUsingSettings(new AppSettings
        {
            AppIdRealtime = "b540bd49-57b7-4b8d-92ca-ea3ce3858526", AppVersion = "steam-tunnel-test-v1",
            UseNameServer = false, Server = "127.0.0.1", Port = master,
            Protocol = ConnectionProtocol.Udp, EnableProtocolFallback = false, AuthMode = AuthModeOption.Auth,
            NetworkLogging = DebugLevel.INFO
        }));
        WaitUntil(() => client.State == ClientState.ConnectedToMasterServer, name + " reaches master server");
        return client;
    }

    public void WaitUntil(Func<bool> predicate, string expectation) => runtime.WaitUntil(predicate, expectation, Pump, 15000);

    private void Pump()
    {
        Assert.False(server.HasExited, "Luxon server unexpectedly exited.");
        foreach (TestClient client in clients) client.Service();
        hostTransport?.Pump();
        guestTransport?.Pump();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            foreach (TestClient client in clients) client.Disconnect();
            var timer = Stopwatch.StartNew();
            while (clients.Any(client => client.State != ClientState.Disconnected) && timer.ElapsedMilliseconds < 2000)
            {
                foreach (TestClient client in clients) client.Service();
                hostTransport?.Pump();
                guestTransport?.Pump();
                SteamAPI.RunCallbacks();
                Thread.Sleep(1);
            }
        }
        finally
        {
            hostTransport?.Dispose();
            guestTransport?.Dispose();
            server.Dispose();
            lock (log) File.WriteAllLines(Path.Combine(artifacts, "server-and-steam.log"), log);
            foreach (TestClient client in clients)
                File.WriteAllLines(Path.Combine(artifacts, client.Name + ".log"), client.Log);
        }
    }
}

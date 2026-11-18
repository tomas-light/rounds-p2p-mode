using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using ExitGames.Client.Photon;
using Photon.Realtime;
using RoundsP2P.Core;
using Xunit;

namespace RoundsP2P.Network.Tests;

// Serial execution is intentional: Photon keeps a process-wide custom-type registry.
[CollectionDefinition("Photon server", DisableParallelization = true)]
public sealed class PhotonServerCollection { }

internal sealed class PhotonSession : IDisposable
{
    private readonly LuxonServerProcess server;
    private readonly ConcurrentQueue<string> serverLog = new();
    private readonly List<TestClient> clients = new();
    private readonly string artifactDirectory;
    private readonly int masterPort;
    private bool disposed;

    public PhotonSession(string testName, bool throwingLogObserver = false)
    {
        string root = FindRepositoryRoot();
        string executable = Path.Combine(root, ".cache", "Luxon", "v1.5", "luxon_server.exe");
        Assert.True(File.Exists(executable), "Run scripts/prepare.ps1 first: the pinned Luxon v1.5 executable is required; integration tests are never silently skipped.");
        artifactDirectory = Path.Combine(root, "artifacts", "network-tests", testName + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDirectory);
        masterPort = FreeUdpPort();
        int gamePort;
        do { gamePort = FreeUdpPort(); } while (gamePort == masterPort);
        var options = new PrototypeOptions(PrototypeRole.Host, "127.0.0.1", "127.0.0.1", "network-test", masterPort, gamePort);
        server = new LuxonServerProcess(executable, artifactDirectory, options, message =>
        {
            serverLog.Enqueue(message);
            if (throwingLogObserver)
                throw new InvalidOperationException("Deliberately failing log observer.");
        });
        try
        {
            WaitUntil(() => server.IsListening, "both actual Luxon IPv4 UDP listeners");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int OwnedProcessId => server.ProcessId;
    public bool HasExited => server.HasExited;
    public bool IsListening => server.IsListening;

    public TestClient Connect(string name)
    {
        var client = new TestClient(name);
        clients.Add(client);
        Assert.True(client.ConnectUsingSettings(new AppSettings
        {
            AppIdRealtime = "b540bd49-57b7-4b8d-92ca-ea3ce3858526",
            AppVersion = "rounds-p2p-prototype-test-v1",
            UseNameServer = false,
            Server = "127.0.0.1",
            Port = masterPort,
            Protocol = ConnectionProtocol.Udp,
            EnableProtocolFallback = false,
            NetworkLogging = DebugLevel.INFO,
            AuthMode = AuthModeOption.Auth
        }));
        WaitUntil(() => client.State == ClientState.ConnectedToMasterServer, name + " connected to actual Luxon master");
        Assert.Equal("127.0.0.1:" + masterPort, client.CurrentServerAddress);
        return client;
    }

    public void CreateRoom(TestClient host, string room)
    {
        Assert.True(host.OpCreateRoom(new EnterRoomParams
        {
            RoomName = room,
            RoomOptions = new RoomOptions
            {
                MaxPlayers = 2,
                IsOpen = true,
                IsVisible = false,
                CleanupCacheOnLeave = true,
                PublishUserId = true,
                CustomRoomProperties = new Hashtable { { "prototype", 1 } }
            }
        }));
        WaitUntil(() => host.InRoom, "host creates actual two-player room");
    }

    public void JoinRoom(TestClient guest, string room)
    {
        Assert.True(guest.OpJoinRoom(new EnterRoomParams { RoomName = room }));
        WaitUntil(() => guest.InRoom, "guest joins actual room");
    }

    public void WaitUntil(Func<bool> predicate, string expectation, int timeoutMilliseconds = 15000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.False(server.HasExited, "Luxon unexpectedly exited. " + string.Join(Environment.NewLine, serverLog));
            foreach (TestClient client in clients)
                client.Service();
            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
                Assert.Fail("Timed out waiting for " + expectation + ". " + string.Join("; ", clients.Select(client => client.Name + ":" + client.State + "/" + client.DisconnectedCause)) + Environment.NewLine + string.Join(Environment.NewLine, clients.SelectMany(client => client.Log.TakeLast(10))));
            Thread.Sleep(5); // Service pump cadence, not a blind readiness/startup delay.
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        foreach (TestClient client in clients)
            client.Disconnect();
        var stopwatch = Stopwatch.StartNew();
        while (clients.Any(client => client.State != ClientState.Disconnected) && stopwatch.ElapsedMilliseconds < 2000)
        {
            foreach (TestClient client in clients)
                client.Service();
            Thread.Sleep(5);
        }
        server.Dispose();
        File.WriteAllLines(Path.Combine(artifactDirectory, "server.log"), serverLog);
        foreach (TestClient client in clients)
            File.WriteAllLines(Path.Combine(artifactDirectory, client.Name + ".log"), client.Log);
        disposed = true;
    }

    internal static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Cannot find repository root.");
    }
}

internal sealed class TestClient : LoadBalancingClient
{
    public TestClient(string name) : base(ConnectionProtocol.Udp)
    {
        Name = name;
        // Realtime's peer property has a private setter. Install a test peer that runs
        // the production wire adapter at the same outgoing-operation boundary as the
        // Unity plugin's scoped patch, while keeping the real Photon UDP implementation.
        typeof(LoadBalancingClient).GetProperty(nameof(LoadBalancingPeer), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(this, new CompatiblePeer(this));
        AuthValues = new AuthenticationValues("test-" + Guid.NewGuid().ToString("N"));
        NickName = name;
        StateChanged += (previous, current) => Log.Enqueue(Name + " state " + previous + " -> " + current);
        EventReceived += data => Events.Add(new ReceivedEvent(data.Code, data.Sender, data.CustomData));
        OpResponseReceived += response => Responses.Add(new ReceivedResponse(response.OperationCode, response.ReturnCode, response.DebugMessage));
    }

    public string Name { get; }
    public ConcurrentQueue<string> Log { get; } = new();
    public List<ReceivedEvent> Events { get; } = new();
    public List<ReceivedResponse> Responses { get; } = new();
    public override void DebugReturn(DebugLevel level, string message) => Log.Enqueue(Name + " " + level + ": " + message);

    private sealed class CompatiblePeer : LoadBalancingPeer
    {
        public CompatiblePeer(IPhotonPeerListener listener) : base(listener, ConnectionProtocol.Udp) { }

        public override bool SendOperation(byte operationCode, Dictionary<byte, object> operationParameters, SendOptions sendOptions)
        {
            if (operationCode == OperationCode.CreateGame || operationCode == OperationCode.JoinGame)
                PhotonWireCompatibility.NormalizeRoomOptions(operationParameters, usePropertiesKey: false);
            return base.SendOperation(operationCode, operationParameters, sendOptions);
        }
    }
}

internal sealed record ReceivedEvent(byte Code, int Sender, object? Content);
internal sealed record ReceivedResponse(byte Code, short ReturnCode, string? Message);

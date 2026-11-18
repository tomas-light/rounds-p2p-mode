using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using RoundsP2P.Core;
using UnityEngine;

namespace RoundsP2P.Plugin;

[BepInPlugin(Id, "ROUNDS P2P Prototype", "1.0.0")]
public sealed partial class Plugin : BaseUnityPlugin
{
    public const string Id = "local.rounds.p2p.prototype";
    // A separate namespace prevents accidental mixing with stock/cloud sessions.
    public const string PrototypeAppId = "5d9f49ea-424d-42a1-956a-dc65752e51ae";
    public const string ProtocolVersion = "rounds-p2p-prototype-0.1";
    internal static Plugin? Instance { get; private set; }

    private ConfigEntry<PrototypeRole> _role = null!;
    private ConfigEntry<string> _host = null!;
    private ConfigEntry<string> _advertised = null!;
    private ConfigEntry<string> _room = null!;
    private ConfigEntry<int> _masterPort = null!;
    private ConfigEntry<int> _gamePort = null!;
    private ConfigEntry<double> _timeout = null!;
    private ConfigEntry<bool> _autoConnect = null!;
    private LuxonServerProcess? _server;
    private PhotonPortDefinition _savedPorts;
    private AuthenticationValues? _savedAuth;
    private string? _savedAppId;
    private string? _savedVersion;
    private bool _active;
    private bool _connecting;
    private bool _panel;
    private bool _seenRoom;
    private bool _disconnectPending;
    private string? _operationError;
    private Coroutine? _connectionRoutine;
    private string _status = "F8: optional direct-IPv4 diagnostics; use Online for Steam lobbies.";
    private ClientState _lastState;
    private StreamWriter? _trace;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _traceLock = new object();
    private Harmony? _smokeHarmony;
    private Harmony? _wireHarmony;
    private readonly SandboxInputFont _sandboxInputFont = new SandboxInputFont();
    internal bool SmokeEnabled { get; private set; }
    internal bool Active => _active;

    private void Awake()
    {
        Instance = this;
        Application.runInBackground = true;
        _role = Config.Bind("Connection", "Role", PrototypeRole.Disabled, "Disabled, Host or Client. The default leaves stock networking alone.");
        _host = Config.Bind("Connection", "HostAddress", "127.0.0.1", "IPv4 of the PC running the server; host normally uses loopback.");
        _advertised = Config.Bind("Connection", "AdvertisedAddress", "127.0.0.1", "Host IPv4 reachable by the friend. This is NOT a bind-address/firewall restriction.");
        _room = Config.Bind("Connection", "RoomName", "rounds-prototype", "Same room on both PCs; ASCII letters/digits/-/_ only.");
        _masterPort = Config.Bind("Connection", "MasterPort", 5055, "UDP master port.");
        _gamePort = Config.Bind("Connection", "GamePort", 5056, "UDP game port.");
        _timeout = Config.Bind("Connection", "TimeoutSeconds", 30.0, "Connection/room operation timeout, 1..120 seconds.");
        _autoConnect = Config.Bind("Connection", "AutoConnect", false, "Connect automatically after the stock main menu is ready.");
        SmokeEnabled = Array.IndexOf(Environment.GetCommandLineArgs(), "--rounds-p2p-smoke") >= 0;
        string tracePath = CommandLineValue("--rounds-p2p-trace=") ?? Path.Combine(Paths.BepInExRootPath, "RoundsP2P.trace.log");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(tracePath))!);
        _trace = new StreamWriter(tracePath, false) { AutoFlush = true };
        _wireHarmony = new Harmony(Id + ".wire");
        InstallMatchHudHooks(_wireHarmony);
        InstallRematchHooks(_wireHarmony);
        _sandboxInputFont.Install(_wireHarmony);
        _wireHarmony.Patch(AccessTools.Method(typeof(LoadBalancingPeer), "RoomOptionsToOpParameters"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(NormalizeRoomOptions)));
        // Stock failure callbacks schedule a delayed scene restart. Our scoped
        // connection flow owns failure/retry and must not inherit that restart.
        foreach (string callback in new[] { "OnDisconnected", "OnJoinRoomFailed" })
            _wireHarmony.Patch(AccessTools.Method(typeof(NetworkConnectionHandler), callback),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(UseStockFailureHandling)));
        InstallSteamHooks(_wireHarmony);
        if (SmokeEnabled)
        {
            _smokeHarmony = new Harmony(Id + ".smoke");
            SmokeDriver.Install(_smokeHarmony);
            gameObject.AddComponent<SmokeDriver>();
        }
        WriteTrace("plugin_loaded", "unity=" + Application.unityVersion + ";smoke=" + SmokeEnabled);
        Logger.LogInfo("Standalone prototype loaded; no RWF/UnboundLib/DuctTape dependency. F8 opens connection controls.");
    }

    private IEnumerator Start()
    {
        while (NetworkConnectionHandler.instance == null || MainMenuHandler.instance == null || LoadingScreen.instance == null)
            yield return null;
        InitializeSteamFeatures();
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--rounds-p2p-sandbox-font-smoke") >= 0)
        {
            int.TryParse(CommandLineValue("--rounds-p2p-steam-ui-hold="), out int holdSeconds);
            yield return SandboxFontSmoke.Run(this, Math.Max(0, Math.Min(120, holdSeconds)));
            yield break;
        }
        string? roleText = CommandLineValue("--rounds-p2p-role=");
        if (roleText != null || _autoConnect.Value)
        {
            PrototypeRole role;
            try
            {
                role = roleText == null ? _role.Value : (PrototypeRole)Enum.Parse(typeof(PrototypeRole), roleText, true);
            }
            catch (Exception exception) { Fail(exception.Message); yield break; }
            if (SmokeEnabled && Array.IndexOf(Environment.GetCommandLineArgs(), "--rounds-p2p-cancel-retry-smoke") >= 0)
            {
                // Cancel a real host startup/connection, including its owned server,
                // then start the actual host. No stale coroutine may survive.
                try { Begin(PrototypeRole.Host); }
                catch (Exception exception) { Fail(exception.Message); yield break; }
                yield return null;
                StopSession();
                double deadline = _clock.Elapsed.TotalSeconds + 5;
                while (!PhotonStopped() && _clock.Elapsed.TotalSeconds < deadline)
                    yield return null;
                if (!PhotonStopped() || _connectionRoutine != null ||
                    PhotonNetwork.NetworkingClient.AppId != _savedAppId || PhotonNetwork.AuthValues != _savedAuth)
                {
                    WriteTrace("smoke_failed", "cancel_retry_did_not_restore_settings;state=" + PhotonNetwork.NetworkClientState +
                        ";peer=" + PhotonNetwork.NetworkingClient.LoadBalancingPeer.PeerState + ";routine_stopped=" + (_connectionRoutine == null) +
                        ";app_restored=" + (PhotonNetwork.NetworkingClient.AppId == _savedAppId) + ";auth_restored=" + (PhotonNetwork.AuthValues == _savedAuth));
                    Application.Quit(2);
                    yield break;
                }
                WriteTrace("smoke_cancel_retry_complete", "pending_attempt_cancelled;settings_restored");
            }
            try { Begin(role); }
            catch (Exception exception) { Fail(exception.Message); }
        }
    }

    private static bool PhotonStopped() => PhotonNetwork.NetworkClientState == ClientState.Disconnected ||
                                           PhotonNetwork.NetworkClientState == ClientState.PeerCreated;

    private static string? CommandLineValue(string prefix)
    {
        foreach (string argument in Environment.GetCommandLineArgs())
            if (argument.StartsWith(prefix, StringComparison.Ordinal)) return argument.Substring(prefix.Length);
        return null;
    }

    private static void NormalizeRoomOptions(Dictionary<byte, object> op, bool usePropertiesKey)
    {
        if (Instance != null && Instance.Active)
            PhotonWireCompatibility.NormalizeRoomOptions(op, usePropertiesKey);
    }

    private static bool UseStockFailureHandling() => Instance == null || !Instance.Active;

    private void OnOperationResponse(OperationResponse response)
    {
        if (_active && response.ReturnCode != 0 &&
            (response.OperationCode == OperationCode.CreateGame || response.OperationCode == OperationCode.JoinGame))
            _operationError = "Room operation failed (" + response.ReturnCode + "): " + response.DebugMessage;
    }

    private PrototypeOptions Options(PrototypeRole role) => new PrototypeOptions(
        role, CommandLineValue("--rounds-p2p-host=") ?? _host.Value,
        CommandLineValue("--rounds-p2p-advertised=") ?? _advertised.Value,
        CommandLineValue("--rounds-p2p-room=") ?? _room.Value,
        int.Parse(CommandLineValue("--rounds-p2p-master-port=") ?? _masterPort.Value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        int.Parse(CommandLineValue("--rounds-p2p-game-port=") ?? _gamePort.Value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), _timeout.Value);

    private void Begin(PrototypeRole role)
    {
        if (role == PrototypeRole.Disabled) return;
        if (_active || PhotonNetwork.IsConnected || (GameManager.instance != null && GameManager.instance.isPlaying))
            throw new InvalidOperationException("Leave the current game/connection before starting the prototype.");
        if (NetworkConnectionHandler.instance == null || LoadingScreen.instance == null)
            throw new InvalidOperationException("Wait for the main menu to finish loading.");
        PrototypeOptions options = _steamOptions ?? Options(role);
        _savedPorts = PhotonNetwork.ServerPortOverrides;
        _savedAuth = PhotonNetwork.AuthValues;
        _savedAppId = PhotonNetwork.NetworkingClient.AppId;
        _savedVersion = PhotonNetwork.GameVersion;
        _savedMtu = PhotonNetwork.NetworkingClient.LoadBalancingPeer.MaximumTransferUnit;
        _active = true;
        _matchScore = _usingSteam || _hudSmoke ? new LobbyMatchScore() : null;
        _rematchNumber = 0;
        _rematchArmed = false;
        _rematchPreparing = false;
        _seenRoom = false;
        _connecting = true;
        _operationError = null;
        PhotonNetwork.NetworkingClient.OpResponseReceived += OnOperationResponse;
        _panel = false;
        try
        {
            if (role == PrototypeRole.Host)
            {
                string pluginDirectory = Path.GetDirectoryName(Info.Location)!;
                string workingDirectory = Path.Combine(Paths.CachePath, "RoundsP2P", Guid.NewGuid().ToString("N"));
                _server = new LuxonServerProcess(Path.Combine(pluginDirectory, "luxon", "luxon_server.exe"), workingDirectory, options,
                    message => Logger.LogInfo("[Luxon] " + message));
                WriteTrace("server_started", "pid=" + _server.ProcessId);
            }
            _connectionRoutine = StartCoroutine(Connect(options));
        }
        catch (Exception exception) { Fail(exception.Message); }
    }

    private IEnumerator Connect(PrototypeOptions options)
    {
        double deadline = _clock.Elapsed.TotalSeconds + options.TimeoutSeconds;
        while (_active && _server != null && !_server.IsListening)
        {
            if (_server.HasExited) { Fail("The hosted server exited before binding its UDP ports."); yield break; }
            if (_clock.Elapsed.TotalSeconds >= deadline) { Fail("Timed out waiting for the hosted server to listen."); yield break; }
            yield return null;
        }
        if (!_active) yield break;
        if (_server != null) WriteTrace("server_listening", "master=" + options.MasterPort + ";game=" + options.GamePort);
        // The stock handler sets alternative cloud UDP ports in Awake. Override only for this session.
        PhotonNetwork.ServerPortOverrides = new PhotonPortDefinition
        {
            MasterServerPort = (ushort)options.MasterPort,
            GameServerPort = (ushort)options.GamePort
        };
        PhotonNetwork.AuthValues = new AuthenticationValues(Guid.NewGuid().ToString("N"));
        if (_usingSteam) PhotonNetwork.NetworkingClient.LoadBalancingPeer.MaximumTransferUnit = 1000;
        var settings = new AppSettings
        {
            AppIdRealtime = PrototypeAppId, AppVersion = ProtocolVersion,
            UseNameServer = false, Server = options.HostAddress, Port = options.MasterPort,
            Protocol = ConnectionProtocol.Udp, EnableProtocolFallback = false,
            AuthMode = AuthModeOption.Auth, EnableLobbyStatistics = false
        };
        _status = "Connecting to " + options.HostAddress + ":" + options.MasterPort;
        if (!PhotonNetwork.ConnectUsingSettings(settings)) { Fail("Photon rejected direct-master connection."); yield break; }
        WriteTrace("connect_started", _status);
        deadline = _clock.Elapsed.TotalSeconds + options.TimeoutSeconds;
        while (_active && PhotonNetwork.NetworkClientState != ClientState.ConnectedToMasterServer)
        {
            if (PhotonNetwork.NetworkClientState == ClientState.Disconnected) { Fail("Connection failed: " + PhotonNetwork.NetworkingClient.DisconnectedCause); yield break; }
            if (_clock.Elapsed.TotalSeconds >= deadline) { Fail("Timed out connecting to the local master server."); yield break; }
            if (_server != null && _server.HasExited) { Fail("The hosted server exited during connection."); yield break; }
            yield return null;
        }
        if (!_active) yield break;

        // Mark this as private hosting on both sides to disable the stock region-search watchdog.
        var searchingType = AccessTools.Field(typeof(NetworkConnectionHandler), "m_searchingType");
        searchingType.SetValue(NetworkConnectionHandler.instance, Enum.Parse(searchingType.FieldType, "HostRoom"));
        if (!_usingSteam) ShowMatchLoading();
        var roomOptions = new RoomOptions
        {
            MaxPlayers = 2, IsOpen = true, IsVisible = false,
            CustomRoomProperties = new ExitGames.Client.Photon.Hashtable { { NetworkConnectionHandler.ROOM_CODE, options.RoomName } }
        };
        bool accepted = options.Role == PrototypeRole.Host
            ? PhotonNetwork.CreateRoom(options.RoomName, roomOptions, TypedLobby.Default)
            : PhotonNetwork.JoinRoom(options.RoomName);
        if (!accepted) { Fail("Photon rejected the create/join operation."); yield break; }
        _status = "Waiting to join room " + options.RoomName;
        deadline = _clock.Elapsed.TotalSeconds + options.TimeoutSeconds;
        while (_active && !PhotonNetwork.InRoom)
        {
            if (_operationError != null) { Fail(_operationError); yield break; }
            if (_clock.Elapsed.TotalSeconds >= deadline) { Fail("Timed out joining room; check host address, ports and room name."); yield break; }
            if (PhotonNetwork.NetworkClientState == ClientState.Disconnected) { Fail("Disconnected while joining the room."); yield break; }
            yield return null;
        }
        if (!_active) yield break;
        _connecting = false;
        _connectionRoutine = null;
        _seenRoom = true;
        _status = "Room joined; waiting for the second player (press Space when the stock game asks).";
        WriteTrace("room_joined", "actor=" + PhotonNetwork.LocalPlayer.ActorNumber + ";server=" + PhotonNetwork.ServerAddress);
        if (_usingSteam) _steam?.OnPhotonRoomReady();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8)) _panel = !_panel;
        PumpStoppedPhoton();
        UpdateSteamFeatures();
        if (!_active) return;
        ClientState state = PhotonNetwork.NetworkClientState;
        if (state != _lastState)
        {
            _lastState = state;
            WriteTrace("photon_state", state + ";server=" + PhotonNetwork.ServerAddress);
        }
        if (!_connecting && _seenRoom && !PhotonNetwork.InRoom)
        {
            _status = "Session ended; stock network settings restored.";
            StopSession();
        }
        else if (_server != null && _server.HasExited) Fail("The hosted server exited.");
    }

    private void OnGUI()
    {
        if (!_panel) return;
        GUILayout.BeginArea(new Rect(20, 90, 580, 320), GUI.skin.box);
        GUILayout.Label("ROUNDS P2P — IPv4 diagnostics 0.2 (F8)");
        GUILayout.Label(_status);
        if (!_active)
        {
            GUILayout.Label("Server IPv4 (client):"); _host.Value = GUILayout.TextField(_host.Value);
            GUILayout.Label("Host IPv4 reachable by friend (host advertises it):"); _advertised.Value = GUILayout.TextField(_advertised.Value);
            GUILayout.Label("Room name:"); _room.Value = GUILayout.TextField(_room.Value);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host on this PC")) { try { Begin(PrototypeRole.Host); } catch (Exception exception) { Fail(exception.Message); } }
            if (GUILayout.Button("Join host")) { try { Begin(PrototypeRole.Client); } catch (Exception exception) { Fail(exception.Message); } }
            GUILayout.EndHorizontal();
        }
        else
        {
            GUILayout.Label("Photon: " + PhotonNetwork.NetworkClientState + "; players: " + (PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom.PlayerCount : 0));
            GUILayout.Label("UDP server RTT: " + PhotonNetwork.GetPing() + " ms (not peer RTT)");
            if (GUILayout.Button("Disconnect prototype"))
            {
                StopSession();
                NetworkConnectionHandler.instance.NetworkRestart();
            }
        }
        GUILayout.EndArea();
    }

    private void Fail(string message)
    {
        _status = "ERROR: " + message;
        _panel = true;
        Logger.LogError(_status);
        WriteTrace("error", message);
        StopSession();
        if (LoadingScreen.instance != null && LoadingScreen.instance.IsLoading)
            NetworkConnectionHandler.instance?.NetworkRestart();
    }

    private void StopSession()
    {
        if (!_active) return;
        _active = false;
        _matchScore = null;
        _mapBeforeRematch = null;
        _rematchArmed = false;
        _rematchPreparing = false;
        _matchHud.Hide();
        _connecting = false;
        PhotonNetwork.NetworkingClient.OpResponseReceived -= OnOperationResponse;
        if (_connectionRoutine != null) { StopCoroutine(_connectionRoutine); _connectionRoutine = null; }
        try
        {
            try { if (PhotonNetwork.IsConnected) PhotonNetwork.Disconnect(); }
            finally { _server?.Dispose(); }
        }
        catch (Exception exception)
        {
            Logger.LogError("Prototype cleanup: " + exception.Message);
            WriteTrace("cleanup_error", exception.Message);
        }
        finally
        {
            _server = null;
            PhotonNetwork.ServerPortOverrides = _savedPorts;
            PhotonNetwork.AuthValues = _savedAuth;
            PhotonNetwork.NetworkingClient.AppId = _savedAppId;
            PhotonNetwork.GameVersion = _savedVersion;
            PhotonNetwork.NetworkingClient.LoadBalancingPeer.MaximumTransferUnit = _savedMtu;
            _disconnectPending = PhotonNetwork.NetworkClientState == ClientState.Disconnecting;
            bool wasSteam = _usingSteam;
            _usingSteam = false;
            _steamOptions = null;
            WriteTrace("session_stopped", "settings_restored");
            if (wasSteam) _steam?.OnPhotonStopped();
        }
    }

    private void PumpStoppedPhoton()
    {
        if (!_disconnectPending || _active) return;
        var client = PhotonNetwork.NetworkingClient;
        // An early cancellation can leave a real disconnect notification queued.
        // The UDP peer is already stopped; drain that notification so the
        // Realtime client can finish transitioning before the next attempt.
        if (client.LoadBalancingPeer.PeerState == PeerStateValue.Disconnected && client.State == ClientState.Disconnecting)
            for (int count = 0; count < 64 && client.LoadBalancingPeer.DispatchIncomingCommands(); count++) { }
        _disconnectPending = client.State == ClientState.Disconnecting;
    }

    internal void WriteTrace(string name, string detail)
    {
        lock (_traceLock)
            _trace?.WriteLine(_clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) + "\t" + name + "\t" + detail.Replace('\r', ' ').Replace('\n', ' '));
    }

    private void OnApplicationQuit() => StopSession();
    private void OnDestroy()
    {
        StopSession();
        _steam?.Dispose();
        _steamMenu?.Dispose();
        _matchHud.Dispose();
        _sandboxInputFont.Dispose();
        _smokeHarmony?.UnpatchSelf();
        _wireHarmony?.UnpatchSelf();
        lock (_traceLock) { _trace?.Dispose(); _trace = null; }
        Instance = null;
    }
}

using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Landfall.Network;
using Photon.Pun;
using RoundsP2P.Core;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace RoundsP2P.Plugin;

public sealed partial class Plugin
{
    private SteamLobbySession? _steam;
    private NativeSteamMenu? _steamMenu;
    private PrototypeOptions? _steamOptions;
    private bool _usingSteam;
    private bool _steamPlayerJoined;
    private int _savedMtu;
    private string _steamUnavailable = "Создайте лобби, чтобы пригласить друга через Steam.";
    internal string SteamStatus => _steam?.Status ?? _steamUnavailable;
    internal bool HasSteamLobby => _steam?.HasLobby == true;
    internal bool CanInviteSteamFriend => _steam?.CanInvite == true;

    private static void InstallSteamHooks(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(MainMenuHandler), "PlayOnlineMultiplayer"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(OpenSteamOnline)));
        harmony.Patch(AccessTools.Method(typeof(CheckUserPrivilegeButton), nameof(CheckUserPrivilegeButton.TestPrivilege)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(OpenSteamOnlineFromMainMenu)));
        foreach (string action in new[] { nameof(NetworkConnectionHandler.HostPrivateRoom), nameof(NetworkConnectionHandler.HostPrivateAndInviteFriend) })
            harmony.Patch(AccessTools.Method(typeof(NetworkConnectionHandler), action),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(CreateSteamLobbyFromStockRoom)));
        harmony.Patch(AccessTools.Method(typeof(ClientSteamLobby), "OnLobbyJoinRequest"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(HandleSteamInvite)));
        harmony.Patch(AccessTools.Method(typeof(NetworkConnectionHandler), "RPCA_FoundGame"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(PrepareSteamMatchLoading)));
    }

    private void InitializeSteamFeatures()
    {
        _steamMenu = new NativeSteamMenu(this);
        if (!SteamManager.Initialized)
        {
            _steamUnavailable = "Steam недоступен. Запустите Steam и войдите в аккаунт.";
            return;
        }
        _steam = new SteamLobbySession(this, GameFingerprint());
        string? launchLobby = null;
        string[] arguments = Environment.GetCommandLineArgs();
        for (int index = 0; index + 1 < arguments.Length; index++)
            if (arguments[index] == "+connect_lobby") launchLobby = arguments[index + 1];
        SteamApps.GetLaunchCommandLine(out string steamArguments, 2048);
        var match = System.Text.RegularExpressions.Regex.Match(steamArguments ?? string.Empty, @"(?:^|\s)\+connect_lobby\s+(\d+)");
        if (match.Success) launchLobby = match.Groups[1].Value;
        if (ulong.TryParse(launchLobby, out ulong lobby)) _steam.ReceiveInvite(new CSteamID(lobby), null);
        if (Array.IndexOf(arguments, "--rounds-p2p-steam-ui-smoke") >= 0) StartCoroutine(SteamUiSmoke());
    }

    private static bool OpenSteamOnline()
    {
        if (Instance == null) return true;
        try { Instance._steamMenu?.OpenOnline(); }
        catch (Exception exception) { Instance.ShowSteamFailure(exception.Message); }
        return false;
    }

    private static bool OpenSteamOnlineFromMainMenu(CheckUserPrivilegeButton __instance)
    {
        var mainPage = MainMenuHandler.instance?.MainMenu;
        if (mainPage == null || !__instance.transform.IsChildOf(mainPage.transform)) return true;
        Instance?.WriteTrace("steam_online_routed", "main_menu_privilege_button");
        return OpenSteamOnline();
    }

    private static bool CreateSteamLobbyFromStockRoom()
    {
        if (Instance?._steamMenu == null) return true;
        Instance.CreateSteamLobby();
        Instance._steamMenu.ShowLobby();
        return false;
    }

    private static bool HandleSteamInvite(ClientSteamLobby __instance, GameLobbyJoinRequested_t param)
    {
        Plugin? plugin = Instance;
        if (plugin?._steam == null) return true;
        CSteamID lobby = param.m_steamIDLobby;
        plugin._steam.ReceiveInvite(lobby, () => AccessTools.Method(typeof(ClientSteamLobby), "JoinLobby").Invoke(__instance, new object[] { lobby }));
        return false;
    }

    private static void PrepareSteamMatchLoading()
    {
        if (Instance?._usingSteam == true) Instance.ShowMatchLoading();
    }

    private void ShowMatchLoading()
    {
        CharacterCreatorHandler.instance?.CloseMenus();
        MainMenuHandler.instance.Close();
        TimeHandler.instance.gameStartTime = 1f;
        LoadingScreen.instance.StartLoading(privateGame: true);
    }

    internal void CreateSteamLobby()
    {
        try
        {
            if (_steam == null) throw new InvalidOperationException(_steamUnavailable);
            _steam.Create();
        }
        catch (Exception exception) { ShowSteamFailure(exception.Message); }
    }

    internal void InviteSteamFriend()
    {
        try { _steam?.Invite(); }
        catch (Exception exception) { ShowSteamFailure(exception.Message); }
    }

    internal void LeaveSteamLobby()
    {
        _steam?.Leave();
        StopSession();
        if (GameManager.instance != null && GameManager.instance.isPlaying) NetworkConnectionHandler.instance.NetworkRestart();
    }

    internal void ShowSteamFailure(string message)
    {
        _steamUnavailable = message;
        WriteTrace("steam_error", message);
        Logger.LogError(message);
        _steamMenu?.Refresh();
    }

    internal PrototypeOptions StartSteamHost(string room)
    {
        int master = FreeUdpPort();
        int game;
        do { game = FreeUdpPort(); } while (game == master);
        var options = new PrototypeOptions(PrototypeRole.Host, "127.0.0.1", "127.0.0.1", room, master, game, 40);
        BeginSteam(options);
        return options;
    }

    internal void StartSteamClient(int master, int game, string room) =>
        BeginSteam(new PrototypeOptions(PrototypeRole.Client, "127.0.0.1", "127.0.0.1", room, master, game, 40));

    private void BeginSteam(PrototypeOptions options)
    {
        _usingSteam = true;
        _steamPlayerJoined = false;
        _steamOptions = options;
        Begin(options.Role);
        if (!_active) throw new InvalidOperationException("Не удалось подготовить игровую сессию.");
    }

    internal void StopSteamPhoton()
    {
        StopSession();
        if (GameManager.instance != null && GameManager.instance.isPlaying)
            NetworkConnectionHandler.instance.NetworkRestart();
    }
    internal void ShowSteamLobbyPage() => _steamMenu?.ShowLobby();
    internal void SimulateSteamFailureForSmoke()
    {
        if (!SmokeEnabled || _steam == null) throw new InvalidOperationException("Steam failure smoke requires the explicit test driver and a running Steam client.");
        _steam.Fail("Тестовое завершение соединения.");
    }

    private void UpdateSteamFeatures()
    {
        try
        {
            _steam?.Pump();
            _steamMenu?.Refresh();
            if (_usingSteam && !_steamPlayerJoined && !SmokeEnabled && PhotonNetwork.InRoom &&
                PhotonNetwork.CurrentRoom.PlayerCount == 2 && GM_ArmsRace.instance != null &&
                GM_ArmsRace.instance.gameObject.activeInHierarchy && LoadingScreen.instance != null && !LoadingScreen.instance.IsLoading)
            {
                _steamPlayerJoined = true;
                PlayerAssigner.instance.CreatePlayer(null, false);
                WriteTrace("steam_player_joined", "stock_player_assignment");
            }
        }
        catch (Exception exception) { _steam?.Fail(exception.Message); }
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static string GameFingerprint()
    {
        var text = new StringBuilder();
        using var sha = SHA256.Create();
        foreach (string name in new[] { "Assembly-CSharp.dll", "Photon3Unity3D.dll", "PhotonRealtime.dll", "PhotonUnityNetworking.dll" })
        {
            using var file = File.OpenRead(Path.Combine(Application.dataPath, "Managed", name));
            text.Append(name).Append(':').Append(BitConverter.ToString(sha.ComputeHash(file)));
        }
        return BitConverter.ToString(sha.ComputeHash(Encoding.ASCII.GetBytes(text.ToString()))).Replace("-", string.Empty).ToLowerInvariant();
    }

    private IEnumerator SteamUiSmoke()
    {
        while (ListMenu.instance == null || !ListMenu.instance.IsInitialized)
            yield return null;
        var mainMenu = MainMenuHandler.instance.MainMenu;
        var online = mainMenu.GetComponentInChildren<CheckUserPrivilegeButton>(true)?.GetComponent<Button>();
        if (online == null || !online.interactable)
        {
            FailSteamUiSmoke("Missing stock Online button.");
            yield break;
        }
        UnityEngine.Object.FindObjectOfType<SkipIntro>(true)?.Skip();
        mainMenu.Show();
        online.onClick.Invoke();
        yield return null;
        if (!_steamMenu!.TryClickCreateLobbyForSmoke())
        {
            FailSteamUiSmoke("Stock Online button did not open the mod's Online page.");
            yield break;
        }
        WriteTrace("steam_ui_stock_online_verified", "clicked_native_online_and_create_lobby_buttons");
        double deadline = _clock.Elapsed.TotalSeconds + 45;
        while (_steam != null && !_steam.CanInvite && _clock.Elapsed.TotalSeconds < deadline) yield return null;
        if (_steam?.CanInvite != true) { WriteTrace("steam_ui_smoke_failed", SteamStatus); Application.Quit(2); yield break; }
        WriteTrace("steam_ui_smoke_ready", "lobby_created;host_photon_ready;invite_available;no_invites_sent");
        float hold = float.TryParse(CommandLineValue("--rounds-p2p-steam-ui-hold="), out float requested) ? Math.Max(0, Math.Min(120, requested)) : 15;
        yield return new WaitForSecondsRealtime(hold);
        LeaveSteamLobby();
        WriteTrace("steam_ui_smoke_complete", "lobby_and_host_cleaned");
        Application.Quit(0);
    }

    private void FailSteamUiSmoke(string reason)
    {
        WriteTrace("steam_ui_smoke_failed", reason);
        Application.Quit(2);
    }
}

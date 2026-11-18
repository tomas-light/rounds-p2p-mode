using System;
using System.Collections.Generic;
using System.Reflection;
using FriendlyFoe.Platform;
using HarmonyLib;
using Photon.Pun;
using TMPro;
using UnityEngine;

namespace RoundsP2P.Plugin;

// Installed only with --rounds-p2p-smoke. Drives normal join/card/damage paths,
// without writing match scores, changing rules or recording account stats.
internal sealed class SmokeDriver : MonoBehaviour
{
    private bool _joined;
    private bool _finished;
    private float _lastDamage;
    private float _started;
    private float _lastReport;
    private int _lastScore = -1;
    private bool _capacityVerified;
    private bool _initialHudVerified;
    private bool _matchEnded;
    private bool _rematchRequested;
    private bool _replayingDelayedLoad;
    private int _rematchMapRequests;
    private static readonly bool TestHud = Array.IndexOf(Environment.GetCommandLineArgs(), "--rounds-p2p-hud-smoke") >= 0;
    private readonly FieldInfo _cards = AccessTools.Field(typeof(CardChoice), "spawnedCards");
    private readonly FieldInfo _pickingAnimation = AccessTools.Field(typeof(CardChoice), "isPlaying");

    internal static void Install(Harmony harmony)
    {
        var noStats = new HarmonyMethod(typeof(SmokeDriver), nameof(SuppressStats));
        foreach (string name in new[] { "UnlockAchievement", "ProgressAchievement", "ClearAchievementsProgress" })
            harmony.Patch(AccessTools.Method(typeof(PlatformManager), name), prefix: noStats);
        foreach (MethodInfo method in typeof(Steamworks.SteamUserStats).GetMethods(BindingFlags.Static | BindingFlags.Public))
            if (method.ReturnType == typeof(bool) && Array.IndexOf(new[]
                { "SetAchievement", "SetStat", "UpdateAvgRateStat", "ClearAchievement", "ResetAllStats", "StoreStats", "IndicateAchievementProgress" }, method.Name) >= 0)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(SmokeDriver), nameof(SuppressSteamWrite)));
        harmony.Patch(AccessTools.Method(typeof(GM_ArmsRace), "GameOver"), postfix: new HarmonyMethod(typeof(SmokeDriver), nameof(OnGameOver)));
        if (TestHud)
        {
            harmony.Patch(AccessTools.Method(typeof(GM_ArmsRace), "GameOverRematch"), postfix: new HarmonyMethod(typeof(SmokeDriver), nameof(OnRematchPrompt)));
            harmony.Patch(AccessTools.Method(typeof(MapManager), nameof(MapManager.RPCA_LoadLevel)),
                prefix: new HarmonyMethod(typeof(SmokeDriver), nameof(DelayRematchMapOnGuest)));
        }
    }

    private static bool SuppressStats() => false;
    private static bool SuppressSteamWrite(ref bool __result) { __result = false; return false; }

    private void Start()
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--rounds-p2p-mute") >= 0)
            AudioListener.volume = 0f;
        _started = Time.realtimeSinceStartup;
        Plugin.Instance!.WriteTrace("smoke_started", "steam_stat_writes_suppressed;normal_rules_2_points_5_rounds");
    }

    private void Update()
    {
        if (_finished) return;
        Plugin? plugin = Plugin.Instance;
        if (plugin == null) return;
        if (Time.realtimeSinceStartup - _started > 300f)
        {
            plugin.WriteTrace("smoke_failed", "timeout"); _finished = true; Application.Quit(2); return;
        }
        if (Time.realtimeSinceStartup - _lastReport > 10f)
        {
            _lastReport = Time.realtimeSinceStartup;
            plugin.WriteTrace("smoke_progress", "room=" + PhotonNetwork.InRoom + ";loading=" + (LoadingScreen.instance != null && LoadingScreen.instance.IsLoading) +
                ";players=" + (PlayerManager.instance == null ? -1 : PlayerManager.instance.players.Count));
        }
        if (!plugin.Active || !PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom.PlayerCount != 2) return;
        if (!_capacityVerified)
        {
            if (PhotonNetwork.CurrentRoom.MaxPlayers != 2)
            {
                plugin.WriteTrace("smoke_failed", "wire_patch_did_not_enforce_max_players_2");
                _finished = true; Application.Quit(2); return;
            }
            _capacityVerified = true;
            plugin.WriteTrace("smoke_capacity_verified", "max_players=2");
        }
        GM_ArmsRace game = GM_ArmsRace.instance;
        LoadingScreen? loading = LoadingScreen.instance;
        PlayerManager? manager = PlayerManager.instance;
        if (game == null || loading == null || manager == null || !game.gameObject.activeInHierarchy || loading.IsLoading) return;
        if (!_joined)
        {
            _joined = true; PlayerAssigner.instance.CreatePlayer(null, false);
            plugin.WriteTrace("smoke_player_created", "actor=" + PhotonNetwork.LocalPlayer.ActorNumber);
        }
        CardChoice choice = CardChoice.instance;
        if (choice != null && choice.IsPicking && choice.pickrID >= 0 && !(bool)_pickingAnimation.GetValue(choice))
        {
            if (_rematchRequested && !RematchPromptClosed())
            {
                plugin.WriteTrace("smoke_failed", "rematch_prompt_visible_during_card_pick");
                _finished = true;
                Application.Quit(2);
                return;
            }
            Player? picker = manager.players.Find(player => player.PlayerID == choice.pickrID);
            var cards = (List<GameObject>)_cards.GetValue(choice);
            if (picker != null && picker.data.view.IsMine && cards.Count > 0)
            {
                int pickerId = choice.pickrID; choice.Pick(cards[0], false); choice.pickrID = -1;
                plugin.WriteTrace("smoke_card_picked", "player=" + pickerId);
            }
        }
        int score = game.p1Rounds * 1000 + game.p2Rounds * 100 + game.p1Points * 10 + game.p2Points;
        if (score != _lastScore)
        {
            _lastScore = score;
            plugin.WriteTrace("smoke_score", "rounds=" + game.p1Rounds + ":" + game.p2Rounds + ";points=" + game.p1Points + ":" + game.p2Points);
        }
        if (!GameManager.instance.battleOngoing || Time.realtimeSinceStartup - _lastDamage < 2f) return;
        int orangeTeam = OrangeTeam();
        if (TestHud)
        {
            if (_rematchRequested)
            {
                if (!VerifyHud(orangeTeam == 0 ? 1 : 0, orangeTeam == 1 ? 1 : 0)) return;
                if (_rematchMapRequests != 1 || MapManager.instance.currentMap?.Map.hasEntered != true)
                {
                    plugin.WriteTrace("smoke_failed", $"rematch_map_requests={_rematchMapRequests};expected=1;map_must_be_entered");
                    _finished = true;
                    Application.Quit(2);
                    return;
                }
                _finished = true;
                StartCoroutine(VerifyRematchStaysPlayable(plugin, game, MapManager.instance.currentMap));
                return;
            }
            if (!_initialHudVerified)
            {
                if (!VerifyHud(0, 0)) return;
                _initialHudVerified = true;
                plugin.WriteTrace("smoke_initial_hud_verified", "orange=0;blue=0;ping_placeholder_visible");
            }
        }
        if (PhotonNetwork.IsMasterClient && Array.IndexOf(Environment.GetCommandLineArgs(), "--rounds-p2p-steam-failure-smoke") >= 0)
        {
            _finished = true;
            plugin.WriteTrace("smoke_steam_failure_injected", "battle_was_ongoing");
            plugin.SimulateSteamFailureForSmoke();
            plugin.StartCoroutine(VerifyFailureRecovery(plugin));
            return;
        }
        if (_matchEnded) return;
        int losingTeam = TestHud ? 1 - orangeTeam : 1;
        foreach (Player player in manager.players)
        {
            if (player.TeamID != losingTeam || !player.data.view.IsMine || !player.data.isPlaying || player.data.dead) continue;
            _lastDamage = Time.realtimeSinceStartup;
            Player? opponent = manager.players.Find(candidate => candidate.TeamID != losingTeam);
            if (opponent == null) continue;
            player.data.healthHandler.CallTakeDamage(new Vector2(0, 1e9f), player.transform.position, null, opponent, true, HealthHandler.DamageSource.OutOfBounds);
            plugin.WriteTrace("smoke_damage_sent", "losing_team=" + losingTeam);
        }
    }

    private static void OnGameOver(GM_ArmsRace __instance, int winningTeamID)
    {
        Plugin? plugin = Plugin.Instance;
        if (plugin == null || !plugin.SmokeEnabled || !plugin.Active) return;
        bool valid = __instance.roundsToWinGame == 5 &&
            ((winningTeamID == 0 && __instance.p1Rounds == 5) || (winningTeamID == 1 && __instance.p2Rounds == 5));
        plugin.WriteTrace(valid ? "smoke_match_complete" : "smoke_failed", "winner=" + winningTeamID +
            ";rounds=" + __instance.p1Rounds + ":" + __instance.p2Rounds + ";limit=" + __instance.roundsToWinGame);
        var driver = plugin.GetComponent<SmokeDriver>();
        if (TestHud && valid)
        {
            driver._matchEnded = true;
            return;
        }
        driver._finished = true;
        plugin.StartCoroutine(QuitAfterDelivery(valid ? 0 : 2));
    }

    private static void OnRematchPrompt(int winningTeamID)
    {
        var plugin = Plugin.Instance!;
        var driver = plugin.GetComponent<SmokeDriver>();
        if (winningTeamID != OrangeTeam() || !VerifyHud(0, 0))
        {
            plugin.WriteTrace("smoke_failed", "orange_win_was_not_pending_before_rematch");
            driver._finished = true;
            Application.Quit(2);
            return;
        }
        driver.StartCoroutine(driver.AcceptRematch(plugin));
    }

    private System.Collections.IEnumerator AcceptRematch(Plugin plugin)
    {
        yield return new WaitForSecondsRealtime(0.75f);
        var prompt = UIHandler.instance.popUpHandler;
        if (!prompt.isPicking || RematchPromptClosed())
        {
            plugin.WriteTrace("smoke_failed", "rematch_prompt_did_not_open");
            _finished = true;
            Application.Quit(2);
            yield break;
        }
        _rematchRequested = true;
        AccessTools.Field(typeof(PopUpHandler), "currentYesNo").SetValue(prompt, PopUpHandler.YesNo.Yes);
        AccessTools.Method(typeof(PopUpHandler), "DonePicking").Invoke(prompt, null);
        if (!RematchPromptClosed())
        {
            plugin.WriteTrace("smoke_failed", "rematch_prompt_stayed_visible_after_confirmation");
            _finished = true;
            Application.Quit(2);
            yield break;
        }
        plugin.WriteTrace("smoke_rematch_prompt_closed", "stock_confirmation_stopped_and_cleared_yes_no");
    }

    private static bool RematchPromptClosed()
    {
        var prompt = UIHandler.instance.popUpHandler;
        return !prompt.isPicking && !prompt.yesPart.loop && !prompt.noPart.loop &&
            !HasVisiblePromptParticles(prompt.yesPart) && !HasVisiblePromptParticles(prompt.noPart);
    }

    private static bool HasVisiblePromptParticles(GeneralParticleSystem emitter)
    {
        if (emitter.transform.childCount == 0) return false;
        foreach (Transform particle in emitter.transform.GetChild(0))
            if (particle.gameObject.activeInHierarchy) return true;
        return false;
    }

    private static bool DelayRematchMapOnGuest(MapManager __instance, string sceneName)
    {
        var driver = Plugin.Instance?.GetComponent<SmokeDriver>();
        if (driver == null || !driver._matchEnded || driver._replayingDelayedLoad) return true;
        driver._rematchMapRequests++;
        if (PhotonNetwork.IsMasterClient) return true;
        driver.StartCoroutine(driver.LoadDelayedRematchMap(__instance, sceneName));
        return false;
    }

    private System.Collections.IEnumerator LoadDelayedRematchMap(MapManager manager, string sceneName)
    {
        Plugin.Instance?.WriteTrace("smoke_delayed_rematch_map", "guest_waits_2.5_seconds_before_loading");
        yield return new WaitForSecondsRealtime(2.5f);
        _replayingDelayedLoad = true;
        try { manager.RPCA_LoadLevel(sceneName); }
        finally { _replayingDelayedLoad = false; }
    }

    private static System.Collections.IEnumerator VerifyRematchStaysPlayable(Plugin plugin, GM_ArmsRace game, MapWrapper map)
    {
        float deadline = Time.realtimeSinceStartup + 3f;
        while (Time.realtimeSinceStartup < deadline)
        {
            var players = PlayerManager.instance.players;
            if (!GameManager.instance.battleOngoing || !ReferenceEquals(MapManager.instance.currentMap, map) ||
                !map.Scene.isLoaded || players.Count != 2 || players.Exists(player => player.data.dead || !player.data.isPlaying) ||
                !RematchPromptClosed() ||
                game.p1Points != 0 || game.p2Points != 0 || game.p1Rounds != 0 || game.p2Rounds != 0)
            {
                plugin.WriteTrace("smoke_failed", "rematch_battle_did_not_stay_playable");
                Application.Quit(2);
                yield break;
            }
            yield return null;
        }
        int orangeTeam = OrangeTeam();
        if (!VerifyHud(orangeTeam == 0 ? 1 : 0, orangeTeam == 1 ? 1 : 0))
        {
            plugin.WriteTrace("smoke_failed", "rematch_lobby_score_changed_during_battle");
            Application.Quit(2);
            yield break;
        }
        plugin.WriteTrace("smoke_rematch_hud_verified", $"orange=1;blue=0;rounds={game.p1Rounds}:{game.p2Rounds}");
        yield return QuitAfterDelivery(0, 12);
    }

    private static int OrangeTeam()
    {
        Color first = PlayerSkinBank.GetPlayerSkinColors(0).winText;
        Color second = PlayerSkinBank.GetPlayerSkinColors(1).winText;
        return first.r - first.b > second.r - second.b ? 0 : 1;
    }

    private static bool VerifyHud(int team0, int team1)
    {
        var roundCounter = UIHandler.instance.roundCounterSmall;
        var hud = roundCounter.transform.Find("RoundsP2P.MatchHud");
        if (hud == null || !hud.gameObject.activeInHierarchy) return false;
        return VerifyLabel(hud, "Team0MatchWins", team0.ToString()) &&
               VerifyLabel(hud, "Team1MatchWins", team1.ToString()) &&
               VerifyLabel(hud, "PeerPing", "Ping —");
    }

    private static bool VerifyLabel(Transform hud, string name, string expected)
    {
        var label = hud.Find(name)?.GetComponent<TextMeshProUGUI>();
        if (label == null || !label.isActiveAndEnabled || label.text != expected) return false;
        label.ForceMeshUpdate();
        return label.textInfo.characterCount == expected.Length;
    }

    private static System.Collections.IEnumerator QuitAfterDelivery(int exitCode, float delay = 5)
    {
        yield return new WaitForSecondsRealtime(delay); Application.Quit(exitCode);
    }

    private static System.Collections.IEnumerator VerifyFailureRecovery(Plugin plugin)
    {
        float deadline = Time.realtimeSinceStartup + 20;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (GameManager.instance != null && !GameManager.instance.isPlaying && MainMenuHandler.instance != null &&
                MainMenuHandler.instance.gameObject.activeInHierarchy && !PhotonNetwork.IsConnected)
            {
                plugin.WriteTrace("smoke_failure_recovery_complete", "game_stopped;menu_restored;photon_disconnected");
                Application.Quit(0);
                yield break;
            }
            yield return null;
        }
        plugin.WriteTrace("smoke_failed", "steam_failure_did_not_restore_menu");
        Application.Quit(2);
    }
}

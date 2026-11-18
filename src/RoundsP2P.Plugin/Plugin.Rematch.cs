using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Photon.Pun;
using RoundsP2P.Core;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace RoundsP2P.Plugin;

public sealed partial class Plugin
{
    private const string RematchReadyKey = "r2p_rematch_map";
    private MapWrapper? _mapBeforeRematch;
    private int _rematchNumber;
    private bool _rematchArmed;
    private bool _rematchPreparing;

    private void InstallRematchHooks(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(GM_ArmsRace), "GameOverRematch"),
            transpiler: new HarmonyMethod(typeof(Plugin), nameof(ReplacePreviewLoad)));
        harmony.Patch(AccessTools.Method(typeof(GM_ArmsRace), "GetRematchYesNo"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(DismissRematchPrompt)));
        harmony.Patch(AccessTools.Method(typeof(GM_ArmsRace), "DoStartGame"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(WrapRematchStart)));
        harmony.Patch(AccessTools.Method(typeof(GM_ArmsRace), nameof(GM_ArmsRace.PlayerDied)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowRoundEnding)));
    }

    private static IEnumerable<CodeInstruction> ReplacePreviewLoad(IEnumerable<CodeInstruction> instructions)
    {
        var load = AccessTools.Method(typeof(MapManager), nameof(MapManager.LoadNextLevel));
        var replacement = AccessTools.Method(typeof(Plugin), nameof(LoadRematchPreview));
        var result = new List<CodeInstruction>(instructions);
        var preview = result.Find(code => code.Calls(load));
        if (preview == null)
            throw new InvalidOperationException("RoundsP2P could not locate the stock rematch map preload.");
        preview.opcode = OpCodes.Call;
        preview.operand = replacement;
        return result;
    }

    private static void LoadRematchPreview(MapManager manager, bool callInImmediately, bool forceLoad)
    {
        var plugin = Instance;
        if (plugin?._matchScore == null)
        {
            manager.LoadNextLevel(callInImmediately, forceLoad);
            return;
        }

        plugin.WriteTrace("rematch_preview_skipped", "map_is_requested_after_both_votes");
    }

    private static void DismissRematchPrompt()
    {
        if (Instance?._matchScore == null) return;
        var prompt = UIHandler.instance?.popUpHandler;
        if (prompt == null) return;

        // Stock input stops looping after the vote but leaves emitted labels on screen.
        prompt.yesPart.Stop();
        prompt.noPart.Stop();
        prompt.yesAnim.PlayOut();
        prompt.noAnim.PlayOut();
    }

    private static bool AllowRoundEnding() => Instance?._rematchPreparing != true;

    private static void WrapRematchStart(GM_ArmsRace __instance, ref IEnumerator __result)
    {
        var plugin = Instance;
        if (plugin?._rematchArmed != true || plugin._matchScore == null) return;
        plugin._rematchArmed = false;
        __result = plugin.RunRematchStart(__instance, __result, plugin._matchScore, plugin._mapBeforeRematch, plugin._rematchNumber);
    }

    private IEnumerator RunRematchStart(GM_ArmsRace game, IEnumerator original, LobbyMatchScore session, MapWrapper? oldMap, int number)
    {
        _rematchPreparing = true;
        bool waitedForLoad = false;
        try
        {
            while (IsRematchActive(game, session, number) && original.MoveNext())
            {
                if (!waitedForLoad && original.Current is WaitForSecondsRealtime)
                {
                    // The first real-time wait is immediately after the stock request
                    // to load the map. Its fixed delay cannot tell whether both peers loaded it.
                    yield return WaitForBothMaps(game, session, oldMap, number);
                    waitedForLoad = true;
                }
                else yield return original.Current;
            }

            if (!IsRematchActive(game, session, number)) yield break;
            GameManager.instance.battleOngoing = false;
            while (IsRematchActive(game, session, number) && !PlayersAndMapEntered())
                yield return null;
            if (IsRematchActive(game, session, number))
            {
                GameManager.instance.battleOngoing = true;
                WriteTrace("rematch_battle_ready", "cards_finished;map_entered;players_spawned");
            }
        }
        finally
        {
            (original as IDisposable)?.Dispose();
            if (ReferenceEquals(_matchScore, session) && _rematchNumber == number)
                _rematchPreparing = false;
        }
    }

    private IEnumerator WaitForBothMaps(GM_ArmsRace game, LobbyMatchScore session, MapWrapper? oldMap, int number)
    {
        string? ready = null;
        while (IsRematchActive(game, session, number))
        {
            var manager = MapManager.instance;
            var current = manager?.currentMap;
            // The old map's exit also cleans up transient objects. Let it finish
            // before the picker creates new cards or the new map starts moving.
            if (ready == null && current != null && !ReferenceEquals(current, oldMap) &&
                (oldMap == null || !oldMap.Scene.isLoaded) &&
                current.Scene.isLoaded && current.Map != null && manager!.currentLevelID >= 0)
            {
                string state = $"{number}:{manager.currentLevelID}";
                if (PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { RematchReadyKey, state } }))
                    ready = state;
            }

            if (ready != null && BothPlayersLoaded(ready))
            {
                WriteTrace("rematch_map_ready", "both_clients_loaded_matching_map");
                yield break;
            }
            yield return null;
        }
    }

    private bool IsRematchActive(GM_ArmsRace game, LobbyMatchScore session, int number) =>
        ReferenceEquals(_matchScore, session) && _rematchNumber == number &&
        game != null && game.isActiveAndEnabled && PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.PlayerCount == 2;

    private static bool BothPlayersLoaded(string ready)
    {
        foreach (var player in PhotonNetwork.CurrentRoom.Players.Values)
            if (!player.CustomProperties.TryGetValue(RematchReadyKey, out object value) || (value as string) != ready)
                return false;
        return true;
    }

    private static bool PlayersAndMapEntered()
    {
        var map = MapManager.instance?.currentMap?.Map;
        var players = PlayerManager.instance?.players;
        if (map == null || !map.hasEntered || players == null || players.Count != 2) return false;
        foreach (var player in players)
            if (player == null || player.data == null || !player.data.isPlaying || player.data.dead)
                return false;
        return true;
    }
}

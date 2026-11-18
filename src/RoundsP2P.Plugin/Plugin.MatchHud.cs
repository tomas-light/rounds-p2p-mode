using System;
using HarmonyLib;
using Photon.Pun;
using RoundsP2P.Core;

namespace RoundsP2P.Plugin;

public sealed partial class Plugin
{
    private readonly MatchHud _matchHud = new MatchHud();
    private LobbyMatchScore? _matchScore;
    private bool _hudSmoke;

    private void InstallMatchHudHooks(Harmony harmony)
    {
        _hudSmoke = SmokeEnabled && Array.IndexOf(Environment.GetCommandLineArgs(), "--rounds-p2p-hud-smoke") >= 0;
        harmony.Patch(AccessTools.Method(typeof(GM_ArmsRace), "GameOver"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(RecordMatchWinner)));
        // The stock rematch coroutine calls ResetMatch only after both players
        // accept. It then starts the next match on the same room and actors.
        harmony.Patch(AccessTools.Method(typeof(GM_ArmsRace), "ResetMatch"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(CommitRematchScore)));
    }

    private static void RecordMatchWinner(int winningTeamID)
    {
        var plugin = Instance;
        if (plugin?._matchScore == null || !PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom.PlayerCount != 2)
            return;

        plugin._matchScore.RecordMatchWinner(ActorForTeam(winningTeamID));
        plugin._mapBeforeRematch = MapManager.instance?.currentMap;
    }

    private static void CommitRematchScore()
    {
        var plugin = Instance;
        if (plugin?._matchScore == null || !PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom.PlayerCount != 2)
            return;

        if (plugin._matchScore.StartRematch())
            plugin.WriteTrace("lobby_rematch_score", $"team0={plugin.MatchWinsForTeam(0)};team1={plugin.MatchWinsForTeam(1)}");
        plugin._rematchNumber++;
        plugin._rematchArmed = true;
    }

    private static int ActorForTeam(int teamId)
    {
        var manager = PlayerManager.instance;
        if (manager == null) return 0;
        foreach (var player in manager.players)
        {
            if (player == null || player.TeamID != teamId || player.data == null || player.data.view == null)
                continue;
            return player.data.view.OwnerActorNr;
        }
        return 0;
    }

    internal int MatchWinsForTeam(int teamId) => _matchScore?.WinsFor(ActorForTeam(teamId)) ?? 0;

    private void LateUpdate()
    {
        var ui = UIHandler.instance;
        if (_matchScore == null || ui == null || ui.roundCounterSmall == null ||
            !ui.roundCounterSmall.isActiveAndEnabled || !PhotonNetwork.InRoom)
        {
            _matchHud.Hide();
            return;
        }

        _matchHud.Refresh(ui.roundCounterSmall, MatchWinsForTeam(0), MatchWinsForTeam(1), _steam?.PeerPingMilliseconds);
    }
}

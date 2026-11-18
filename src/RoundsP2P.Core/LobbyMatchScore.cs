using System.Collections.Generic;

namespace RoundsP2P.Core;

public sealed class LobbyMatchScore
{
    private readonly Dictionary<int, int> _wins = new Dictionary<int, int>();
    private int? _pendingWinner;

    public int WinsFor(int actorNumber) => _wins.TryGetValue(actorNumber, out int wins) ? wins : 0;

    public void RecordMatchWinner(int actorNumber)
    {
        if (actorNumber > 0 && !_pendingWinner.HasValue)
            _pendingWinner = actorNumber;
    }

    public bool StartRematch()
    {
        if (!_pendingWinner.HasValue)
            return false;

        int winner = _pendingWinner.Value;
        _wins[winner] = WinsFor(winner) + 1;
        _pendingWinner = null;
        return true;
    }
}

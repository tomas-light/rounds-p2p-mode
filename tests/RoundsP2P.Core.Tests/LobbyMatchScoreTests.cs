using Xunit;

namespace RoundsP2P.Core.Tests;

public sealed class LobbyMatchScoreTests
{
    [Fact]
    public void Both_scores_start_at_zero_and_the_match_end_does_not_award_a_win()
    {
        var score = new LobbyMatchScore();
        Assert.Equal(0, score.WinsFor(1));
        Assert.Equal(0, score.WinsFor(2));

        score.RecordMatchWinner(2);
        Assert.Equal(0, score.WinsFor(2));
    }

    [Fact]
    public void Starting_each_rematch_awards_one_win_to_the_recorded_actor()
    {
        var score = new LobbyMatchScore();
        score.RecordMatchWinner(2);
        score.RecordMatchWinner(2);
        Assert.True(score.StartRematch());
        Assert.False(score.StartRematch());
        Assert.Equal(0, score.WinsFor(1));
        Assert.Equal(1, score.WinsFor(2));

        score.RecordMatchWinner(1);
        Assert.True(score.StartRematch());
        score.RecordMatchWinner(2);
        Assert.True(score.StartRematch());
        Assert.Equal(1, score.WinsFor(1));
        Assert.Equal(2, score.WinsFor(2));
    }

    [Fact]
    public void A_new_lobby_does_not_inherit_wins_or_an_unplayed_rematch()
    {
        var oldLobby = new LobbyMatchScore();
        oldLobby.RecordMatchWinner(1);
        oldLobby.StartRematch();
        oldLobby.RecordMatchWinner(2);

        var newLobby = new LobbyMatchScore();
        Assert.False(newLobby.StartRematch());
        Assert.Equal(0, newLobby.WinsFor(1));
        Assert.Equal(0, newLobby.WinsFor(2));
    }
}

using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class ReadyLogicTests
{
    private const int Spec = ReadyLogic.TeamSpectator;
    private const int T = ReadyLogic.TeamTerrorist;
    private const int CT = ReadyLogic.TeamCounterTerrorist;

    [Theory]
    [InlineData(1, "Spectator")]
    [InlineData(2, "T")]
    [InlineData(3, "CT")]
    [InlineData(0, "None")]
    public void TeamNumbersAreCs2Sides(int team, string label)
    {
        // Discord report: the log shows team 1, 2 and 3. Those are CS2 side numbers, not a slot mix-up.
        Assert.Equal(label, ReadyLogic.TeamLabel(team));
    }

    [Fact]
    public void SpectatorsWithNoMinimumAreAlwaysReady()
    {
        Assert.True(ReadyLogic.IsTeamReady(Spec, 0, 0, 0, 0, readyAvailable: true, forcedReady: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptySideIsNeverReady(bool forced)
    {
        // Discord log: team 3 minPlayers:3 minReady:1 playerCount:0 readyCount:0.
        Assert.False(ReadyLogic.IsTeamReady(CT, 0, 0, 3, 1, readyAvailable: true, forcedReady: forced));
    }

    [Fact]
    public void RosterMustBeFullEvenWhenForced()
    {
        Assert.False(ReadyLogic.IsTeamReady(T, 2, 2, 3, 1, readyAvailable: true, forcedReady: true));
    }

    [Theory]
    [InlineData(3, 3, 0, true)]   // minReady 0: everyone must ready
    [InlineData(3, 2, 0, false)]
    [InlineData(3, 1, 1, true)]   // minReady N: at least N
    [InlineData(3, 0, 1, false)]
    public void ReadyThreshold(int players, int ready, int minReady, bool expected)
    {
        Assert.Equal(expected, ReadyLogic.IsTeamReady(CT, players, ready, 3, minReady, readyAvailable: true, forcedReady: false));
    }

    [Fact]
    public void ForceReadyPassesReadyCountWithFullRoster()
    {
        Assert.True(ReadyLogic.IsTeamReady(T, 3, 0, 3, 0, readyAvailable: true, forcedReady: true));
    }

    [Theory]
    [InlineData(false, false, false, false, true)]  // human not tracked: register (was never counted)
    [InlineData(false, false, true, false, true)]   // tracked under a stale controller: re-register
    [InlineData(false, false, true, true, false)]   // tracked and live: nothing to do
    [InlineData(false, true, false, false, false)]  // bots have their own flow
    [InlineData(true, false, false, false, false)]  // GOTV is never tracked
    public void ReadyRegistersUntrackedHumans(bool hltv, bool bot, bool tracked, bool valid, bool expected)
    {
        Assert.Equal(expected, ReadyLogic.NeedsTrackingOnReady(hltv, bot, tracked, valid));
    }
}

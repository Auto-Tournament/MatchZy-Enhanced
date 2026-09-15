using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class MatchLogicTests
{
    [Theory]
    [InlineData(3, 1, null, "team1")]
    [InlineData(1, 3, null, "team2")]
    [InlineData(13, 11, "team2", "team1")] // decisive score ignores any tiebreak
    [InlineData(2, 2, "team1", "team1")]   // QA: dust2 2-2, Alpha (team1) won on damage 1707-1360, was sent as team2
    [InlineData(2, 2, "team2", "team2")]
    [InlineData(2, 2, null, "none")]       // damage tied too: a real draw
    public void MapWinnerUsesScoreThenTiebreak(int t1, int t2, string? tiebreak, string expected)
    {
        Assert.Equal(expected, MatchLogic.ResolveMapWinnerSlot(t1, t2, tiebreak));
    }

    [Theory]
    [InlineData(1525, 1513, "team1")] // QA: de_train Charlie (team1) 1525 vs Delta 1513
    [InlineData(995, 1023, "team2")]
    [InlineData(1000, 1000, null)]
    public void DamageTiebreak(int d1, int d2, string? expected)
    {
        Assert.Equal(expected, MatchLogic.ResolveDamageTiebreakSlot(d1, d2));
    }

    [Theory]
    [InlineData("team1", "CT", "3")]
    [InlineData("team1", "TERRORIST", "2")]
    [InlineData("team2", "CT", "2")]
    [InlineData("team2", "TERRORIST", "3")]
    [InlineData("none", "CT", "0")]
    public void SideNumberForSlot(string slot, string team1Side, string expected)
    {
        Assert.Equal(expected, MatchLogic.SideNumberForSlot(slot, team1Side));
    }

    [Theory]
    // map_sides team1_ct: team1 on CT (3)
    [InlineData(3, "CT", "team1")]
    [InlineData(2, "CT", "team2")]
    // map_sides team2_ct, or after the halftime swap: team1 on T (2)
    [InlineData(3, "TERRORIST", "team2")]
    [InlineData(2, "TERRORIST", "team1")]
    [InlineData(1, "CT", null)] // spectator
    [InlineData(0, "CT", null)] // not joined yet
    public void SlotForTeamNum(int teamNum, string team1Side, string? expected)
    {
        Assert.Equal(expected, MatchLogic.SlotForTeamNum(teamNum, team1Side));
    }

    [Fact]
    public void SlotAndSideAreInverse()
    {
        foreach (var team1Side in new[] { "CT", "TERRORIST" })
        {
            foreach (var slot in new[] { "team1", "team2" })
            {
                int teamNum = int.Parse(MatchLogic.SideNumberForSlot(slot, team1Side));
                Assert.Equal(slot, MatchLogic.SlotForTeamNum(teamNum, team1Side));
            }
        }
    }

    [Theory]
    [InlineData(10f, 10f)]  // MAT's maximum used to be clamped to 4
    [InlineData(4f, 4f)]
    [InlineData(25f, 10f)]
    [InlineData(0.05f, 0.1f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    public void SimulationTimeScaleClamp(float requested, float expected)
    {
        Assert.Equal(expected, MatchLogic.ClampSimulationTimeScale(requested));
    }
}

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
    [InlineData(2, 2, null, "none")]       // no tiebreak slot (draws allowed): a real draw
    public void MapWinnerUsesScoreThenTiebreak(int t1, int t2, string? tiebreak, string expected)
    {
        Assert.Equal(expected, MatchLogic.ResolveMapWinnerSlot(t1, t2, tiebreak));
    }

    [Theory]
    [InlineData(1525, 1513, "team1")] // QA: de_train Charlie (team1) 1525 vs Delta 1513
    [InlineData(995, 1023, "team2")]
    public void DamageDecidesFirst(int d1, int d2, string expected)
    {
        // Damage outranks every later criterion, even when those favour the other team.
        var t1 = new MatchLogic.TiebreakTotals(d1, Kills: 1, HeadshotKills: 1, UtilityDamage: 1);
        var t2 = new MatchLogic.TiebreakTotals(d2, Kills: 99, HeadshotKills: 99, UtilityDamage: 99);
        (string? slot, string criterion) = MatchLogic.ResolveTiedMap(t1, t2, drawsDisallowed: true, 1, 0);
        Assert.Equal(expected, slot);
        Assert.Equal("damage", criterion);
    }

    [Fact]
    public void EqualDamageFallsToNextCriterion()
    {
        // QA: grand final Anubis 2-2, damage 1291 vs 1291 was recorded as a draw.
        var kills = MatchLogic.ResolveTiedMap(new(1291, 10, 5, 0), new(1291, 12, 1, 0), true, 46, 0);
        Assert.Equal(("team2", "kills"), kills);

        var headshots = MatchLogic.ResolveTiedMap(new(1291, 12, 6, 0), new(1291, 12, 5, 90), true, 46, 0);
        Assert.Equal(("team1", "headshot_kills"), headshots);

        var utility = MatchLogic.ResolveTiedMap(new(1291, 12, 5, 10), new(1291, 12, 5, 90), true, 46, 0);
        Assert.Equal(("team2", "utility_damage"), utility);
    }

    [Fact]
    public void AllTiedWithDrawsDisallowedIsDeterministicCoinFlip()
    {
        var totals = new MatchLogic.TiebreakTotals(1291, 12, 5, 40);
        var seen = new HashSet<string>();
        for (long matchId = 1; matchId <= 64; matchId++)
        {
            for (int map = 0; map < 3; map++)
            {
                (string? slot, string criterion) = MatchLogic.ResolveTiedMap(totals, totals, true, matchId, map);
                Assert.Equal(MatchLogic.CoinFlipCriterion, criterion);
                Assert.True(slot == "team1" || slot == "team2", "never a draw");
                Assert.Equal(slot, MatchLogic.ResolveTiedMap(totals, totals, true, matchId, map).Slot);
                Assert.Equal(slot, MatchLogic.CoinFlipSlot(matchId, map));
                seen.Add(slot!);
            }
        }
        Assert.Equal(2, seen.Count); // both outcomes occur across seeds
    }

    [Fact]
    public void AllTiedWithDrawsAllowedIsDraw()
    {
        var totals = new MatchLogic.TiebreakTotals(1291, 12, 5, 40);
        Assert.Equal(((string?)null, "none"), MatchLogic.ResolveTiedMap(totals, totals, drawsDisallowed: false, 46, 0));
        Assert.Equal("none", MatchLogic.ResolveMapWinnerSlot(2, 2, null));
    }

    [Theory]
    [InlineData("disabled", 0, true)]    // QA config: no OT, no draws
    [InlineData("disabled", null, true)]
    [InlineData("DISABLED", null, true)]
    [InlineData("disabled", 2, false)]
    [InlineData("enabled", 3, true)]     // capped OT, no draws after it
    [InlineData("enabled", 0, false)]
    [InlineData(null, null, false)]      // legacy: draws allowed
    [InlineData(null, 2, true)]
    public void DrawsDisallowed(string? mode, int? segments, bool expected)
    {
        Assert.Equal(expected, MatchLogic.DrawsDisallowed(mode, segments));
    }

    [Theory]
    [InlineData(3, 3, 0, 2)]
    [InlineData(3, 3, 1, 1)]
    [InlineData(3, 3, 2, 0)]  // QA: after map index 2 the old formula said 1 (the drawn map was not counted)
    [InlineData(3, 3, 5, 0)]  // never negative
    [InlineData(3, 2, 1, 0)]  // shorter maplist caps the series
    [InlineData(1, 1, 0, 0)]
    public void RemainingMapsCountsDrawnMaps(int numMaps, int maplistCount, int currentMapNumber, int expected)
    {
        Assert.Equal(expected, MatchLogic.RemainingMaps(numMaps, maplistCount, currentMapNumber));
    }

    [Theory]
    // Bo3 with clinch; QA sequence draw (0-0), Bravo (1-0), Hotel (1-1): all maps played -> series over.
    [InlineData(3, 2, 0, 0, true, false)]
    [InlineData(3, 1, 1, 0, true, false)]
    [InlineData(3, 0, 1, 1, true, true)]
    [InlineData(3, 0, 1, 0, true, true)]  // draw, win, draw: no maps left, series ends 1-0
    [InlineData(3, 1, 2, 0, true, true)]  // clinched
    [InlineData(3, 1, 2, 0, false, false)] // no clinch: play it out
    [InlineData(3, 0, 2, 1, false, true)]
    [InlineData(2, 0, 1, 1, true, true)]  // Bo2 tie: series over (draw)
    public void SeriesOverWhenNoMapsLeftOrClinched(int numMaps, int remaining, int t1, int t2, bool clinch, bool expected)
    {
        Assert.Equal(expected, MatchLogic.IsSeriesOver(numMaps, remaining, t1, t2, clinch));
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

using AutoTournamentCS2;
using Xunit;

namespace AutoTournamentCS2.Tests;

public class TournamentStatusLogicTests
{
    // Simulates UpdateTournamentStatus: returns the (status, match) pair the convars hold afterwards.
    private static (string Status, string Match) Publish(
        (string Status, string Match) current, bool isMatchSetup, string status, string slug = "")
    {
        bool active = TournamentStatusLogic.HasActiveMatch(isMatchSetup, current.Status, status);
        return (status, TournamentStatusLogic.ResolveMatchValue(status, slug, current.Match, active));
    }

    [Fact]
    public void IdleAlwaysClearsTheMatch()
    {
        // QA: server-1 EndSeries logged "Status: idle, Match: 25" and stayed busy in MAT.
        Assert.Equal("", TournamentStatusLogic.ResolveMatchValue("idle", "", "25", hasActiveMatch: true));
        Assert.Equal("", TournamentStatusLogic.ResolveMatchValue("IDLE", "25", "25", hasActiveMatch: true));
    }

    [Fact]
    public void WarmupWithoutActiveMatchClearsTheMatch()
    {
        Assert.Equal("", TournamentStatusLogic.ResolveMatchValue("warmup", "", "35", hasActiveMatch: false));
    }

    [Fact]
    public void ActiveMatchKeepsOrReplacesTheMatch()
    {
        Assert.Equal("35", TournamentStatusLogic.ResolveMatchValue("playing", "", "35", hasActiveMatch: true));
        Assert.Equal("36", TournamentStatusLogic.ResolveMatchValue("loading", "36", "35", hasActiveMatch: true));
    }

    [Theory]
    [InlineData(true, "warmup", "postgame", true)]
    [InlineData(false, "loading", "warmup", true)]  // load in progress, isMatchSetup not flipped yet
    [InlineData(false, "idle", "loading", true)]
    [InlineData(false, "postgame", "warmup", false)] // after ResetMatch
    [InlineData(false, "error", "idle", false)]
    public void HasActiveMatch(bool isMatchSetup, string current, string next, bool expected)
    {
        Assert.Equal(expected, TournamentStatusLogic.HasActiveMatch(isMatchSetup, current, next));
    }

    [Theory]
    [InlineData(true, "warmup")]
    [InlineData(false, "idle")]
    public void IdleStatusFollowsWarmup(bool isWarmup, string expected)
    {
        Assert.Equal(expected, TournamentStatusLogic.IdleStatus(isWarmup));
    }

    [Fact]
    public void FullSeriesThenResetEndsIdleWithNoMatch()
    {
        var s = ("idle", "");
        s = Publish(s, isMatchSetup: false, "loading", "25");
        Assert.Equal(("loading", "25"), s);
        s = Publish(s, isMatchSetup: false, "warmup"); // StartWarmup inside LoadMatchFromJSON
        Assert.Equal(("warmup", "25"), s);
        s = Publish(s, isMatchSetup: true, "playing");
        s = Publish(s, isMatchSetup: true, "postgame");
        Assert.Equal(("postgame", "25"), s);
        s = Publish(s, isMatchSetup: true, "idle", ""); // EndSeries, before ResetMatch
        Assert.Equal(("idle", ""), s);
        s = Publish(s, isMatchSetup: false, "warmup"); // ResetMatch -> StartWarmup
        Assert.Equal(("warmup", ""), s);
        s = Publish(s, isMatchSetup: false, "idle", "");
        Assert.Equal(("idle", ""), s);
    }

    [Fact]
    public void RestartDuringLiveMatchClearsTheMatch()
    {
        var s = ("playing", "40");
        s = Publish(s, isMatchSetup: false, "warmup"); // css_restart / css_endmatch -> ResetMatch
        Assert.Equal(("warmup", ""), s);
    }

    [Fact]
    public void FailedLoadKeepsIdThenResetClearsIt()
    {
        var s = ("loading", "41");
        s = Publish(s, isMatchSetup: false, "error");
        Assert.Equal(("error", "41"), s);
        s = Publish(s, isMatchSetup: false, "warmup");
        Assert.Equal(("warmup", ""), s);
    }
}

using AutoTournamentCS2;
using Xunit;

namespace AutoTournamentCS2.Tests;

public class MatchRestartDelayLogicTests
{
    [Theory]
    [InlineData(0, 15, 15)]
    [InlineData(105, 120, 130)]
    [InlineData(-5, 15, 15)]
    public void RequiredDelayFollowsTvDelay(int tvDelay, int flush, int required)
    {
        Assert.Equal(flush, MatchRestartDelayLogic.TvFlushDelay(tvDelay));
        Assert.Equal(required, MatchRestartDelayLogic.RequiredDelay(tvDelay));
    }

    [Fact]
    public void TvDelayZeroDoesNotExtend()
    {
        var logic = new MatchRestartDelayLogic();
        var d = logic.Compute(25, 0, demoRecordingEnabled: true, hasUploadEndpoint: true);
        Assert.Equal(new MatchEndDelays(25, 15, null), d);
        Assert.Null(logic.OriginalDelay);
        Assert.Null(logic.Restore(25));
    }

    [Fact]
    public void ExtendsWhenTvDelayNeedsItAndRestoresOriginal()
    {
        var logic = new MatchRestartDelayLogic();
        var d = logic.Compute(25, 105, true, true);
        Assert.Equal(new MatchEndDelays(130, 120, 130), d);
        Assert.Equal(25, logic.OriginalDelay);

        Assert.Equal(25, logic.Restore(130));
        Assert.Null(logic.OriginalDelay);
        Assert.Null(logic.Restore(25));
    }

    [Fact]
    public void LaterMapWithTvDelayZeroShrinksBackInsteadOfInheritingExtension()
    {
        // The reported bug: extended to 130 once, then every tv_delay 0 match kept 130.
        var logic = new MatchRestartDelayLogic();
        logic.Compute(25, 105, true, true);
        var d = logic.Compute(130, 0, true, true);
        Assert.Equal(new MatchEndDelays(25, 15, 25), d);
    }

    [Fact]
    public void IdempotentAcrossSeries()
    {
        var logic = new MatchRestartDelayLogic();
        int cvar = 25;
        for (int series = 0; series < 3; series++)
        {
            var d = logic.Compute(cvar, 105, true, true);
            Assert.Equal(130, d.RestartDelay);
            cvar = d.CvarToSet ?? cvar;
            Assert.Equal(25, logic.OriginalDelay);
            cvar = logic.Restore(cvar) ?? cvar;
            Assert.Equal(25, cvar);
        }

        Assert.Equal(new MatchEndDelays(25, 15, null), logic.Compute(cvar, 0, true, true));
    }

    [Fact]
    public void KeepsOriginalAcrossMapsOfOneSeries()
    {
        var logic = new MatchRestartDelayLogic();
        var map1 = logic.Compute(25, 105, true, true);
        var map2 = logic.Compute(map1.CvarToSet!.Value, 105, true, true);
        Assert.Equal(new MatchEndDelays(130, 120, null), map2);
        Assert.Equal(25, logic.OriginalDelay);
    }

    [Fact]
    public void ExternalChangeAfterExtensionWins()
    {
        var logic = new MatchRestartDelayLogic();
        logic.Compute(25, 105, true, true);
        // A cfg set 40 after our raise to 130: don't clobber it.
        Assert.Null(logic.Restore(40));
        Assert.Null(logic.OriginalDelay);
    }

    [Fact]
    public void OperatorDelayLargerThanRequiredIsKept()
    {
        var logic = new MatchRestartDelayLogic();
        Assert.Equal(new MatchEndDelays(200, 120, null), logic.Compute(200, 105, true, true));
    }

    [Fact]
    public void NonUploadBranchesNeverTouchConvar()
    {
        var logic = new MatchRestartDelayLogic();
        Assert.Equal(new MatchEndDelays(10, 0, null), logic.Compute(130, 105, false, true));
        Assert.Equal(new MatchEndDelays(130, 120, null), logic.Compute(25, 105, true, false));
        Assert.Equal(new MatchEndDelays(15, 15, null), logic.Compute(130, 0, true, false));
        Assert.Null(logic.OriginalDelay);
    }
}

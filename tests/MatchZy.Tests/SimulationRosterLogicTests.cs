using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class SimulationRosterLogicTests
{
    private static List<SimRosterSlot> Roster(int perTeam = 5)
    {
        var roster = new List<SimRosterSlot>();
        for (int i = 1; i <= perTeam; i++) roster.Add(new SimRosterSlot($"1{i}", "team1"));
        for (int i = 1; i <= perTeam; i++) roster.Add(new SimRosterSlot($"2{i}", "team2"));
        return roster;
    }

    private static Dictionary<int, string> FullMapping() => new()
    {
        [3] = "11", [4] = "12", [5] = "13", [6] = "14", [7] = "15",
        [8] = "21", [9] = "22", [10] = "23", [11] = "24", [12] = "25",
    };

    private static List<SimLiveBot> Bots(IEnumerable<int> team1, IEnumerable<int> team2) =>
        team1.Select(id => new SimLiveBot(id, "team1"))
            .Concat(team2.Select(id => new SimLiveBot(id, "team2")))
            .ToList();

    [Fact]
    public void ConsistentRosterNeedsNoChanges()
    {
        var plan = SimulationRosterLogic.Reconcile(Roster(), FullMapping(), Bots(new[] { 3, 4, 5, 6, 7 }, new[] { 8, 9, 10, 11, 12 }));
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void BotThatLeftIsStaleAndItsSlotIsMissing()
    {
        // QA match 64: the bot behind UserId=12 was gone; team2 stuck at 4 of 5.
        var plan = SimulationRosterLogic.Reconcile(Roster(), FullMapping(), Bots(new[] { 3, 4, 5, 6, 7 }, new[] { 8, 9, 10, 11 }));
        Assert.Equal(new[] { 12 }, plan.StaleUserIds);
        Assert.Empty(plan.NewAssignments);
        Assert.Equal(new[] { new SimRosterSlot("25", "team2") }, plan.MissingSlots);
    }

    [Fact]
    public void ReplacementBotTakesTheFreedSlotOfItsSide()
    {
        var mapping = FullMapping();
        var bots = Bots(new[] { 3, 4, 5, 6, 7 }, new[] { 8, 9, 10, 11, 13 });
        var plan = SimulationRosterLogic.Reconcile(Roster(), mapping, bots);

        Assert.Equal(new[] { 12 }, plan.StaleUserIds);
        Assert.Equal("25", plan.NewAssignments[13]);
        Assert.Empty(plan.MissingSlots);
    }

    [Fact]
    public void UnmappedBotPrefersItsOwnTeamSlot()
    {
        var mapping = FullMapping();
        mapping.Remove(3); // team1 slot "11" free
        mapping.Remove(8); // team2 slot "21" free
        var bots = Bots(new[] { 4, 5, 6, 7 }, new[] { 9, 10, 11, 12, 20 }).Append(new SimLiveBot(21, "team1")).ToList();

        var plan = SimulationRosterLogic.Reconcile(Roster(), mapping, bots);

        Assert.Equal("21", plan.NewAssignments[20]);
        Assert.Equal("11", plan.NewAssignments[21]);
        Assert.Empty(plan.MissingSlots);
    }

    [Fact]
    public void BotWithoutSideTakesAnyFreeSlot()
    {
        var mapping = FullMapping();
        mapping.Remove(12);
        var bots = Bots(new[] { 3, 4, 5, 6, 7 }, new[] { 8, 9, 10, 11 }).Append(new SimLiveBot(30, null)).ToList();

        var plan = SimulationRosterLogic.Reconcile(Roster(), mapping, bots);

        Assert.Equal("25", plan.NewAssignments[30]);
        Assert.Empty(plan.MissingSlots);
    }

    [Fact]
    public void DuplicateSlotMappingKeepsOneBotAndRemapsTheOther()
    {
        var mapping = FullMapping();
        mapping[12] = "24"; // 11 and 12 both claim "24"; "25" unheld
        var plan = SimulationRosterLogic.Reconcile(Roster(), mapping, Bots(new[] { 3, 4, 5, 6, 7 }, new[] { 8, 9, 10, 11, 12 }));

        Assert.Empty(plan.StaleUserIds);
        Assert.Equal("25", plan.NewAssignments[12]);
        Assert.Empty(plan.MissingSlots);
    }

    [Fact]
    public void MappingToIdentityNotInRosterIsStale()
    {
        var mapping = FullMapping();
        mapping[12] = "999";
        var plan = SimulationRosterLogic.Reconcile(Roster(), mapping, Bots(new[] { 3, 4, 5, 6, 7 }, new[] { 8, 9, 10, 11, 12 }));
        Assert.Equal("25", plan.NewAssignments[12]);
        Assert.Empty(plan.MissingSlots);
    }

    [Fact]
    public void NoBotsMeansEverySlotMissing()
    {
        var plan = SimulationRosterLogic.Reconcile(Roster(), new Dictionary<int, string>(), new List<SimLiveBot>());
        Assert.Equal(10, plan.MissingSlots.Count);
    }

    [Fact]
    public void ExtraBotsBeyondRosterStayUnmapped()
    {
        var bots = Bots(new[] { 3, 4, 5, 6, 7, 40 }, new[] { 8, 9, 10, 11, 12 });
        var plan = SimulationRosterLogic.Reconcile(Roster(), FullMapping(), bots);
        Assert.False(plan.HasChanges);
    }

    [Theory]
    [InlineData(true, true, true, false, 61, true)]
    [InlineData(true, true, true, false, 59, false)]
    [InlineData(false, true, true, false, 600, false)]
    [InlineData(true, false, true, false, 600, false)]
    [InlineData(true, true, false, false, 600, false)]
    [InlineData(true, true, true, true, 600, false)]
    public void WatchdogOnlyFiresForStuckSimulatedWarmup(bool sim, bool setup, bool readyAvailable, bool started, double seconds, bool expected)
    {
        Assert.Equal(expected, SimulationRosterLogic.ShouldWatchdogReconcile(sim, setup, readyAvailable, started, seconds));
    }

    [Theory]
    [InlineData(true, true, true, false, false, true)]
    [InlineData(false, true, true, false, false, false)] // real match: leave mp_warmup_end alone
    [InlineData(true, true, false, false, false, false)] // plugin's own start already cleared readyAvailable
    [InlineData(true, true, true, true, false, false)]
    [InlineData(true, true, true, false, true, false)]   // start in progress issues its own mp_warmup_end
    public void WarmupEndInterceptedOnlyDuringSimulatedWarmup(bool sim, bool setup, bool readyAvailable, bool started, bool inProgress, bool expected)
    {
        Assert.Equal(expected, SimulationRosterLogic.ShouldInterceptWarmupEnd(sim, setup, readyAvailable, started, inProgress));
    }

    [Theory]
    [InlineData("team1", "CT", "CT")]
    [InlineData("team2", "CT", "T")]
    [InlineData("team1", "TERRORIST", "T")]
    [InlineData("team2", "TERRORIST", "CT")]
    public void SideForTeamSlotFollowsTeamSides(string slot, string team1Side, string expected)
    {
        Assert.Equal(expected, SimulationRosterLogic.SideForTeamSlot(slot, team1Side));
    }
}

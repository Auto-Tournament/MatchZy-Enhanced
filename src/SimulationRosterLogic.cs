using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTournamentCS2;

/// <summary>One configured roster slot a simulation bot stands in for.</summary>
public sealed record SimRosterSlot(string SteamId, string TeamSlot);

/// <summary>A bot that is connected right now. TeamSlot is "team1"/"team2", or null while the bot has no side.</summary>
public sealed record SimLiveBot(int UserId, string? TeamSlot);

/// <summary>
/// What has to change so every roster slot is held by exactly one connected bot.
/// </summary>
public sealed record SimRosterPlan(
    IReadOnlyList<int> StaleUserIds,
    IReadOnlyDictionary<int, string> NewAssignments,
    IReadOnlyList<SimRosterSlot> MissingSlots)
{
    public bool HasChanges => StaleUserIds.Count > 0 || NewAssignments.Count > 0 || MissingSlots.Count > 0;
}

/// <summary>
/// Pure rules for keeping the simulation roster (configured players -> bots) consistent,
/// kept free of CounterStrikeSharp so they can be unit tested.
///
/// QA (match 64, s_1): a UserId was reused by a new bot while the plugin still held the old
/// controller for it, so the delayed ready for that slot bailed out ("bot UserId=12 no longer
/// valid") and team2 counted 4 of 5 players forever. Everything here works from the roster
/// slot (SteamID) and the set of bots that are connected now, never from remembered UserIds.
/// </summary>
public static class SimulationRosterLogic
{
    /// <summary>Real seconds a simulated match may sit in warmup before the watchdog reconciles.</summary>
    public const double WarmupWatchdogSeconds = 60.0;

    /// <summary>After this many watchdog reconciles without going live, the watchdog force-starts.</summary>
    public const int WatchdogForceStartAfterAttempts = 2;

    public static SimRosterPlan Reconcile(
        IReadOnlyList<SimRosterSlot> roster,
        IReadOnlyDictionary<int, string> mappings,
        IReadOnlyList<SimLiveBot> liveBots)
    {
        var rosterIds = new HashSet<string>(roster.Select(r => r.SteamId));
        var liveById = new Dictionary<int, SimLiveBot>();
        foreach (var bot in liveBots) liveById[bot.UserId] = bot;

        var stale = new List<int>();
        var held = new HashSet<string>();

        // Keep mappings of connected bots; one bot per slot (lowest UserId wins a duplicate).
        foreach (var kv in mappings.OrderBy(k => k.Key))
        {
            bool keep = liveById.ContainsKey(kv.Key)
                && rosterIds.Contains(kv.Value)
                && !held.Contains(kv.Value);
            if (keep) held.Add(kv.Value);
            else stale.Add(kv.Key);
        }

        var assignments = new Dictionary<int, string>();
        var staleSet = new HashSet<int>(stale);
        var unmapped = liveBots
            .Where(b => !mappings.ContainsKey(b.UserId) || staleSet.Contains(b.UserId))
            .OrderBy(b => b.UserId)
            .ToList();

        // First pass: bots with a side take a free slot of the team on that side.
        foreach (var bot in unmapped.Where(b => b.TeamSlot != null).ToList())
        {
            var slot = roster.FirstOrDefault(r => r.TeamSlot == bot.TeamSlot && !held.Contains(r.SteamId));
            if (slot == null) continue;
            held.Add(slot.SteamId);
            assignments[bot.UserId] = slot.SteamId;
            unmapped.Remove(bot);
        }

        // Second pass: whatever is left takes any free slot rather than staying unmapped.
        foreach (var bot in unmapped)
        {
            var slot = roster.FirstOrDefault(r => !held.Contains(r.SteamId));
            if (slot == null) break;
            held.Add(slot.SteamId);
            assignments[bot.UserId] = slot.SteamId;
        }

        // A bot that lost its stale mapping but got a slot back is not stale any more.
        stale.RemoveAll(id => assignments.ContainsKey(id) && liveById.ContainsKey(id));

        var missing = roster.Where(r => !held.Contains(r.SteamId)).ToList();
        return new SimRosterPlan(stale, assignments, missing);
    }

    /// <summary>
    /// Whether the warmup watchdog should reconcile now: a simulation match that is set up and
    /// still in warmup past the threshold is stuck, whatever the per-slot ready state says
    /// (a team-count gate can block going live even when every mapped bot is ready).
    /// </summary>
    public static bool ShouldWatchdogReconcile(
        bool isSimulationMode, bool isMatchSetup, bool readyAvailable, bool matchStarted,
        double secondsInWarmup, double thresholdSeconds = WarmupWatchdogSeconds)
    {
        if (!isSimulationMode || !isMatchSetup || !readyAvailable || matchStarted) return false;
        return secondsInWarmup >= thresholdSeconds;
    }

    /// <summary>
    /// Whether a raw mp_warmup_end (what MAT's "end warmup" sends) should be routed through
    /// the plugin's own start path instead of just ending the engine warmup.
    /// </summary>
    public static bool ShouldInterceptWarmupEnd(
        bool isSimulationMode, bool isMatchSetup, bool readyAvailable, bool matchStarted, bool matchStartInProgress)
    {
        return isSimulationMode && isMatchSetup && readyAvailable && !matchStarted && !matchStartInProgress;
    }

    /// <summary>
    /// Side ("CT" / "T") a bot for this team slot has to join, given team1's current side.
    /// </summary>
    public static string SideForTeamSlot(string teamSlot, string team1Side)
    {
        bool team1IsCt = string.Equals(team1Side, "CT", StringComparison.OrdinalIgnoreCase);
        bool isTeam1 = teamSlot == "team1";
        return isTeam1 == team1IsCt ? "CT" : "T";
    }
}

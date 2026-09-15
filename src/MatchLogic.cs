namespace MatchZy;

/// <summary>
/// Pure match-outcome rules with no CounterStrikeSharp dependency, so they can be
/// unit tested outside the game server (see tests/MatchZy.Tests).
///
/// Conventions used throughout:
/// - Team slots are "team1" / "team2" (or "none" for no winner).
/// - Sides are the internal teamSides strings "CT" / "TERRORIST".
/// - Side numbers follow CS2 team numbers: "3" = CT, "2" = T, "0" = no side.
/// </summary>
public static class MatchLogic
{
    public const string Team1 = "team1";
    public const string Team2 = "team2";
    public const string NoTeam = "none";

    public const float MinSimulationTimeScale = 0.1f;
    public const float MaxSimulationTimeScale = 10.0f;

    /// <summary>
    /// Map winner from the final score. A tied score is resolved by the tiebreak
    /// slot when one was computed; otherwise the map is a draw ("none").
    /// </summary>
    public static string ResolveMapWinnerSlot(int team1Score, int team2Score, string? tiebreakWinnerSlot)
    {
        if (team1Score > team2Score) return Team1;
        if (team2Score > team1Score) return Team2;
        if (tiebreakWinnerSlot == Team1 || tiebreakWinnerSlot == Team2) return tiebreakWinnerSlot;
        return NoTeam;
    }

    /// <summary>
    /// Tiebreak winner by aggregate damage, or null when damage is tied too.
    /// </summary>
    public static string? ResolveDamageTiebreakSlot(int team1Damage, int team2Damage)
    {
        if (team1Damage > team2Damage) return Team1;
        if (team2Damage > team1Damage) return Team2;
        return null;
    }

    /// <summary>
    /// CS2 side number ("3" CT, "2" T) the given slot is currently playing on,
    /// or "0" when the slot is not a team (a draw).
    /// </summary>
    public static string SideNumberForSlot(string slot, string team1Side)
    {
        bool team1IsCt = team1Side == "CT";
        return slot switch
        {
            Team1 => team1IsCt ? "3" : "2",
            Team2 => team1IsCt ? "2" : "3",
            _ => "0",
        };
    }

    /// <summary>
    /// Team slot currently playing on the given CS2 team number (3 CT, 2 T),
    /// or null for spectators / unassigned players.
    /// </summary>
    public static string? SlotForTeamNum(int teamNum, string team1Side)
    {
        bool team1IsCt = team1Side == "CT";
        return teamNum switch
        {
            3 => team1IsCt ? Team1 : Team2,
            2 => team1IsCt ? Team2 : Team1,
            _ => null,
        };
    }

    /// <summary>
    /// Clamps the requested simulation host_timescale to the supported range.
    /// Invalid values (NaN/Infinity) fall back to 1.0.
    /// </summary>
    public static float ClampSimulationTimeScale(float requested)
    {
        if (float.IsNaN(requested) || float.IsInfinity(requested)) return 1.0f;
        if (requested < MinSimulationTimeScale) return MinSimulationTimeScale;
        if (requested > MaxSimulationTimeScale) return MaxSimulationTimeScale;
        return requested;
    }
}

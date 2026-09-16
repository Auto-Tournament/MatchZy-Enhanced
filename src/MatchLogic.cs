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
    /// Whether a map tied on score must still produce a winner (no draws).
    /// - overtimeMode "disabled" with overtimeSegments missing or 0: no OT, no draws.
    /// - overtime enabled with overtimeSegments > 0: no draws after the capped OT.
    /// Anything else keeps the legacy behavior where a tied map is a draw.
    /// </summary>
    public static bool DrawsDisallowed(string? overtimeMode, int? overtimeSegments)
    {
        bool overtimeDisabled = !string.IsNullOrWhiteSpace(overtimeMode) &&
            overtimeMode.Equals("disabled", StringComparison.OrdinalIgnoreCase);
        if (overtimeDisabled) return !overtimeSegments.HasValue || overtimeSegments.Value == 0;
        return overtimeSegments.HasValue && overtimeSegments.Value > 0;
    }

    /// <summary>Per-team aggregates used to break a tied map, in priority order.</summary>
    public readonly record struct TiebreakTotals(int Damage, int Kills, int HeadshotKills, int UtilityDamage);

    public const string CoinFlipCriterion = "coin_flip";

    /// <summary>
    /// Resolves a map tied on score. Criteria are compared in order: damage, kills,
    /// headshot kills, utility damage. When all are equal and draws are disallowed,
    /// a deterministic coin flip seeded by match id and map number picks the winner,
    /// so the result is reproducible and the map is never recorded as a draw.
    /// When draws are allowed and every criterion ties, returns a null slot (draw).
    /// </summary>
    public static (string? Slot, string Criterion) ResolveTiedMap(
        TiebreakTotals team1, TiebreakTotals team2, bool drawsDisallowed, long matchId, int mapNumber)
    {
        (string name, int a, int b)[] criteria =
        {
            ("damage", team1.Damage, team2.Damage),
            ("kills", team1.Kills, team2.Kills),
            ("headshot_kills", team1.HeadshotKills, team2.HeadshotKills),
            ("utility_damage", team1.UtilityDamage, team2.UtilityDamage),
        };
        foreach (var (name, a, b) in criteria)
        {
            if (a > b) return (Team1, name);
            if (b > a) return (Team2, name);
        }

        if (!drawsDisallowed) return (null, "none");
        return (CoinFlipSlot(matchId, mapNumber), CoinFlipCriterion);
    }

    /// <summary>
    /// Deterministic coin flip for (matchId, mapNumber). Uses a SplitMix64 finalizer
    /// rather than GetHashCode, which is randomized per process.
    /// </summary>
    public static string CoinFlipSlot(long matchId, int mapNumber)
    {
        ulong z = unchecked((ulong)matchId * 0x9E3779B97F4A7C15UL + (ulong)(uint)mapNumber + 0x632BE59BD9B4E019UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;
        return (z & 1UL) == 0 ? Team1 : Team2;
    }

    /// <summary>
    /// Maps left in the series after map index <paramref name="currentMapNumber"/> (0-based)
    /// finished. Counts every played map, including drawn ones, so it never points past the
    /// map list. The old formula (numMaps - series wins) ignored draws.
    /// </summary>
    public static int RemainingMaps(int numMaps, int maplistCount, int currentMapNumber)
    {
        int total = maplistCount > 0 ? Math.Min(numMaps, maplistCount) : numMaps;
        return Math.Max(0, total - (currentMapNumber + 1));
    }

    /// <summary>
    /// Whether the series is over after a map. Ends when no maps are left, or when
    /// clinching is enabled and a team has the majority of the series' maps.
    /// </summary>
    public static bool IsSeriesOver(int numMaps, int remainingMaps, int team1SeriesScore, int team2SeriesScore, bool seriesCanClinch)
    {
        if (remainingMaps <= 0) return true;
        if (!seriesCanClinch) return false;
        int mapsToWin = (numMaps / 2) + 1;
        return team1SeriesScore >= mapsToWin || team2SeriesScore >= mapsToWin;
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

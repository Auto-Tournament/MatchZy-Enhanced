namespace MatchZy;

/// <summary>
/// Pure rules for the tournament status convars (matchzy_tournament_status /
/// matchzy_tournament_match), kept free of CounterStrikeSharp so they can be unit tested.
///
/// MAT decides whether a server is free from these convars: "idle", or "warmup" with no
/// match, is free. So whenever the plugin is back in an idle state the match convar has to
/// be empty, otherwise the server keeps reporting the last match and stays "busy" until a
/// full server restart.
/// </summary>
public static class TournamentStatusLogic
{
    public const string Idle = "idle";
    public const string Warmup = "warmup";
    public const string Loading = "loading";

    /// <summary>
    /// A match is bound to the server while it is set up, and while a load is in progress
    /// (LoadMatchFromJSON publishes "loading" and starts warmup before isMatchSetup flips).
    /// </summary>
    public static bool HasActiveMatch(bool isMatchSetup, string? currentStatus, string? newStatus)
    {
        return isMatchSetup
            || IsStatus(currentStatus, Loading)
            || IsStatus(newStatus, Loading);
    }

    /// <summary>
    /// The value matchzy_tournament_match should hold after publishing <paramref name="newStatus"/>.
    /// - "idle" never carries a match.
    /// - With no active match (after a reset, series end, autostart warmup) the match is cleared.
    /// - Otherwise an explicit slug replaces the current one, and an empty slug keeps it.
    /// </summary>
    public static string ResolveMatchValue(string? newStatus, string? requestedMatch, string? currentMatch, bool hasActiveMatch)
    {
        if (IsStatus(newStatus, Idle)) return "";
        if (!hasActiveMatch) return "";
        if (!string.IsNullOrEmpty(requestedMatch)) return requestedMatch;
        return currentMatch ?? "";
    }

    /// <summary>
    /// Status to publish when the plugin has no match: "warmup" while the server sits in
    /// warmup (autostart), "idle" otherwise.
    /// </summary>
    public static string IdleStatus(bool isWarmup) => isWarmup ? Warmup : Idle;

    private static bool IsStatus(string? value, string expected)
    {
        return string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }
}

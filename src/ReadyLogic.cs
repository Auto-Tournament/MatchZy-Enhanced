namespace AutoTournamentCS2;

/// <summary>
/// Pure ready-check rules with no CounterStrikeSharp dependency, so they can be
/// unit tested outside the game server (see tests/AutoTournamentCS2.Tests).
///
/// Team numbers are CS2's own: 1 = Spectator, 2 = Terrorist, 3 = CT. The
/// [IsTeamReady] log line prints these raw numbers, which is why a log shows
/// "team: 1", "team: 2" and "team: 3" — that is not a team1/team2 mix-up.
/// </summary>
public static class ReadyLogic
{
    public const int TeamSpectator = 1;
    public const int TeamTerrorist = 2;
    public const int TeamCounterTerrorist = 3;

    public static string TeamLabel(int team) => team switch
    {
        TeamSpectator => "Spectator",
        TeamTerrorist => "T",
        TeamCounterTerrorist => "CT",
        _ => "None",
    };

    /// <summary>
    /// Whether one side passes the ready check.
    /// - Spectators with no minimum are always ready.
    /// - While the ready system is up, a side with nobody on it is never ready.
    /// - The roster must be full (minPlayers).
    /// - minReady 0 means everyone on the side must be ready; N means at least N.
    /// - A force-ready passes the ready count but never the roster requirement.
    /// </summary>
    public static bool IsTeamReady(
        int team,
        int playerCount,
        int readyCount,
        int minPlayers,
        int minReady,
        bool readyAvailable,
        bool forcedReady)
    {
        if (team == TeamSpectator && minReady == 0) return true;
        if (readyAvailable && playerCount == 0) return false;
        if (playerCount < minPlayers) return false;

        if (minReady <= 0)
        {
            if (playerCount == readyCount) return true;
        }
        else if (readyCount >= minReady)
        {
            return true;
        }

        return forcedReady;
    }

    /// <summary>
    /// Whether a player who just typed .ready / !ready has to be (re)registered in
    /// the player map before the ready check runs. The ready count only looks at
    /// the player map, so a player missing from it — or tracked under a stale
    /// controller that is no longer valid — readied up without ever being counted
    /// (logs: playerCount:0, readyCount:0, match never goes live).
    /// Bots and GOTV are registered by their own flows and are left alone here.
    /// </summary>
    public static bool NeedsTrackingOnReady(bool isHltv, bool isBot, bool isTracked, bool trackedEntryValid)
    {
        if (isHltv || isBot) return false;
        return !isTracked || !trackedEntryValid;
    }
}

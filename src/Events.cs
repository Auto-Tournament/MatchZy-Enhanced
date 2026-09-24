using System.Text.Json.Serialization;

namespace AutoTournamentCS2;
public class AutoTournamentCS2Event
{
    public AutoTournamentCS2Event(string eventName)
    {
        EventName = eventName;
    }

    [JsonPropertyName("event")]
    public string EventName { get; }
}

public class AutoTournamentCS2MatchEvent : AutoTournamentCS2Event
{
    [JsonPropertyName("matchid")]
    public required long MatchId { get; init; }

    protected AutoTournamentCS2MatchEvent(string eventName) : base(eventName)
    {
    }
}

public class AutoTournamentCS2MatchTeamEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("team")]
    public required string Team { get; init; }

    protected AutoTournamentCS2MatchTeamEvent(string eventName) : base(eventName)
    {
    }
}

public class AutoTournamentCS2MapEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("map_number")]
    public required int MapNumber { get; init; }

    protected AutoTournamentCS2MapEvent(string eventName) : base(eventName)
    {
    }
}

public class AutoTournamentCS2MapTeamEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("team_int")]
    public required int TeamNumber { get; init; }

    protected AutoTournamentCS2MapTeamEvent(string eventName) : base(eventName)
    {
    }
}

public class AutoTournamentCS2RoundEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("round_number")]
    public required int RoundNumber { get; init; }

    protected AutoTournamentCS2RoundEvent(string eventName) : base(eventName)
    {
    }
}

public class AutoTournamentCS2TimedRoundEvent : AutoTournamentCS2RoundEvent
{
    [JsonPropertyName("round_time")]
    public required int RoundTime { get; init; }

    protected AutoTournamentCS2TimedRoundEvent(string eventName) : base(eventName)
    {
    }
}

public class AutoTournamentCS2PlayerRoundEvent : AutoTournamentCS2RoundEvent
{

    [JsonPropertyName("player")]
    public required int Player { get; init; }

    protected AutoTournamentCS2PlayerRoundEvent(string eventName) : base(eventName)
    {
    }
}

public class AutoTournamentCS2PlayerTimedRoundEvent : AutoTournamentCS2TimedRoundEvent
{
    [JsonPropertyName("player")]
    public required int Player { get; init; }

    protected AutoTournamentCS2PlayerTimedRoundEvent(string eventName) : base(eventName)
    {
    }
}

public class AutoTournamentCS2PlayerConnectedEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("player")]
    public required AutoTournamentCS2PlayerInfo Player { get; init; }

    public AutoTournamentCS2PlayerConnectedEvent() : base("player_connect")
    {
    }
}

public class AutoTournamentCS2PlayerDisconnectedEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("player")]
    public required AutoTournamentCS2PlayerInfo Player { get; init; }

    public AutoTournamentCS2PlayerDisconnectedEvent() : base("player_disconnect")
    {
    }
}

public class AutoTournamentCS2BackupLoadedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("round_number")]
    public required int RoundNumber { get; init; }

    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    public AutoTournamentCS2BackupLoadedEvent() : base("backup_loaded")
    {
    }
}

public class AutoTournamentCS2SeriesStartedEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("team1")]
    public required AutoTournamentCS2TeamWrapper Team1 { get; init; }

    [JsonPropertyName("team2")]
    public required AutoTournamentCS2TeamWrapper Team2 { get; init; }

    [JsonPropertyName("num_maps")]
    public required int NumberOfMaps { get; init; }

    public AutoTournamentCS2SeriesStartedEvent() : base("series_start")
    {
    }
}

public class AutoTournamentCS2SeriesResultEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("time_until_restore")]
    public required int TimeUntilRestore { get; init; }

    [JsonPropertyName("winner")]
    public required Winner Winner { get; init; }

    [JsonPropertyName("team1_series_score")]
    public required int Team1SeriesScore { get; init; }

    [JsonPropertyName("team2_series_score")]
    public required int Team2SeriesScore { get; init; }

    public AutoTournamentCS2SeriesResultEvent() : base("series_end")
    {
    }
}

public class GoingLiveEvent : AutoTournamentCS2MapEvent
{
    public GoingLiveEvent() : base("going_live")
    {
    }
}

public class AutoTournamentCS2RoundEndedEvent : AutoTournamentCS2TimedRoundEvent
{

    [JsonPropertyName("reason")]
    public required int Reason { get; init; }

    [JsonPropertyName("winner")]
    public required Winner Winner { get; init; }

    [JsonPropertyName("team1")]
    public required AutoTournamentCS2StatsTeam StatsTeam1 { get; init; }

    [JsonPropertyName("team2")]
    public required AutoTournamentCS2StatsTeam StatsTeam2 { get; init; }

    public AutoTournamentCS2RoundEndedEvent() : base("round_end")
    {
    }
}

public class MapResultEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("winner")]
    public required Winner Winner { get; init; }

    [JsonPropertyName("team1")]
    public required AutoTournamentCS2StatsTeam StatsTeam1 { get; init; }

    [JsonPropertyName("team2")]
    public required AutoTournamentCS2StatsTeam StatsTeam2 { get; init; }

    public MapResultEvent() : base("map_result")
    {
    }
}

public class AutoTournamentCS2DemoUploadedEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("map_number")]
    public required int MapNumber { get; init; }

    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    public AutoTournamentCS2DemoUploadedEvent() : base("demo_upload_ended")
    {
    }
}

// ============================================================================
// Demo lifecycle events (recording + upload progress)
// ============================================================================

public class AutoTournamentCS2DemoRecordingStartedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    public AutoTournamentCS2DemoRecordingStartedEvent() : base("demo_recording_start")
    {
    }
}

public class AutoTournamentCS2DemoRecordingStoppedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    public AutoTournamentCS2DemoRecordingStoppedEvent() : base("demo_recording_stop")
    {
    }
}

public class AutoTournamentCS2DemoUploadStartedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    [JsonPropertyName("size_mb")]
    public required double SizeMB { get; init; }

    public AutoTournamentCS2DemoUploadStartedEvent() : base("demo_upload_start")
    {
    }
}

public class AutoTournamentCS2DemoUploadSuccessEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    [JsonPropertyName("size_mb")]
    public required double SizeMB { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    public AutoTournamentCS2DemoUploadSuccessEvent() : base("demo_upload_success")
    {
    }
}

public class AutoTournamentCS2DemoUploadFailEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    [JsonPropertyName("size_mb")]
    public double? SizeMB { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    public AutoTournamentCS2DemoUploadFailEvent() : base("demo_upload_fail")
    {
    }
}

// Player Ready System Events
public class AutoTournamentCS2PlayerReadyEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("player")]
    public required AutoTournamentCS2PlayerInfo Player { get; init; }

    [JsonPropertyName("team")]
    public required string Team { get; init; }

    [JsonPropertyName("ready_count_team1")]
    public required int ReadyCountTeam1 { get; init; }

    [JsonPropertyName("ready_count_team2")]
    public required int ReadyCountTeam2 { get; init; }

    [JsonPropertyName("total_ready")]
    public required int TotalReady { get; init; }

    [JsonPropertyName("expected_total")]
    public required int ExpectedTotal { get; init; }

    public AutoTournamentCS2PlayerReadyEvent() : base("player_ready")
    {
    }
}

public class AutoTournamentCS2PlayerUnreadyEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("player")]
    public required AutoTournamentCS2PlayerInfo Player { get; init; }

    [JsonPropertyName("team")]
    public required string Team { get; init; }

    [JsonPropertyName("ready_count_team1")]
    public required int ReadyCountTeam1 { get; init; }

    [JsonPropertyName("ready_count_team2")]
    public required int ReadyCountTeam2 { get; init; }

    [JsonPropertyName("total_ready")]
    public required int TotalReady { get; init; }

    [JsonPropertyName("expected_total")]
    public required int ExpectedTotal { get; init; }

    public AutoTournamentCS2PlayerUnreadyEvent() : base("player_unready")
    {
    }
}

public class AutoTournamentCS2TeamReadyEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("team")]
    public required string Team { get; init; }

    [JsonPropertyName("ready_count")]
    public required int ReadyCount { get; init; }

    [JsonPropertyName("total_ready")]
    public required int TotalReady { get; init; }

    [JsonPropertyName("expected_total")]
    public required int ExpectedTotal { get; init; }

    public AutoTournamentCS2TeamReadyEvent() : base("team_ready")
    {
    }
}

public class AutoTournamentCS2AllPlayersReadyEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("ready_count_team1")]
    public required int ReadyCountTeam1 { get; init; }

    [JsonPropertyName("ready_count_team2")]
    public required int ReadyCountTeam2 { get; init; }

    [JsonPropertyName("total_ready")]
    public required int TotalReady { get; init; }

    [JsonPropertyName("countdown_started")]
    public required bool CountdownStarted { get; init; }

    public AutoTournamentCS2AllPlayersReadyEvent() : base("all_players_ready")
    {
    }
}

// Match Phase Change Events
public class AutoTournamentCS2WarmupEndedEvent : AutoTournamentCS2MapEvent
{
    public AutoTournamentCS2WarmupEndedEvent() : base("warmup_ended")
    {
    }
}

public class AutoTournamentCS2KnifeRoundStartedEvent : AutoTournamentCS2MapEvent
{
    public AutoTournamentCS2KnifeRoundStartedEvent() : base("knife_round_started")
    {
    }
}

public class AutoTournamentCS2KnifeRoundEndedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("winner")]
    public required string Winner { get; init; }

    public AutoTournamentCS2KnifeRoundEndedEvent() : base("knife_round_ended")
    {
    }
}

// Pause System Events
public class AutoTournamentCS2PauseRequestedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("requested_by")]
    public required AutoTournamentCS2PlayerInfo RequestedBy { get; init; }

    [JsonPropertyName("is_tactical")]
    public required bool IsTactical { get; init; }

    [JsonPropertyName("is_admin")]
    public required bool IsAdmin { get; init; }

    public AutoTournamentCS2PauseRequestedEvent() : base("pause_requested")
    {
    }
}

public class AutoTournamentCS2MatchPausedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("paused_by")]
    public required AutoTournamentCS2PlayerInfo PausedBy { get; init; }

    [JsonPropertyName("is_tactical")]
    public required bool IsTactical { get; init; }

    [JsonPropertyName("is_admin")]
    public required bool IsAdmin { get; init; }

    [JsonPropertyName("pause_time")]
    public required long PauseTime { get; init; }

    public AutoTournamentCS2MatchPausedEvent() : base("match_paused")
    {
    }
}

public class AutoTournamentCS2UnpauseRequestedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("team")]
    public required string Team { get; init; }

    [JsonPropertyName("teams_ready")]
    public required int TeamsReady { get; init; }

    [JsonPropertyName("teams_needed")]
    public required int TeamsNeeded { get; init; }

    public AutoTournamentCS2UnpauseRequestedEvent() : base("unpause_requested")
    {
    }
}

public class AutoTournamentCS2MatchUnpausedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("pause_duration")]
    public required long PauseDuration { get; init; }

    public AutoTournamentCS2MatchUnpausedEvent() : base("match_unpaused")
    {
    }
}

// Round and Game State Events
public class AutoTournamentCS2RoundStartedEvent : AutoTournamentCS2RoundEvent
{
    [JsonPropertyName("team1_score")]
    public required int Team1Score { get; init; }

    [JsonPropertyName("team2_score")]
    public required int Team2Score { get; init; }

    public AutoTournamentCS2RoundStartedEvent() : base("round_started")
    {
    }
}

public class AutoTournamentCS2HalftimeStartedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("team1_score")]
    public required int Team1Score { get; init; }

    [JsonPropertyName("team2_score")]
    public required int Team2Score { get; init; }

    public AutoTournamentCS2HalftimeStartedEvent() : base("halftime_started")
    {
    }
}

public class AutoTournamentCS2OvertimeStartedEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("overtime_number")]
    public required int OvertimeNumber { get; init; }

    public AutoTournamentCS2OvertimeStartedEvent() : base("overtime_started")
    {
    }
}

public class AutoTournamentCS2SideSwapEvent : AutoTournamentCS2MapEvent
{
    [JsonPropertyName("team1_side")]
    public required string Team1Side { get; init; }

    [JsonPropertyName("team2_side")]
    public required string Team2Side { get; init; }

    public AutoTournamentCS2SideSwapEvent() : base("side_swap")
    {
    }
}

// Test Event
public class AutoTournamentCS2ServerConfiguredEvent : AutoTournamentCS2Event
{
    [JsonPropertyName("server_id")]
    public required string ServerId { get; init; }

    [JsonPropertyName("hostname")]
    public required string Hostname { get; init; }

    [JsonPropertyName("plugin_version")]
    public required string PluginVersion { get; init; }

    [JsonPropertyName("remote_log_url")]
    public required string RemoteLogUrl { get; init; }

    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; }

    [JsonPropertyName("configured_by")]
    public required string ConfiguredBy { get; init; }

    public AutoTournamentCS2ServerConfiguredEvent() : base("server_configured")
    {
    }
}

public class AutoTournamentCS2ServerHealthEvent : AutoTournamentCS2Event
{
    [JsonPropertyName("server_id")]
    public required string ServerId { get; init; }

    [JsonPropertyName("plugin_version")]
    public required string PluginVersion { get; init; }

    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; }

    [JsonPropertyName("db_ok")]
    public required bool DbOk { get; init; }

    [JsonPropertyName("db_type")]
    public required string DbType { get; init; } // "sqlite" | "mysql"

    [JsonPropertyName("db_error")]
    public string? DbError { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; } // "startup" | "periodic" | "change"

    public AutoTournamentCS2ServerHealthEvent() : base("server_health")
    {
    }
}

public class AutoTournamentCS2TestEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; }

    [JsonPropertyName("triggered_by")]
    public required string TriggeredBy { get; init; }

    // Optional metadata to help external controllers attribute connectivity checks
    // cleanly per server and per logical match slug.
    [JsonPropertyName("server_id")]
    public string? ServerId { get; init; }

    [JsonPropertyName("match_slug")]
    public string? MatchSlug { get; init; }

    public AutoTournamentCS2TestEvent() : base("test_event")
    {
    }
}

// CS2 update status events (server-level)
public class AutoTournamentCS2Cs2UpdateRequiredEvent : AutoTournamentCS2MatchEvent
{
    [JsonPropertyName("server_id")]
    public required string ServerId { get; init; }

    [JsonPropertyName("required_version")]
    public required int RequiredVersion { get; init; }

    // Best-effort marker so the API/admin UI can distinguish "update found"
    // vs "about to quit now".
    [JsonPropertyName("phase")]
    public string? Phase { get; init; }

    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; }

    public AutoTournamentCS2Cs2UpdateRequiredEvent() : base("cs2_update_required")
    {
    }
}
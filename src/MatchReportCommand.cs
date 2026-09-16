using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Newtonsoft.Json.Linq;

namespace MatchZy
{
public record MatchReportPayload
{
    public MatchReportMatch Match { get; init; } = new();
    public Dictionary<string, MatchReportTeam> Teams { get; init; } = new();
    public MatchReportSpectatorInfo Spectators { get; init; } = new();
    public List<MatchReportPlayerConnection> Connections { get; init; } = new();
    public MatchReportServer Server { get; init; } = new();
}

public record MatchReportMatch
{
    public long MatchId { get; init; }
    public string Slug { get; init; } = "";
    public string Phase { get; init; } = "";
    public MatchReportMap Map { get; init; } = new();
    public MatchReportScore Score { get; init; } = new();
    public bool Simulated { get; init; }
    public bool Paused { get; init; }
    public string PauseRequestedBy { get; init; } = "";
    public string PauseTeamSlot { get; init; } = "";
    public MatchReportReadyState Ready { get; init; } = new();
    public int ConnectedPlayers { get; init; }
}

public record MatchReportMap
{
    public string Name { get; init; } = "";
    public int Index { get; init; }
    public int Number { get; init; }
    public int Total { get; init; }
    public int Round { get; init; }
}

public record MatchReportScore
{
    public int Team1 { get; init; }
    public int Team2 { get; init; }
    public MatchReportSeriesScore Series { get; init; } = new();
}

public record MatchReportSeriesScore
{
    public int Team1 { get; init; }
    public int Team2 { get; init; }
}

public record MatchReportReadyState
{
    public bool ReadySystemActive { get; init; }
    public int ReadyPlayers { get; init; }
    public int TrackingPlayers { get; init; }
    public int MinimumRequired { get; init; }
}

public record MatchReportTeam
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Side { get; init; } = "";
    public int SeriesScore { get; init; }
    public int CurrentMapScore { get; init; }
    public int ConnectedCount { get; init; }
    public int ReadyCount { get; init; }
    public int ExpectedPlayers { get; init; }
    public List<MatchReportPlayerConnection> Players { get; init; } = new();
    public List<MatchReportRosterEntry> Roster { get; init; } = new();
}

public record MatchReportPlayerConnection
{
    public string SteamId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Slot { get; init; } = "";
    public string TeamSide { get; init; } = "";
    public bool Ready { get; init; }
    public long ConnectedAt { get; init; }
    public bool Connected { get; init; } = true;
    public bool Coach { get; init; }
}

public record MatchReportRosterEntry
{
    public string SteamId { get; init; } = "";
    public string Name { get; init; } = "";
}

public record MatchReportSpectatorInfo
{
    public List<MatchReportPlayerConnection> Connected { get; init; } = new();
    public List<MatchReportRosterEntry> Configured { get; init; } = new();
}

public record MatchReportServer
{
    public string MatchSlug { get; init; } = "";
    public long MatchId { get; init; }
    public long LastHeartbeat { get; init; }
    public string ModuleVersion { get; init; } = "";
    public string TournamentStatus { get; init; } = "";
    public string TournamentMatch { get; init; } = "";
    public long TournamentUpdated { get; init; }
    public string LoadedConfig { get; init; } = "";
    public string RemoteLogUrl { get; init; } = "";
}

public record MatchReportUploadEnvelope
{
    [JsonPropertyName("serverId")]
    public string ServerId { get; init; } = "";

    [JsonPropertyName("matchSlug")]
    public string? MatchSlug { get; init; }

    [JsonPropertyName("report")]
    public MatchReportPayload Report { get; init; } = new();
}

public partial class MatchZy : BasePlugin
{
    private static readonly JsonSerializerOptions MatchReportSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    [ConsoleCommand("matchzy_match_report", "Returns a structured JSON snapshot of the current match state")]
    [ConsoleCommand("css_match_report", "Returns a structured JSON snapshot of the current match state")]
    public void OnMatchReportCommand(CCSPlayerController? player, CommandInfo command)
    {
        // Schedule on main thread to avoid "Invoked on a non-main thread" errors
        Server.NextFrame(() =>
        {
            MatchReportPayload? payload = null;
            try
            {
                payload = BuildMatchReport();
            }
            catch (Exception e)
            {
                Log($"[MatchReport] Failed to build report: {e.Message}");
                command.ReplyToCommand(JsonSerializer.Serialize(new { error = "match_report_failed", reason = e.Message }, MatchReportSerializerOptions));
                return;
            }

            // Read ConVar values on main thread before starting async work
            string endpoint = matchReportEndpoint.Value;
            string serverId = matchReportServerId.Value;
            string token = matchReportToken.Value;

            // Run async upload on background thread, but ensure all CSSharp native calls are wrapped
            Task.Run(async () =>
            {
                try
                {
                    bool uploaded = await UploadMatchReport(payload, endpoint, serverId, token, fallbackToConsole: true);
                    
                    // Schedule reply on main thread
                    Server.NextFrame(() =>
                    {
                        if (!uploaded)
                        {
                            command.ReplyToCommand(JsonSerializer.Serialize(payload, MatchReportSerializerOptions));
                        }
                    });
                }
                catch (Exception e)
                {
                    Log($"[MatchReport] Failed to upload report: {e.Message}");
                    Server.NextFrame(() =>
                    {
                        command.ReplyToCommand(JsonSerializer.Serialize(new { error = "match_report_upload_failed", reason = e.Message }, MatchReportSerializerOptions));
                    });
                }
            });
        });
    }

    private MatchReportPayload BuildMatchReport()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        (int team1Score, int team2Score) = GetTeamsScore();
        List<MatchReportPlayerConnection> connections = BuildConnectionSnapshots(now);
        string mapName = !string.IsNullOrWhiteSpace(Server.MapName)
            ? Server.MapName
            : (matchConfig.Maplist.Count > matchConfig.CurrentMapNumber
                ? matchConfig.Maplist[matchConfig.CurrentMapNumber]
                : "unknown");

        string slug = GetActiveMatchSlug();
        string pauseRequestedBy = unpauseData.TryGetValue("pauseTeam", out var pauseSourceObj)
            ? pauseSourceObj?.ToString() ?? ""
            : "";
        string pauseTeamSlot = MapPauseSourceToSlot(pauseRequestedBy);

        MatchReportReadyState readyState = new()
        {
            ReadySystemActive = readyAvailable,
            ReadyPlayers = playerReadyStatus.Count(kv => kv.Value),
            TrackingPlayers = playerReadyStatus.Count,
            MinimumRequired = minimumReadyRequired
        };

        MatchReportMatch matchSection = new()
        {
            MatchId = liveMatchId,
            Slug = slug,
            Phase = GetMatchPhaseLabel(),
            Map = new MatchReportMap
            {
                Name = mapName,
                Index = matchConfig.CurrentMapNumber,
                Number = matchConfig.CurrentMapNumber + 1,
                Total = matchConfig.NumMaps,
                Round = GetRoundNumer()
            },
            Score = new MatchReportScore
            {
                Team1 = team1Score,
                Team2 = team2Score,
                Series = new MatchReportSeriesScore
                {
                    Team1 = matchzyTeam1.seriesScore,
                    Team2 = matchzyTeam2.seriesScore
                }
                },
                Paused = isPaused,
                PauseRequestedBy = pauseRequestedBy,
                PauseTeamSlot = pauseTeamSlot,
                Ready = readyState,
                ConnectedPlayers = connections.Count,
                Simulated = isSimulationMode
        };

        var team1Connections = connections.Where(c => c.Slot == "team1").ToList();
        var team2Connections = connections.Where(c => c.Slot == "team2").ToList();

        Dictionary<string, MatchReportTeam> teamsSection = new()
        {
            ["team1"] = BuildTeamReport(matchzyTeam1, "team1", team1Score, team1Connections),
            ["team2"] = BuildTeamReport(matchzyTeam2, "team2", team2Score, team2Connections)
        };

        MatchReportSpectatorInfo spectators = new()
        {
            Connected = connections.Where(c => c.Slot == "spectator").ToList(),
            Configured = BuildRosterEntries(matchConfig.Spectators)
        };

        MatchReportPayload payload = new()
        {
            Match = matchSection,
            Teams = teamsSection,
            Spectators = spectators,
            Connections = connections,
            Server = BuildServerReport(slug, now)
        };

        return payload;
    }

    private MatchReportTeam BuildTeamReport(Team team, string slot, int currentMapScore, List<MatchReportPlayerConnection> connections)
    {
        string side = teamSides.TryGetValue(team, out var assignedSide) ? assignedSide.ToLowerInvariant() : slot;
        return new MatchReportTeam
        {
            Id = team.id,
            Name = team.teamName,
            Side = side,
            SeriesScore = team.seriesScore,
            CurrentMapScore = currentMapScore,
            ConnectedCount = connections.Count,
            ReadyCount = connections.Count(c => c.Ready),
            ExpectedPlayers = matchConfig.PlayersPerTeam,
            Players = connections,
            Roster = BuildRosterEntries(team.teamPlayers)
        };
    }

    private List<MatchReportPlayerConnection> BuildConnectionSnapshots(long fallbackTimestamp)
    {
        List<MatchReportPlayerConnection> snapshot = new();

        foreach (var entry in playerData.ToList())
        {
            CCSPlayerController player = entry.Value;
            if (!IsPlayerValid(player)) continue;

            string slot = ResolvePlayerSlot(player);
            bool readyFlag = playerReadyStatus.TryGetValue(entry.Key, out bool readyState) && readyState;
            bool isCoach = matchzyTeam1.coach.Contains(player) || matchzyTeam2.coach.Contains(player);
            long connectedAt = playerConnectionTimes.TryGetValue(player.SteamID, out long timestamp)
                ? timestamp
                : fallbackTimestamp;

            string steamId = player.SteamID.ToString();
            string name = player.PlayerName;

            if (isSimulationMode && player.UserId.HasValue &&
                simulationPlayersByUserId.TryGetValue(player.UserId.Value, out var identity))
            {
                steamId = identity.ConfigSteamId;
                name = identity.ConfigName;
                slot = identity.TeamSlot;
            }

            snapshot.Add(new MatchReportPlayerConnection
            {
                SteamId = steamId,
                Name = name,
                Slot = slot,
                TeamSide = ResolvePlayerTeamSide(slot),
                Ready = readyFlag,
                ConnectedAt = connectedAt,
                Coach = isCoach
            });
        }

        return snapshot;
    }

    private string ResolvePlayerSlot(CCSPlayerController player)
    {
        if (matchzyTeam1.coach.Contains(player)) return "team1";
        if (matchzyTeam2.coach.Contains(player)) return "team2";

        if (isSimulationMode && player.UserId.HasValue &&
            simulationPlayersByUserId.TryGetValue(player.UserId.Value, out var identity))
        {
            return identity.TeamSlot;
        }

        string steamId = player.SteamID.ToString();
        if (PlayerIsInConfig(matchzyTeam1.teamPlayers, steamId)) return "team1";
        if (PlayerIsInConfig(matchzyTeam2.teamPlayers, steamId)) return "team2";

        if (player.Team == CsTeam.CounterTerrorist && reverseTeamSides.TryGetValue("CT", out var ctTeam))
        {
            return ctTeam == matchzyTeam1 ? "team1" : "team2";
        }

        if (player.Team == CsTeam.Terrorist && reverseTeamSides.TryGetValue("TERRORIST", out var tTeam))
        {
            return tTeam == matchzyTeam1 ? "team1" : "team2";
        }

        if (player.Team == CsTeam.Spectator) return "spectator";

        return "unknown";
    }

    private static bool PlayerIsInConfig(JToken? teamConfig, string steamId)
    {
        try
        {
            return teamConfig?[steamId] != null;
        }
        catch
        {
            return false;
        }
    }

    private string ResolvePlayerTeamSide(string slot)
    {
        return slot switch
        {
            "team1" => teamSides.TryGetValue(matchzyTeam1, out var ctSide) ? ctSide.ToLowerInvariant() : "ct",
            "team2" => teamSides.TryGetValue(matchzyTeam2, out var tSide) ? tSide.ToLowerInvariant() : "t",
            "spectator" => "spectator",
            _ => "unknown"
        };
    }

    private static List<MatchReportRosterEntry> BuildRosterEntries(JToken? teamConfig)
    {
        List<MatchReportRosterEntry> roster = new();
        if (teamConfig == null) return roster;

        if (teamConfig is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                roster.Add(new MatchReportRosterEntry
                {
                    SteamId = property.Name,
                    Name = property.Value?.ToString() ?? ""
                });
            }
        }
        else if (teamConfig is JArray array)
        {
            foreach (var token in array)
            {
                roster.Add(new MatchReportRosterEntry
                {
                    Name = token?.ToString() ?? ""
                });
            }
        }

        return roster;
    }

    private MatchReportServer BuildServerReport(string slug, long heartbeatTimestamp)
    {
        long.TryParse(tournamentUpdated.Value, out long tournamentTimestamp);
        return new MatchReportServer
        {
            MatchSlug = slug,
            MatchId = liveMatchId,
            LastHeartbeat = heartbeatTimestamp,
            ModuleVersion = ModuleVersion,
            TournamentStatus = tournamentStatus.Value,
            TournamentMatch = tournamentMatch.Value,
            TournamentUpdated = tournamentTimestamp,
            LoadedConfig = loadedConfigFile,
            RemoteLogUrl = matchConfig.RemoteLogURL
        };
    }

    private string GetMatchPhaseLabel()
    {
        if (!isMatchSetup)
        {
            return "idle";
        }

        if (isRoundRestoring) return "round_restore";
        if (isPaused) return "paused";
        if (IsPostGamePhase()) return "postgame";
        if (isSideSelectionPhase) return "knife_decision";
        if (isKnifeRound) return "knife";
        if (isMatchLive) return "live";
        if (matchStarted) return "going_live";
        if (isWarmup) return "warmup";
        return "setup";
    }

    private string GetActiveMatchSlug()
    {
        if (!string.IsNullOrWhiteSpace(tournamentMatch.Value))
        {
            return tournamentMatch.Value;
        }

        if (!string.IsNullOrWhiteSpace(loadedConfigFile))
        {
            return loadedConfigFile;
        }

        return liveMatchId > 0 ? $"match-{liveMatchId}" : "unassigned";
    }

    private string MapPauseSourceToSlot(string pauseSource)
    {
        if (string.IsNullOrWhiteSpace(pauseSource)) return "";

        if (reverseTeamSides.TryGetValue("CT", out var ctTeam) && pauseSource.Equals(ctTeam.teamName, StringComparison.OrdinalIgnoreCase))
        {
            return ctTeam == matchzyTeam1 ? "team1" : "team2";
        }

        if (reverseTeamSides.TryGetValue("TERRORIST", out var tTeam) && pauseSource.Equals(tTeam.teamName, StringComparison.OrdinalIgnoreCase))
        {
            return tTeam == matchzyTeam1 ? "team1" : "team2";
        }

        if (pauseSource.Equals("ct", StringComparison.OrdinalIgnoreCase))
        {
            return reverseTeamSides.TryGetValue("CT", out ctTeam) && ctTeam == matchzyTeam1 ? "team1" : "team2";
        }

        if (pauseSource.Equals("t", StringComparison.OrdinalIgnoreCase) ||
            pauseSource.Equals("terrorist", StringComparison.OrdinalIgnoreCase))
        {
            return reverseTeamSides.TryGetValue("TERRORIST", out tTeam) && tTeam == matchzyTeam1 ? "team1" : "team2";
        }

        return pauseSource.Equals("admin", StringComparison.OrdinalIgnoreCase) ? "admin" : "";
    }

    private async Task<bool> UploadMatchReport(MatchReportPayload payload, string endpoint, string serverId, string token, bool fallbackToConsole)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(serverId))
        {
            Log("[MatchReport] Upload skipped - report endpoint or server ID not configured");
            return false;
        }

        MatchReportUploadEnvelope envelope = new()
        {
            ServerId = serverId,
            MatchSlug = string.IsNullOrWhiteSpace(payload.Match.Slug) ? null : payload.Match.Slug,
            Report = payload
        };

        string jsonBody = JsonSerializer.Serialize(envelope, MatchReportSerializerOptions);

        using HttpClient client = new();

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Server.NextFrame(() => Server.PrintToConsole($"[MatchZy] Uploading match report (attempt {attempt}/{maxAttempts})..."));
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.TryAddWithoutValidation("x-matchzy-token", token);
                }

                HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
                string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (response.IsSuccessStatusCode && ResponseIndicatesSuccess(responseBody))
                {
                    Server.NextFrame(() => Server.PrintToConsole($"[MatchZy] Match report upload succeeded ({(int)response.StatusCode})"));
                    return true;
                }

                Log($"[MatchReport] Upload failed (attempt {attempt}) StatusCode: {(int)response.StatusCode}, Content: {SecretRedactor.RedactText(responseBody)}");
            }
            catch (Exception ex)
            {
                Log($"[MatchReport] Upload exception (attempt {attempt}): {ex.Message}");
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }

        if (fallbackToConsole)
        {
            Server.NextFrame(() => Server.PrintToConsole("[MatchZy] Match report upload failed after retries. Falling back to console output."));
        }
        return false;
    }

    private static bool ResponseIndicatesSuccess(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return true;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("success", out var successProp))
            {
                return successProp.ValueKind == JsonValueKind.True;
            }
        }
        catch
        {
            // ignore parse errors, treat as failure
        }

        return false;
    }
    private void TriggerMatchReportUpload(string reason)
    {
        if (string.IsNullOrWhiteSpace(matchReportEndpoint.Value) || string.IsNullOrWhiteSpace(matchReportServerId.Value))
        {
            return;
        }

        lock (matchReportUploadLock)
        {
            if (matchReportUploadScheduled)
            {
                return;
            }
            matchReportUploadScheduled = true;
        }

        Server.NextFrame(() =>
        {
            MatchReportPayload? payload = null;
            try
            {
                payload = BuildMatchReport();
            }
            catch (Exception ex)
            {
                Log($"[MatchReport] Failed to build report on main thread (trigger: {reason}) - {ex.Message}");
                lock (matchReportUploadLock)
                {
                    matchReportUploadScheduled = false;
                }
                return;
            }

            // Read ConVar values on main thread before starting async work
            string endpoint = matchReportEndpoint.Value;
            string serverId = matchReportServerId.Value;
            string token = matchReportToken.Value;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500);
                    bool uploaded = await UploadMatchReport(payload!, endpoint, serverId, token, fallbackToConsole: false);
                    if (!uploaded)
                    {
                        Log($"[MatchReport] Auto upload failed (trigger: {reason})");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[MatchReport] Auto upload exception (trigger: {reason}) - {ex.Message}");
                }
                finally
                {
                    lock (matchReportUploadLock)
                    {
                        matchReportUploadScheduled = false;
                    }
                }
            });
        });
    }
}
}


using CounterStrikeSharp.API.Core;

namespace AutoTournamentCS2;

public partial class AutoTournamentCS2
{
    /// <summary>
    /// Computes logical team1/team2 ready counts for event payloads.
    /// In simulation mode this uses the simulated identity mapping so that
    /// counts always reflect the configured match teams, independent of the
    /// underlying CS2 CT/T distribution.
    /// </summary>
    private void GetLogicalReadyCountsForEvents(out int readyCountTeam1, out int readyCountTeam2, out int totalReady)
    {
        readyCountTeam1 = 0;
        readyCountTeam2 = 0;

        // Prefer simulation-aware counts when available.
        if (isSimulationMode && simulationPlayersByUserId.Count > 0)
        {
            foreach (var kvp in simulationPlayersByUserId)
            {
                int userId = kvp.Key;
                var identity = kvp.Value;

                if (!playerReadyStatus.TryGetValue(userId, out bool isReady) || !isReady)
                {
                    continue;
                }

                if (identity.TeamSlot == "team1")
                {
                    readyCountTeam1++;
                }
                else if (identity.TeamSlot == "team2")
                {
                    readyCountTeam2++;
                }
            }

            totalReady = readyCountTeam1 + readyCountTeam2;
            return;
        }

        // Fallback: derive logical team counts from CT/T side counts and current side mapping.
        (int ctPlayerCount, int ctReadyCount) = GetTeamPlayerCount((int)CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist, false);
        (int tPlayerCount, int tReadyCount) = GetTeamPlayerCount((int)CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist, false);

        if (reverseTeamSides.ContainsKey("CT"))
        {
            bool ctIsTeam1 = reverseTeamSides["CT"] == atTeam1;
            if (ctIsTeam1)
            {
                readyCountTeam1 = ctReadyCount;
                readyCountTeam2 = tReadyCount;
            }
            else
            {
                readyCountTeam1 = tReadyCount;
                readyCountTeam2 = ctReadyCount;
            }
        }
        else
        {
            // If we somehow don't know which side team1 is on yet, treat CT as team1.
            readyCountTeam1 = ctReadyCount;
            readyCountTeam2 = tReadyCount;
        }

        totalReady = readyCountTeam1 + readyCountTeam2;
    }

    private void SendPlayerReadyEvent(CCSPlayerController player, bool isReady)
    {
        Log($"[SendPlayerReadyEvent] Called - isMatchSetup: {isMatchSetup}, readyAvailable: {readyAvailable}, RemoteLogURL: {matchConfig.RemoteLogURL}, isReady: {isReady}");
        
        if (!isMatchSetup)
        {
            Log($"[SendPlayerReadyEvent] Skipping - Match not setup");
            return;
        }
        
        if (string.IsNullOrEmpty(matchConfig.RemoteLogURL))
        {
            Log($"[SendPlayerReadyEvent] Skipping - RemoteLogURL not configured");
            return;
        }
        
        if (!readyAvailable)
        {
            Log($"[SendPlayerReadyEvent] Skipping - Ready system not available");
            return;
        }
        
        if (!player.UserId.HasValue)
        {
            Log($"[SendPlayerReadyEvent] Skipping - Player UserId is null");
            return;
        }

        // Expected total is players per team * 2
        int expectedTotal = matchConfig.PlayersPerTeam * 2;

        // Determine which team this player is on
        string teamName = "none";
        if (reverseTeamSides.ContainsKey("CT") && player.TeamNum == 3)
        {
            teamName = reverseTeamSides["CT"].teamName;
        }
        else if (reverseTeamSides.ContainsKey("TERRORIST") && player.TeamNum == 2)
        {
            teamName = reverseTeamSides["TERRORIST"].teamName;
        }

        var playerInfo = BuildPlayerInfo(player, teamName);

        // Compute logical ready counts for event payloads. In simulation mode this is
        // based on the simulated team slots (team1/team2) rather than raw CS sides,
        // so that the API always sees correct per-team readiness even if bots are
        // temporarily unbalanced between CT/T.
        GetLogicalReadyCountsForEvents(out int readyCountTeam1, out int readyCountTeam2, out int totalReady);

        if (isReady)
        {
            Log($"[SendPlayerReadyEvent] Creating player_ready event for {player.PlayerName}");
            
            var readyEvent = new AutoTournamentCS2PlayerReadyEvent
            {
                MatchId = liveMatchId,
                Player = playerInfo,
                Team = teamName,
                ReadyCountTeam1 = readyCountTeam1,
                ReadyCountTeam2 = readyCountTeam2,
                TotalReady = totalReady,
                ExpectedTotal = expectedTotal
            };

            Log($"[SendPlayerReadyEvent] Sending player_ready event to remote URL");
            Task.Run(async () => {
                await SendEventAsync(readyEvent);
            });

            // Check if team is now ready
            CheckAndSendTeamReadyEvent();
        }
        else
        {
            Log($"[SendPlayerReadyEvent] Creating player_unready event for {player.PlayerName}");
            
            var unreadyEvent = new AutoTournamentCS2PlayerUnreadyEvent
            {
                MatchId = liveMatchId,
                Player = playerInfo,
                Team = teamName,
                ReadyCountTeam1 = readyCountTeam1,
                ReadyCountTeam2 = readyCountTeam2,
                TotalReady = totalReady,
                ExpectedTotal = expectedTotal
            };

            Log($"[SendPlayerReadyEvent] Sending player_unready event to remote URL");
            Task.Run(async () => {
                await SendEventAsync(unreadyEvent);
            });
        }

        TriggerMatchReportUpload(isReady ? "player_ready" : "player_unready");
    }

    private void CheckAndSendTeamReadyEvent()
    {
        Log($"[CheckAndSendTeamReadyEvent] Called - isMatchSetup: {isMatchSetup}, readyAvailable: {readyAvailable}, RemoteLogURL configured: {!string.IsNullOrEmpty(matchConfig.RemoteLogURL)}");
        
        if (!isMatchSetup || !readyAvailable || string.IsNullOrEmpty(matchConfig.RemoteLogURL)) return;

        bool ctTeamReady = IsTeamReady((int)CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist);
        bool tTeamReady = IsTeamReady((int)CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist);

        // Side-based counts are still used for internal gating logic, but for events we
        // report logical team1/team2 counts (especially important in simulation mode).
        (int ctPlayerCount, int ctReadyCount) = GetTeamPlayerCount((int)CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist, false);
        (int tPlayerCount, int tReadyCount) = GetTeamPlayerCount((int)CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist, false);

        // Logical event-facing counts.
        GetLogicalReadyCountsForEvents(out int readyCountTeam1, out int readyCountTeam2, out int totalReady);
        int expectedTotal = matchConfig.PlayersPerTeam * 2;

        // Send team_ready event for CT team if ready
        if (ctTeamReady && reverseTeamSides.ContainsKey("CT"))
        {
            Log($"[CheckAndSendTeamReadyEvent] CT team is ready, sending team_ready event");
            
            var teamReadyEvent = new AutoTournamentCS2TeamReadyEvent
            {
                MatchId = liveMatchId,
                Team = reverseTeamSides["CT"] == atTeam1 ? "team1" : "team2",
                ReadyCount = reverseTeamSides["CT"] == atTeam1 ? readyCountTeam1 : readyCountTeam2,
                TotalReady = totalReady,
                ExpectedTotal = expectedTotal
            };

            Task.Run(async () => {
                await SendEventAsync(teamReadyEvent);
            });
        }

        // Send team_ready event for T team if ready
        if (tTeamReady && reverseTeamSides.ContainsKey("TERRORIST"))
        {
            Log($"[CheckAndSendTeamReadyEvent] T team is ready, sending team_ready event");
            
            var teamReadyEvent = new AutoTournamentCS2TeamReadyEvent
            {
                MatchId = liveMatchId,
                Team = reverseTeamSides["TERRORIST"] == atTeam1 ? "team1" : "team2",
                ReadyCount = reverseTeamSides["TERRORIST"] == atTeam1 ? readyCountTeam1 : readyCountTeam2,
                TotalReady = totalReady,
                ExpectedTotal = expectedTotal
            };

            Task.Run(async () => {
                await SendEventAsync(teamReadyEvent);
            });
        }

        // Send all_players_ready event if both teams are ready
        if (ctTeamReady && tTeamReady)
        {
            // In simulation mode, require that all configured simulated players are
            // ready before emitting all_players_ready, so the API/FE always sees
            // a full 10/10 ready snapshot instead of just min_players_to_ready.
            if (isSimulationMode && totalReady < expectedTotal)
            {
                Log($"[CheckAndSendTeamReadyEvent] Both sides flagged ready but only {totalReady}/{expectedTotal} simulated players ready; deferring all_players_ready.");
                return;
            }

            Log($"[CheckAndSendTeamReadyEvent] Both teams ready, sending all_players_ready event");
            
            var allPlayersReadyEvent = new AutoTournamentCS2AllPlayersReadyEvent
            {
                MatchId = liveMatchId,
                ReadyCountTeam1 = readyCountTeam1,
                ReadyCountTeam2 = readyCountTeam2,
                TotalReady = totalReady,
                CountdownStarted = true
            };

            Task.Run(async () => {
                await SendEventAsync(allPlayersReadyEvent);
            });
        }
    }
}


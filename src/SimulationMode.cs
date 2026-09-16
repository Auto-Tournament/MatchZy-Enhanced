using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Newtonsoft.Json.Linq;

namespace MatchZy;

// Internal identity for a simulated player: the "real" identity from the match JSON
// that a given in-game bot is representing.
internal record SimulationPlayerIdentity(string ConfigSteamId, string ConfigName, string TeamSlot);

public partial class MatchZy
{
    // Mapping from CS2 userId -> configured player identity for simulation mode.
    private readonly Dictionary<int, SimulationPlayerIdentity> simulationPlayersByUserId = new();

    // Flat pool of configured players across both teams.
    private readonly List<SimulationPlayerIdentity> simulationIdentityPool = new();

    // Tracks which configured SteamIDs have already been assigned to a bot.
    private readonly HashSet<string> assignedSimulationSteamIds = new();

    // Ensure we only start the simulated ready flow once per map.
    private bool simulationReadyFlowScheduled = false;

    // Tracks whether the per-map simulation flow (bot spawning, mapping, ready flow
    // scheduling) has already been started. This prevents double-initialization on
    // maps where both the map-start hook and a deferred EventRoundStart path call
    // MaybeStartSimulationFlow().
    private bool simulationFlowStarted = false;

    // Set once the simulated ready flow has run for this map; bots mapped after that are
    // readied straight away by the reconcile pass.
    private bool simulationReadyFlowRan = false;

    // Warmup watchdog: when the per-map flow started (real time), how often it has had to
    // reconcile, and whether a reconcile is already queued.
    private DateTime simulationWarmupStartedUtc = DateTime.MinValue;
    private CounterStrikeSharp.API.Modules.Timers.Timer? simulationWatchdogTimer = null;
    private int simulationWatchdogAttempts = 0;
    private bool simulationReconcilePending = false;

    private void ClearSimulationState()
    {
        simulationPlayersByUserId.Clear();
        simulationIdentityPool.Clear();
        assignedSimulationSteamIds.Clear();
        simulationReadyFlowScheduled = false;
        simulationReadyFlowRan = false;
        simulationFlowStarted = false;
        StopSimulationWatchdog();
        simulationReconcilePending = false;
        isSimulationMode = false;
    }

    private static bool IsConnectedSimulationBot(CCSPlayerController? player)
    {
        return player != null
            && player.IsValid
            && player.IsBot
            && !player.IsHLTV
            && player.UserId.HasValue
            && player.Connected == PlayerConnectedState.PlayerConnected;
    }

    /// <summary>
    /// The bot currently holding the roster slot for <paramref name="steamId"/>, looked up from the
    /// live engine controller rather than a remembered one (UserIds are reused by new bots).
    /// </summary>
    private CCSPlayerController? FindSimulationBotForSlot(string steamId)
    {
        foreach (var kv in simulationPlayersByUserId)
        {
            if (kv.Value.ConfigSteamId != steamId) continue;
            var controller = Utilities.GetPlayerFromUserid(kv.Key);
            if (IsConnectedSimulationBot(controller)) return controller;
        }
        return null;
    }

    /// <summary>
    /// Drops the mapping for a bot that left and frees its roster slot so another bot can take it.
    /// Previously the mapping was removed but the SteamID stayed "assigned", so the slot could
    /// never be filled again.
    /// </summary>
    private void ReleaseSimulationSlot(int userId, string reason)
    {
        if (!simulationPlayersByUserId.Remove(userId, out var identity)) return;

        bool stillHeld = false;
        foreach (var other in simulationPlayersByUserId.Values)
        {
            if (other.ConfigSteamId == identity.ConfigSteamId) { stillHeld = true; break; }
        }
        if (!stillHeld) assignedSimulationSteamIds.Remove(identity.ConfigSteamId);

        Log($"[SimulationMode] Released roster slot {identity.ConfigName} ({identity.ConfigSteamId}, {identity.TeamSlot}) from UserId={userId} ({reason}).");

        if (isMatchSetup && readyAvailable && !matchStarted)
        {
            RequestSimulationReconcile($"slot released: {reason}");
        }
    }

    /// <summary>Queues one reconcile pass shortly (coalesces bursts of disconnects/failed readies).</summary>
    private void RequestSimulationReconcile(string reason)
    {
        if (!isSimulationMode || simulationReconcilePending) return;
        simulationReconcilePending = true;
        Log($"[SimulationMode] Reconcile requested ({reason}); running in 3s.");
        AddTimer(3.0f, () =>
        {
            simulationReconcilePending = false;
            ReconcileSimulationRoster(reason, forceReady: false);
        });
    }

    /// <summary>
    /// Brings the simulation roster back in line with the bots that are connected now:
    /// drops mappings and tracking for bots that are gone, maps unmapped bots to free slots,
    /// refreshes player tracking with the live controllers, adds bots for slots nobody holds,
    /// and readies mapped bots (always when <paramref name="forceReady"/>, otherwise once the
    /// ready flow has already run for this map).
    /// </summary>
    private void ReconcileSimulationRoster(string reason, bool forceReady)
    {
        if (!isSimulationMode || simulationIdentityPool.Count == 0) return;
        if (matchStarted || !readyAvailable)
        {
            Log($"[SimulationMode] Reconcile ({reason}) skipped: matchStarted={matchStarted}, readyAvailable={readyAvailable}.");
            return;
        }

        string team1Side = teamSides.TryGetValue(matchzyTeam1, out var side1) ? side1 : "CT";

        var liveControllers = new Dictionary<int, CCSPlayerController>();
        var liveBots = new List<SimLiveBot>();
        foreach (var controller in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!IsConnectedSimulationBot(controller)) continue;
            int uid = controller.UserId!.Value;
            liveControllers[uid] = controller;
            liveBots.Add(new SimLiveBot(uid, MatchLogic.SlotForTeamNum(controller.TeamNum, team1Side)));
        }

        var roster = new List<SimRosterSlot>();
        var identityBySteamId = new Dictionary<string, SimulationPlayerIdentity>();
        foreach (var identity in simulationIdentityPool)
        {
            roster.Add(new SimRosterSlot(identity.ConfigSteamId, identity.TeamSlot));
            identityBySteamId[identity.ConfigSteamId] = identity;
        }

        var mappings = new Dictionary<int, string>();
        foreach (var kv in simulationPlayersByUserId) mappings[kv.Key] = kv.Value.ConfigSteamId;

        var plan = SimulationRosterLogic.Reconcile(roster, mappings, liveBots);
        Log($"[SimulationMode] Reconcile ({reason}, forceReady={forceReady}): liveBots={liveBots.Count}, mapped={mappings.Count}, stale={plan.StaleUserIds.Count}, newAssignments={plan.NewAssignments.Count}, missingSlots={plan.MissingSlots.Count}.");

        foreach (int staleId in plan.StaleUserIds)
        {
            simulationPlayersByUserId.Remove(staleId);
            if (!liveControllers.ContainsKey(staleId))
            {
                playerData.Remove(staleId);
                playerReadyStatus.Remove(staleId);
            }
            Log($"[SimulationMode] Reconcile: dropped stale mapping for UserId={staleId}.");
        }

        var newlyMapped = new List<int>();
        foreach (var kv in plan.NewAssignments)
        {
            if (!identityBySteamId.TryGetValue(kv.Value, out var identity)) continue;
            simulationPlayersByUserId[kv.Key] = identity;
            newlyMapped.Add(kv.Key);
            Log($"[SimulationMode] Reconcile: mapped bot UserId={kv.Key} ({liveControllers[kv.Key].PlayerName}) to {identity.ConfigName} ({identity.ConfigSteamId}, {identity.TeamSlot}).");
        }

        assignedSimulationSteamIds.Clear();
        foreach (var identity in simulationPlayersByUserId.Values) assignedSimulationSteamIds.Add(identity.ConfigSteamId);

        // Refresh tracking with the live controllers. A remembered controller under a reused
        // UserId is what left match 64 counting 4 players on a team of 5.
        foreach (var kv in simulationPlayersByUserId)
        {
            if (!liveControllers.TryGetValue(kv.Key, out var controller)) continue;
            bool sameController = playerData.TryGetValue(kv.Key, out var known)
                && known != null && known.IsValid && known.Handle == controller.Handle;
            playerData[kv.Key] = controller;
            if (!sameController || !playerReadyStatus.ContainsKey(kv.Key))
            {
                playerReadyStatus[kv.Key] = false;
            }
        }
        connectedPlayers = GetRealPlayersCount();

        foreach (int uid in newlyMapped)
        {
            if (string.IsNullOrEmpty(matchConfig.RemoteLogURL) || !isMatchSetup) break;
            var info = BuildPlayerInfo(liveControllers[uid], "none");
            var connectEvent = new MatchZyPlayerConnectedEvent { MatchId = liveMatchId, Player = info };
            Task.Run(async () => { await SendEventAsync(connectEvent); });
        }

        if (plan.MissingSlots.Count > 0)
        {
            AddSimulationBotsForMissingSlots(plan.MissingSlots, team1Side, forceReady);
        }

        if (forceReady || simulationReadyFlowRan)
        {
            foreach (var kv in simulationPlayersByUserId)
            {
                if (!liveControllers.TryGetValue(kv.Key, out var controller)) continue;
                if (playerReadyStatus.TryGetValue(kv.Key, out bool ready) && ready) continue;
                Log($"[SimulationMode] Reconcile: readying {kv.Value.ConfigName} (UserId={kv.Key}).");
                OnPlayerReady(controller, null);
                if (matchStarted || !readyAvailable) return;
            }

            if (plan.MissingSlots.Count == 0 || forceReady)
            {
                teamReadyOverride[CsTeam.CounterTerrorist] = true;
                teamReadyOverride[CsTeam.Terrorist] = true;
                CheckAndSendTeamReadyEvent();
                CheckLiveRequired();
            }
        }
    }

    /// <summary>
    /// Adds one bot per roster slot that no connected bot holds, then reconciles again so the
    /// new bots are mapped and readied.
    /// </summary>
    private void AddSimulationBotsForMissingSlots(IReadOnlyList<SimRosterSlot> missingSlots, string team1Side, bool forceReady)
    {
        int rosterCount = simulationIdentityPool.Count;
        for (int i = 0; i < missingSlots.Count; i++)
        {
            string side = SimulationRosterLogic.SideForTeamSlot(missingSlots[i].TeamSlot, team1Side);
            string slotId = missingSlots[i].SteamId;
            AddTimer(i * 1.0f, () =>
            {
                if (!isSimulationMode || matchStarted || !readyAvailable) return;

                int liveCount = 0;
                foreach (var controller in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
                {
                    if (IsConnectedSimulationBot(controller)) liveCount++;
                }
                if (liveCount >= rosterCount)
                {
                    Log($"[SimulationMode] Reconcile: not adding a bot for slot {slotId}; {liveCount} bots already connected for {rosterCount} slots.");
                    return;
                }

                int quota = 0;
                try { quota = ConVar.Find("bot_quota")?.GetPrimitiveValue<int>() ?? 0; } catch { }

                // If the quota already expects more bots than are connected the engine is not
                // refilling it, so add explicitly; otherwise raise the quota by one on the side.
                string cmd = quota > liveCount
                    ? $"bot_join_team {side}; bot_add_{side.ToLowerInvariant()}"
                    : $"bot_join_team {side}; bot_quota {liveCount + 1}";
                Log($"[SimulationMode] Reconcile: adding a bot on {side} for slot {slotId} (live={liveCount}, bot_quota={quota}): {cmd}");
                Server.ExecuteCommand(cmd);
            });
        }

        float followUp = missingSlots.Count * 1.0f + 5.0f;
        AddTimer(followUp, () => ReconcileSimulationRoster("after adding bots", forceReady));
    }

    private void StopSimulationWatchdog()
    {
        simulationWatchdogTimer?.Kill();
        simulationWatchdogTimer = null;
    }

    /// <summary>
    /// Starts the warmup watchdog for this map. It checks every 10s and, once the simulated match
    /// has been in warmup for <see cref="SimulationRosterLogic.WarmupWatchdogSeconds"/> real seconds,
    /// reconciles the roster and force-readies. It measures wall-clock time because game timers
    /// run faster under host_timescale. If reconciling twice has not got the match live, it
    /// force-starts like css_start.
    /// </summary>
    private void StartSimulationWatchdog()
    {
        StopSimulationWatchdog();
        simulationWarmupStartedUtc = DateTime.UtcNow;
        simulationWatchdogAttempts = 0;
        simulationWatchdogTimer = AddTimer(10.0f, SimulationWatchdogTick, TimerFlags.REPEAT);
    }

    private void SimulationWatchdogTick()
    {
        if (!isSimulationMode || matchStarted || !readyAvailable)
        {
            StopSimulationWatchdog();
            return;
        }

        double seconds = (DateTime.UtcNow - simulationWarmupStartedUtc).TotalSeconds;
        if (!SimulationRosterLogic.ShouldWatchdogReconcile(isSimulationMode, isMatchSetup, readyAvailable, matchStarted, seconds))
        {
            return;
        }

        simulationWatchdogAttempts++;
        Log($"[SimulationMode] Watchdog: still in warmup after {seconds:0}s (attempt {simulationWatchdogAttempts}); reconciling roster and force-readying.");
        simulationWarmupStartedUtc = DateTime.UtcNow;

        ReconcileSimulationRoster("watchdog", forceReady: true);

        if (!matchStarted && readyAvailable && simulationWatchdogAttempts >= SimulationRosterLogic.WatchdogForceStartAfterAttempts)
        {
            Log("[SimulationMode] Watchdog: reconcile did not get the match live; force-starting (same as css_start).");
            HandleMatchStart(allowAutoReadySimulationWithoutHumans: true);
        }
    }

    /// <summary>
    /// mp_warmup_end is what MAT's "end warmup" sends. On its own it only ends the engine warmup
    /// while the plugin keeps waiting for ready players. In a simulated match, reconcile the
    /// roster, force-ready the bots and start the match through the normal start path instead.
    /// </summary>
    private HookResult OnWarmupEndCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!SimulationRosterLogic.ShouldInterceptWarmupEnd(isSimulationMode, isMatchSetup, readyAvailable, matchStarted, handleMatchStartInProgress))
        {
            return HookResult.Continue;
        }

        Log("[SimulationMode] mp_warmup_end received during simulated warmup; reconciling roster and starting the match.");
        ReconcileSimulationRoster("mp_warmup_end", forceReady: true);
        if (!matchStarted && readyAvailable)
        {
            HandleMatchStart(allowAutoReadySimulationWithoutHumans: true);
        }
        // The start path issues its own mp_warmup_end once flags are set.
        return HookResult.Stop;
    }

    /// <summary>
    /// Build the list of configured players from the loaded JSON config.
    /// This should be called once per match before any bots are mapped.
    /// </summary>
    private void BuildSimulationConfigPlayers()
    {
        simulationIdentityPool.Clear();
        assignedSimulationSteamIds.Clear();
        simulationPlayersByUserId.Clear();

        void AddFromTeam(JToken? teamPlayers, string teamSlot)
        {
            if (teamPlayers is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    string steamId = prop.Name;
                    string name = prop.Value?.ToString() ?? "";
                    simulationIdentityPool.Add(new SimulationPlayerIdentity(steamId, name, teamSlot));
                }
            }
        }

        AddFromTeam(matchzyTeam1.teamPlayers, "team1");
        AddFromTeam(matchzyTeam2.teamPlayers, "team2");

        Log($"[SimulationMode] Built simulation config players - total: {simulationIdentityPool.Count}");
    }

    /// <summary>
    /// Assigns a configured simulation identity to the given bot, if available.
    /// Called from the player connect handler when a bot joins in simulation mode.
    /// </summary>
    private SimulationPlayerIdentity? AssignSimulationIdentityForBot(CCSPlayerController player, bool allowUnassignedTeam = false)
    {
        if (!isSimulationMode || !player.IsBot || !player.UserId.HasValue)
        {
            return null;
        }

        int userId = player.UserId.Value;
        if (simulationPlayersByUserId.TryGetValue(userId, out var existing))
        {
            return existing;
        }

        // The identity must belong to the team slot currently playing on the bot's side.
        // Bots are requested per side (team1's side first), but the engine does not hand
        // them back in join order: EnsureSimulationBotsMappedAndAnnounced walks controllers
        // newest-first, so assigning from the pool in order put team1's identities on the
        // bots of team2's side. Every player-stat payload (round_end team1/team2 players)
        // then listed the other team's names.
        string team1Side = teamSides.TryGetValue(matchzyTeam1, out var side1) ? side1 : "CT";
        string? botSlot = MatchLogic.SlotForTeamNum(player.TeamNum, team1Side);
        if (botSlot == null && !allowUnassignedTeam)
        {
            // Not on CT/T yet (e.g. connect-full fires before the team join). The follow-up
            // mapping pass assigns it once the bot has a side.
            Log($"[SimulationMode] Bot {player.PlayerName} (UserId {userId}) has no side yet (TeamNum={player.TeamNum}); deferring identity assignment.");
            return null;
        }

        SimulationPlayerIdentity? candidate = null;
        foreach (var identity in simulationIdentityPool)
        {
            if (assignedSimulationSteamIds.Contains(identity.ConfigSteamId)) continue;
            if (botSlot == null || identity.TeamSlot == botSlot)
            {
                candidate = identity;
                break;
            }
        }

        if (candidate == null && botSlot != null)
        {
            // More bots on this side than configured players for the team. Fall back to any
            // free identity rather than leaving the bot unmapped, and say so.
            foreach (var identity in simulationIdentityPool)
            {
                if (!assignedSimulationSteamIds.Contains(identity.ConfigSteamId))
                {
                    candidate = identity;
                    Log($"[SimulationMode] Warning: no free {botSlot} identity for bot {player.PlayerName} (UserId {userId}, TeamNum={player.TeamNum}); using {identity.TeamSlot} identity {identity.ConfigName}.");
                    break;
                }
            }
        }

        if (candidate != null)
        {
            assignedSimulationSteamIds.Add(candidate.ConfigSteamId);
            simulationPlayersByUserId[userId] = candidate;
            Log($"[SimulationMode] Assigned bot {player.PlayerName} (UserId {userId}, TeamNum={player.TeamNum}) to simulated player {candidate.ConfigName} ({candidate.ConfigSteamId}) on {candidate.TeamSlot}");
            // Now that we have at least one mapped simulation player, ensure the
            // simulated ready flow is scheduled. This avoids starting the ready
            // flow too early (before bots have connected) and falling back to a
            // team-level auto-ready with zero players.
            ScheduleSimulationReadyFlowIfNeeded();
            return candidate;
        }

        Log($"[SimulationMode] No available simulated player identity for bot {player.PlayerName} (UserId {userId})");
        return null;
    }

    /// <summary>
    /// Helper to build MatchZyPlayerInfo, respecting simulation mappings when enabled.
    /// </summary>
    private MatchZyPlayerInfo BuildPlayerInfo(CCSPlayerController player, string teamLabelFallback)
    {
        if (isSimulationMode && player.UserId.HasValue &&
            simulationPlayersByUserId.TryGetValue(player.UserId.Value, out var identity))
        {
            return new MatchZyPlayerInfo(identity.ConfigSteamId, identity.ConfigName, identity.TeamSlot);
        }

        return new MatchZyPlayerInfo(player.SteamID.ToString(), player.PlayerName, teamLabelFallback);
    }

    /// <summary>
    /// Spawns one bot per configured player and assigns them to the appropriate CS team.
    /// </summary>
    private void SpawnSimulationBots()
    {
        if (!isSimulationMode || simulationIdentityPool.Count == 0)
        {
            Log($"[SimulationMode] SpawnSimulationBots called but aborted (isSimulationMode={isSimulationMode}, identityPoolCount={simulationIdentityPool.Count}).");
            return;
        }

        Log($"[SimulationMode] Spawning simulation bots (simulation mode active). identityPoolCount={simulationIdentityPool.Count}");

        // For simulation we want exactly one bot per configured player. To avoid the
        // engine auto-spawning *extra* bots, we drive bot creation purely by gradually
        // increasing bot_quota from 0 -> desiredCount while setting bot_join_team for
        // each step, instead of combining bot_quota with explicit bot_add_* calls.
        int desiredBotCount = simulationIdentityPool.Count;
        if (desiredBotCount < 0) desiredBotCount = 0;
        Log($"[SimulationMode] Desired bot count for simulation = {desiredBotCount}");

        // Start from a clean slate: no bots, no auto-kick/auto-balance.
        // At this point ClearExistingBotsForSimulation() has already removed any
        // pre-existing bots, so setting bot_quota 0 will not kick our own simulation bots.
        Log("[SimulationMode] Initializing bot cvars: mp_autoteambalance 0; mp_limitteams 0; mp_autokick 0; bot_quota_mode normal; bot_difficulty 3; bot_quota 0");
        Server.ExecuteCommand("mp_autoteambalance 0; mp_limitteams 0; mp_autokick 0; bot_quota_mode normal; bot_difficulty 3; bot_quota 0");

        int index = 0;
        foreach (var identity in simulationIdentityPool)
        {
            // Decide desired side based on team slot and current teamSides mapping.
            string desiredSide = "T";
            if (identity.TeamSlot == "team1")
            {
                desiredSide = teamSides.TryGetValue(matchzyTeam1, out var side) ? side : "CT";
            }
            else if (identity.TeamSlot == "team2")
            {
                desiredSide = teamSides.TryGetValue(matchzyTeam2, out var side) ? side : "TERRORIST";
            }

            // Quota value we want to reach when this bot is spawned.
            int quotaForThisStep = index + 1;
            float delaySeconds = index * 1.0f;
            var desiredSideCopy = desiredSide;

            // Spawn bots gradually so the server has time to settle between joins and any
            // external config executions. This also produces a more human-like join pattern.
            AddTimer(delaySeconds, () =>
            {
                if (!isSimulationMode)
                {
                    Log("[SimulationMode] SpawnSimulationBots timer fired but simulation mode is no longer active; skipping.");
                    return;
                }

                if (desiredSideCopy == "CT")
                {
                    Log($"[SimulationMode] Requesting next bot on CT (targetQuota={quotaForThisStep}).");
                    Server.ExecuteCommand("bot_join_team CT");
                }
                else
                {
                    Log($"[SimulationMode] Requesting next bot on T (targetQuota={quotaForThisStep}).");
                    Server.ExecuteCommand("bot_join_team T");
                }

                // Bump the quota up by one for this identity; the engine will spawn a new
                // bot on the requested team. Because we only ever increase bot_quota from
                // 0 -> desiredBotCount (never back down to 0), we avoid the previous issue
                // where a late bot_quota 0 would kick all simulation bots.
                Log($"[SimulationMode] Setting bot_quota to {quotaForThisStep}.");
                Server.ExecuteCommand($"bot_quota {quotaForThisStep}");
            });

            index++;
        }

        // After we've requested all bots, schedule a follow-up pass that:
        // - Verifies bots have actually spawned and joined teams.
        // - Ensures each bot is mapped to a configured simulation identity.
        // - Sends synthetic player_connect events for any bots that didn't fire
        //   EventPlayerConnectFull (which appears to be the case on CS2 for bots).
        // - Kicks off the simulated ready flow once mappings exist.
        float mappingDelaySeconds = Math.Max(5.0f, desiredBotCount * 1.5f);
        Log($"[SimulationMode] Scheduling EnsureSimulationBotsMappedAndAnnounced() in {mappingDelaySeconds:0.00}s.");
        AddTimer(mappingDelaySeconds, EnsureSimulationBotsMappedAndAnnounced);
    }

    /// <summary>
    /// After bot_quota-driven spawning has completed, walk the live bot list to:
    /// - Confirm bots are connected and on a team.
    /// - Map each bot to a configured simulation identity (if not already mapped).
    /// - Announce a synthetic player_connect event for each mapped simulation bot.
    /// - Kick off the simulated ready flow.
    /// This compensates for the fact that EventPlayerConnectFull doesn't reliably
    /// fire for CS2 bots.
    /// </summary>
    private void EnsureSimulationBotsMappedAndAnnounced()
    {
        if (!isSimulationMode)
        {
            Log("[SimulationMode] EnsureSimulationBotsMappedAndAnnounced called but simulation mode is no longer active; skipping.");
            return;
        }

        var bots = new List<CCSPlayerController>();

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (player == null) continue;
            if (!player.IsValid || !player.IsBot || player.IsHLTV) continue;
            if (!player.UserId.HasValue) continue;

            bots.Add(player);
        }

        Log($"[SimulationMode] EnsureSimulationBotsMappedAndAnnounced: found {bots.Count} live bot controllers.");

        foreach (var bot in bots)
        {
            if (!bot.UserId.HasValue) continue;
            int userId = bot.UserId.Value;

            Log($"[SimulationMode] Observed bot '{bot.PlayerName}' (UserId={userId}, TeamNum={bot.TeamNum}, Connected={bot.Connected}).");

            // Make sure our core player tracking holds *this* controller. The UserId may be a
            // reused one whose old controller is still remembered (QA match 64: "Delayed ready:
            // bot UserId=12 no longer valid" and team2 stuck at 4 of 5), so always overwrite and
            // reset the ready state unless it is the same live controller.
            bool sameController = playerData.TryGetValue(userId, out var knownController)
                && IsPlayerValid(knownController) && knownController!.Handle == bot.Handle;
            if (!sameController && playerData.ContainsKey(userId))
            {
                Log($"[SimulationMode] Replacing stale tracked controller for UserId={userId} with bot '{bot.PlayerName}'.");
            }
            playerData[userId] = bot;
            if (!sameController || !playerReadyStatus.ContainsKey(userId))
            {
                playerReadyStatus[userId] = !(readyAvailable && !matchStarted);
            }

            // Ensure a simulation identity is assigned.
            SimulationPlayerIdentity? identity;
            if (!simulationPlayersByUserId.TryGetValue(userId, out identity))
            {
                // Last mapping pass: a bot still without a side gets any free identity
                // rather than staying unmapped.
                identity = AssignSimulationIdentityForBot(bot, allowUnassignedTeam: true);
            }

            if (identity == null)
            {
                Log($"[SimulationMode] No simulation identity available for bot '{bot.PlayerName}' (UserId={userId}); skipping synthetic connect.");
                continue;
            }

            // Send synthetic player_connect event immediately for simulation bots.
            // Then schedule a player_ready event after a short delay to simulate realistic
            // human behavior (connect → wait a few seconds → ready up).
            if (!string.IsNullOrEmpty(matchConfig.RemoteLogURL) && isMatchSetup)
            {
                var playerInfo = BuildPlayerInfo(bot, "none");
                Log($"[SimulationMode] Sending synthetic player_connect for sim bot UserId={userId}, steamid={playerInfo.SteamId}, name={playerInfo.Name}, team={playerInfo.Team}.");

                var playerConnectEvent = new MatchZyPlayerConnectedEvent
                {
                    MatchId = liveMatchId,
                    Player = playerInfo
                };

                Task.Run(async () =>
                {
                    await SendEventAsync(playerConnectEvent);
                });

                // Schedule the ready event after a delay to simulate human behavior
                if (readyAvailable && !matchStarted)
                {
                    if (!playerReadyStatus.ContainsKey(userId))
                    {
                        playerReadyStatus[userId] = false;
                    }

                    if (!playerReadyStatus[userId])
                    {
                        // Random delay between 1.5 and 3.5 seconds to simulate realistic ready-up times
                        float readyDelay = 1.5f + (new Random().Next(0, 200) / 100.0f);
                        string slotSteamId = identity.ConfigSteamId;

                        AddTimer(readyDelay, () =>
                        {
                            if (!readyAvailable || matchStarted)
                            {
                                Log($"[SimulationMode] Delayed ready: match already started or ready system disabled for UserId={userId}.");
                                return;
                            }

                            // Resolve the bot by roster slot at fire time, not by the UserId captured earlier.
                            var delayedBot = FindSimulationBotForSlot(slotSteamId);
                            if (delayedBot == null)
                            {
                                Log($"[SimulationMode] Delayed ready: no connected bot holds slot {slotSteamId} (was UserId={userId}); requesting reconcile.");
                                RequestSimulationReconcile("delayed ready found no bot");
                                return;
                            }

                            int currentUserId = delayedBot.UserId!.Value;
                            playerData[currentUserId] = delayedBot;
                            playerReadyStatus[currentUserId] = true;
                            Log($"[SimulationMode] Marking sim bot UserId={currentUserId} as ready after {readyDelay:0.0}s delay.");

                            SendPlayerReadyEvent(delayedBot, true);
                            CheckAndSendTeamReadyEvent();
                            CheckLiveRequired();
                        });
                    }
                }
            }
        }

        // All simulation bots have been mapped and marked as ready.
        // Now ensure team overrides are set and final ready checks are performed.
        if (readyAvailable && !matchStarted && isMatchSetup)
        {
            Log("[SimulationMode] All simulation bots mapped and ready. Setting team overrides and checking match start conditions.");
            
            teamReadyOverride[CsTeam.CounterTerrorist] = true;
            teamReadyOverride[CsTeam.Terrorist] = true;
            
            CheckAndSendTeamReadyEvent();
            CheckLiveRequired();
        }
    }

    /// <summary>
    /// Starts the simulated ready flow: bots gradually "ready up" and then the match goes live.
    /// </summary>
    private void StartSimulationReadyFlow()
    {
        if (!isSimulationMode)
        {
            return;
        }

        var userIds = new List<int>(simulationPlayersByUserId.Keys);
        if (userIds.Count == 0)
        {
            // If there are no configured simulation identities at all, this indicates a
            // genuinely broken simulation configuration (no team/player info). Treat that
            // as a hard error for visibility.
            if (simulationIdentityPool.Count == 0)
            {
                Log("[SimulationMode] ERROR: Simulation mode enabled but no configured players were found in team1/team2.");
                UpdateTournamentStatus("error");
                isSimulationMode = false;
                return;
            }

            // Otherwise, we have valid configured players but no bot mappings yet. This can
            // happen transiently if bots are still connecting. Reschedule the ready
            // flow for a short time later instead of falling back immediately.
            Log("[SimulationMode] No mapped simulation players found for ready flow; rescheduling StartSimulationReadyFlow().");
            simulationReadyFlowScheduled = false;
            AddTimer(2.0f, StartSimulationReadyFlow);
            return;
        }

        if (userIds.Count < simulationIdentityPool.Count)
        {
            Log($"[SimulationMode] Warning: Only {userIds.Count} of {simulationIdentityPool.Count} simulated players were mapped to bots.");
        }

        userIds.Sort();
        simulationReadyFlowRan = true;

        Log($"[SimulationMode] Starting simulated ready flow for {userIds.Count} mapped players.");

        float delayStep = 0.5f;
        for (int i = 0; i < userIds.Count; i++)
        {
            int userId = userIds[i];
            float delay = i * delayStep;
            if (!simulationPlayersByUserId.TryGetValue(userId, out var slotIdentity)) continue;

            AddTimer(delay, () =>
            {
                // Resolve by roster slot at fire time: the UserId may have been freed or reused.
                var player = FindSimulationBotForSlot(slotIdentity.ConfigSteamId);
                if (player == null)
                {
                    Log($"[SimulationMode] Simulated ready: no connected bot holds slot {slotIdentity.ConfigName} ({slotIdentity.ConfigSteamId}, was UserId={userId}); requesting reconcile.");
                    RequestSimulationReconcile("simulated ready found no bot");
                    return;
                }

                int currentUserId = player.UserId!.Value;
                playerData[currentUserId] = player;
                Log($"[SimulationMode] Simulating !ready for UserId={currentUserId}, SteamId={slotIdentity.ConfigSteamId}, Name={slotIdentity.ConfigName}.");

                // Mimic the !ready command, which will:
                // - Mark the player as ready
                // - Send player_ready to the API
                // - Potentially trigger CheckLiveRequired and clan tag updates
                OnPlayerReady(player, null);
            });
        }

        float totalDelay = userIds.Count * delayStep + 0.25f;
        AddTimer(totalDelay, () =>
        {
            // Ensure team-level ready state is satisfied and events are emitted.
            teamReadyOverride[CsTeam.CounterTerrorist] = true;
            teamReadyOverride[CsTeam.Terrorist] = true;

            Log("[SimulationMode] Both teams marked ready via teamReadyOverride; invoking CheckAndSendTeamReadyEvent().");
            CheckAndSendTeamReadyEvent();

            // In simulation mode the internal match start logic (CheckLiveRequired)
            // is what actually transitions from warmup to live. When we mark both
            // teams forced-ready here, we need to re-evaluate that gate so the
            // match can start even if the CT/T player counts are not perfectly
            // balanced (e.g. 1 CT + 9 T bots).
            CheckLiveRequired();

            // Any slot still without a connected bot gets filled now rather than waiting for the watchdog.
            if (!matchStarted && readyAvailable && simulationPlayersByUserId.Count < simulationIdentityPool.Count)
            {
                RequestSimulationReconcile("ready flow finished with unmapped slots");
            }
        });
    }

    /// <summary>
    /// Schedules the simulated ready flow once at an appropriate time after we
    /// have at least one mapped simulation player. This prevents us from running
    /// the ready flow before bots have actually connected.
    /// </summary>
    private void ScheduleSimulationReadyFlowIfNeeded()
    {
        if (!isSimulationMode || simulationReadyFlowScheduled)
        {
            return;
        }

        int mappedCount = simulationPlayersByUserId.Count;
        if (mappedCount == 0)
        {
            Log("[SimulationMode] ScheduleSimulationReadyFlowIfNeeded called but mappedCount=0; nothing to schedule yet.");
            return;
        }

        // Give the server a bit of time for remaining bots to connect and be mapped.
        float delaySeconds = Math.Max(2.0f, mappedCount * 0.5f);

        simulationReadyFlowScheduled = true;
        Log($"[SimulationMode] Scheduling simulated ready flow in {delaySeconds:0.00}s for {mappedCount} mapped players.");
        AddTimer(delaySeconds, StartSimulationReadyFlow);
    }

    /// <summary>
    /// Applies sv_cheats and host_timescale for simulation mode using the configured
    /// simulation_timescale value. This is safe to call repeatedly and is used both
    /// when the per-map simulation flow starts and at the beginning of each round
    /// to ensure external configs or commands cannot silently disable simulation
    /// speedups.
    /// </summary>
    private void ApplySimulationTimescaleAndCheats()
    {
        if (!isSimulationMode)
        {
            return;
        }

        // In simulation mode we can safely speed up the game using cheats and timescale
        // so that simulated matches complete faster. Respect the per-match configuration,
        // defaulting to 1.0x if not provided. Human matches always run at 1.0x.
        float ts = 1.0f;
        if (matchConfig != null)
        {
            ts = MatchLogic.ClampSimulationTimeScale(matchConfig.SimulationTimeScale);
        }

        Log($"[SimulationMode] Enforcing sv_cheats 1 and host_timescale {ts:0.##} for simulation.");
        Server.ExecuteCommand($"sv_cheats 1; host_timescale {ts.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Ensures that non-simulation, non-practice matches run with normal cheats/timescale
    /// settings. This is used as a safeguard at warmup/round boundaries so that any
    /// previous simulation or practice configuration does not leak into real matches.
    /// </summary>
    private void ApplyNormalTimescaleAndCheatsForRealMatches()
    {
        if (isSimulationMode || isPractice)
        {
            return;
        }

        Log("[SimulationMode] Enforcing sv_cheats 0 and host_timescale 1 for non-simulation match.");
        Server.ExecuteCommand("sv_cheats 0; host_timescale 1");
    }

    /// <summary>
    /// Once the server is on the correct map and in warmup (i.e. ready to accept
    /// player connections), schedule the start of the simulation flow after a
    /// short delay. This ensures that all base game configs and warmup scripts
    /// have fully settled before we begin spawning bots and sending events.
    /// </summary>
    private void ScheduleSimulationFlowStart(float delaySeconds)
    {
        if (!isSimulationMode)
        {
            Log("[SimulationMode] ScheduleSimulationFlowStart called but simulation mode is not active; skipping.");
            return;
        }

        Log($"[SimulationMode] Scheduling simulation flow start in {delaySeconds:0.00}s (server warmup ready for connections).");

        AddTimer(delaySeconds, () =>
        {
            if (!isSimulationMode)
            {
                Log("[SimulationMode] Simulation flow start timer fired but simulation mode is no longer active; skipping.");
                return;
            }

            MaybeStartSimulationFlow();
        });
    }

    /// <summary>
    /// For simulation mode we want to start from a clean slate: clear any pre-existing
    /// non-HLTV bots that may have been spawned by the base game configs before we
    /// spawn one bot per configured player.
    /// </summary>
    private void ClearExistingBotsForSimulation()
    {
        var bots = new List<CCSPlayerController>();

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (player == null) continue;
            if (!player.IsValid || !player.IsBot || player.IsHLTV) continue;
            if (!player.UserId.HasValue) continue;

            bots.Add(player);
        }

        if (bots.Count == 0)
        {
            return;
        }

        Log($"[SimulationMode] Clearing {bots.Count} pre-existing bots before spawning simulation bots.");

        foreach (var bot in bots)
        {
            try
            {
                ushort userId = (ushort)bot.UserId!.Value;
                Log($"[SimulationMode] Kicking pre-existing bot '{bot.PlayerName}' (UserId={userId}) before simulation start.");
                Server.ExecuteCommand($"kickid {userId}");
            }
            catch (Exception)
            {
                // Best-effort cleanup; ignore failures for individual bots.
            }
        }
    }

    // Entry point for any simulation-only orchestration after a match is loaded.
    // This drives bot spawning and the simulated ready flow.
    private void MaybeStartSimulationFlow()
    {
        if (!isSimulationMode)
        {
            return;
        }

        // Guard against starting the simulation flow more than once per map. On multi-map
        // series we explicitly reset simulationFlowStarted from the map lifecycle code.
        if (simulationFlowStarted)
        {
            Log("[SimulationMode] MaybeStartSimulationFlow called but simulation flow has already started for this map; skipping.");
            return;
        }
        simulationFlowStarted = true;

        Log("[SimulationMode] Simulation mode enabled for this match. Initializing simulation state.");

        // Ensure bots are allowed to exist without human players connected. When
        // bot_join_after_player is 1, the game will kick bots if the server is
        // empty, which completely breaks fully simulated matches. For simulation
        // we always force this to 0.
        Server.ExecuteCommand("bot_join_after_player 0");

        // Ensure bots are actually active and behaving like real players. These cvars
        // disable common debug/freeze modes and make bots play out the match instead
        // of standing still or ignoring opponents.
        Log("[SimulationMode] Applying gameplay bot cvars: bot_stop 0; bot_freeze 0; bot_dont_shoot 0; bot_ignore_enemies 0; bot_defer_to_human 0");
        Server.ExecuteCommand("bot_stop 0; bot_freeze 0; bot_dont_shoot 0; bot_ignore_enemies 0; bot_defer_to_human 0");

        // Ensure simulation cheats/timescale are enforced for this map. This is also
        // re-applied at the start of each round from EventRoundStartHandler.
        ApplySimulationTimescaleAndCheats();

        // Clear any generic bots that were spawned by base configs (e.g. gamemode_competitive)
        // so that we can spawn exactly one bot per configured player.
        ClearExistingBotsForSimulation();

        // Drop tracked controllers that no longer exist (e.g. bots that left on the map change
        // without a usable disconnect event), so a new bot reusing the UserId is tracked fresh.
        foreach (var staleUserId in new List<int>(playerData.Keys))
        {
            var tracked = playerData[staleUserId];
            if (tracked == null || !tracked.IsValid || tracked.Connected != PlayerConnectedState.PlayerConnected)
            {
                playerData.Remove(staleUserId);
                playerReadyStatus.Remove(staleUserId);
                Log($"[SimulationMode] Pruned stale tracked player UserId={staleUserId} before spawning bots.");
            }
        }

        // Prepare the configured identities that bots will represent.
        BuildSimulationConfigPlayers();
        simulationReadyFlowRan = false;
        StartSimulationWatchdog();

        // Spawn one bot per configured player. The simulated ready flow will be
        // scheduled from AssignSimulationIdentityForBot once bots begin to connect.
        SpawnSimulationBots();
    }

    /// <summary>
    /// After a simulated series concludes, gracefully disconnect bots.
    /// Waits ~30 seconds, then kicks bots one by one at random intervals
    /// so that they appear to leave the server gradually.
    /// </summary>
    private void ScheduleSimulationBotDisconnects()
    {
        if (!isSimulationMode)
        {
            return;
        }

        const float initialDelaySeconds = 30.0f;

        Log("[SimulationMode] Scheduling simulated bot disconnects after series end.");

        // First wait a short fixed period after series end.
        AddTimer(initialDelaySeconds, () =>
        {
            if (!isSimulationMode)
            {
                return;
            }

            var bots = new List<CCSPlayerController>();

            foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
            {
                if (player == null) continue;
                if (!player.IsValid || !player.IsBot || player.IsHLTV) continue;
                if (player.Connected != PlayerConnectedState.PlayerConnected) continue;

                bots.Add(player);
            }

            if (bots.Count == 0)
            {
                Log("[SimulationMode] No bots found to disconnect after series end.");
                return;
            }

            Log($"[SimulationMode] Disconnecting {bots.Count} simulation bots over a random interval.");

            // Kick bots one by one with a small random delay between each
            var random = new Random();
            float accumulatedDelay = 0.0f;

            foreach (var bot in bots)
            {
                // Random interval between 1 and 5 seconds for each bot
                float interval = 1.0f + (float)(random.NextDouble() * 4.0);
                accumulatedDelay += interval;

                var botRef = bot;
                AddTimer(accumulatedDelay, () =>
                {
                    if (!isSimulationMode)
                    {
                        return;
                    }

                    if (!IsPlayerValid(botRef) || !botRef.IsBot)
                    {
                        return;
                    }

                    Log($"[SimulationMode] Disconnecting simulation bot {botRef.PlayerName} (UserId {botRef.UserId}).");
                    KickPlayer(botRef);
                });
            }
        });
    }
}



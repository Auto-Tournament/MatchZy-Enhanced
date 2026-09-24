using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Timers;

namespace AutoTournamentCS2
{
    public partial class AutoTournamentCS2
    {
        private static readonly HttpClient MatHeartbeatHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        private bool heartbeatInFlight = false;
        private int heartbeatConsecutiveFailures = 0;
        private long heartbeatNextAllowedAt = 0;

        private void StartMatHeartbeatTimerIfConfigured()
        {
            try
            {
                if (heartbeatTimer != null) return;
                if (string.IsNullOrWhiteSpace(heartbeatUrl)) return;

                // Send immediately, then every 15 seconds.
                SendMatHeartbeatSnapshot("startup", force: true);
                heartbeatTimer = AddTimer(15.0f, () => { SendMatHeartbeatSnapshot("timer"); }, TimerFlags.REPEAT);
                Log($"[MAT_HEARTBEAT] Started heartbeat timer (url={SecretRedactor.RedactText(heartbeatUrl)})");
            }
            catch (Exception ex)
            {
                Log($"[MAT_HEARTBEAT] Failed to start heartbeat timer: {ex.Message}");
            }
        }

        private void SendMatHeartbeatSnapshot(string reason, bool force = false)
        {
            // Timer callback runs on main thread. Capture all game/plugin state here.
            string hbUrl = heartbeatUrl?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(hbUrl)) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!force && heartbeatNextAllowedAt > 0 && now < heartbeatNextAllowedAt)
            {
                return; // in backoff window
            }
            if (!force && heartbeatInFlight)
            {
                return; // avoid piling up requests if MAT is slow
            }

            string token = ResolveMatTokenForHeartbeat();
            string statusRaw = tournamentStatus?.Value ?? "idle";
            string status = MapTournamentStatusToMatHeartbeatStatus(statusRaw);

            long matchId = liveMatchId > 0 ? liveMatchId : -1;
            int? matchid = liveMatchId > 0 ? (int)liveMatchId : null;

            string? matchSlug = TryDeriveMatchSlugForHeartbeat();
            bool readyForAllocation = IsReadyForAllocationHeartbeat();

            // CS2 build info (best-effort, cached).
            var (cs2BuildId, cs2VersionString) = GetCachedCs2BuildInfo();

            // Optional metadata (MAT ignores unknown fields, but useful for UIs/ops)
            string mapName = string.IsNullOrWhiteSpace(Server.MapName) ? "unknown" : Server.MapName;
            int trackingPlayers = 0;
            int readyPlayers = 0;
            try
            {
                trackingPlayers = playerReadyStatus.Count;
                readyPlayers = playerReadyStatus.Count(kv => kv.Value);
            }
            catch
            {
                // best-effort only
            }

            var payload = new
            {
                status,
                match_slug = matchSlug,
                matchid,
                ready_for_allocation = readyForAllocation,
                plugin_version = ModuleVersion,
                cs2_build_id = cs2BuildId,
                cs2_version_string = cs2VersionString,
                map = mapName,
                connected_players = connectedPlayers,
                tracking_players = trackingPlayers,
                ready_players = readyPlayers,
                simulated = isSimulationMode,
                paused = isPaused,
                _reason = reason, // non-contract debug marker (MAT ignores unknown keys)
                _ts = now,
            };

            string jsonBody = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            });

            // Send on background thread.
            heartbeatInFlight = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, hbUrl)
                    {
                        Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                    };

                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        request.Headers.TryAddWithoutValidation("x-auto-tournament-token", token);
                    }

                    var response = await MatHeartbeatHttpClient.SendAsync(request).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var respBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Log($"[MAT_HEARTBEAT] POST failed ({(int)response.StatusCode}) status={statusRaw} matchid={matchId} url={SecretRedactor.RedactText(hbUrl)} resp={SecretRedactor.RedactText(respBody)}");
                        OnMatHeartbeatFailure();
                        return;
                    }

                    // Success: reset backoff.
                    OnMatHeartbeatSuccess();
                }
                catch (Exception ex)
                {
                    Log($"[MAT_HEARTBEAT] POST exception: {ex.Message}");
                    OnMatHeartbeatFailure();
                }
                finally
                {
                    heartbeatInFlight = false;
                }
            });
        }

        private void OnMatHeartbeatSuccess()
        {
            heartbeatConsecutiveFailures = 0;
            heartbeatNextAllowedAt = 0;
        }

        private void OnMatHeartbeatFailure()
        {
            try
            {
                heartbeatConsecutiveFailures += 1;

                // Backoff schedule (seconds): 15, 30, 60, 120 (cap)
                int backoffSeconds = heartbeatConsecutiveFailures switch
                {
                    1 => 15,
                    2 => 30,
                    3 => 60,
                    _ => 120,
                };

                // Jitter 0..3s so servers don’t synchronize after outages.
                int jitter = Random.Shared.Next(0, 4);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                heartbeatNextAllowedAt = now + backoffSeconds + jitter;
            }
            catch
            {
                // best-effort only
            }
        }

        private string ResolveMatTokenForHeartbeat()
        {
            if (!string.IsNullOrWhiteSpace(matchToken))
            {
                return matchToken.Trim();
            }

            // Fallback: remote log header value is typically the same shared token.
            if (!string.IsNullOrWhiteSpace(matchConfig.RemoteLogHeaderValue))
            {
                return matchConfig.RemoteLogHeaderValue.Trim();
            }

            // Fallback: bootstrap token, if configured.
            if (!string.IsNullOrWhiteSpace(bootstrapToken))
            {
                return bootstrapToken.Trim();
            }

            return "";
        }

        private bool IsReadyForAllocationHeartbeat()
        {
            try
            {
                if (isMatchSetup) return false;
                var s = (tournamentStatus?.Value ?? "idle").Trim().ToLowerInvariant();
                return s == "idle";
            }
            catch
            {
                return false;
            }
        }

        private static string MapTournamentStatusToMatHeartbeatStatus(string raw)
        {
            var s = (raw ?? "").Trim().ToLowerInvariant();
            return s switch
            {
                "idle" => "idle",
                "loading" => "loading",
                "warmup" => "warmup",
                "live" => "live",
                "playing" => "live",
                "postgame" => "postgame",
                "error" => "error",
                // Plugin-only / extra phases (MAT only accepts a smaller set)
                "knife" => "live",
                "paused" => "live",
                "halftime" => "live",
                "setup" => "loading",
                "queued" => "loading",
                "going_live" => "live",
                _ => "error",
            };
        }

        private string? TryDeriveMatchSlugForHeartbeat()
        {
            try
            {
                // Prefer a slug derived from the last loaded config URL/path (MAT serves /api/matches/:slug.json)
                var raw = loadedConfigFile?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var s = DeriveMatMatchSlugFromConfigUrl(raw) ?? DeriveIdentifierFromUrlOrPath(raw);
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s;
                    }
                }

                // tournamentMatch is often set to matchid during loading; only use if it looks like a slug.
                var tm = tournamentMatch?.Value?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(tm) && !Regex.IsMatch(tm, @"^\\d+$"))
                {
                    return tm;
                }

                // No match.
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string? DeriveIdentifierFromUrlOrPath(string raw)
        {
            try
            {
                if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                {
                    var path = uri.AbsolutePath.TrimEnd('/');
                    var lastSegment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                    if (!string.IsNullOrWhiteSpace(lastSegment))
                    {
                        return Path.GetFileNameWithoutExtension(lastSegment);
                    }
                }

                // Local path or relative file reference
                var file = raw.Replace('\\', '/').TrimEnd('/');
                var last = file.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (!string.IsNullOrWhiteSpace(last))
                {
                    return Path.GetFileNameWithoutExtension(last);
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static string? DeriveMatMatchSlugFromConfigUrl(string raw)
        {
            try
            {
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                {
                    return null;
                }

                // Expected: /api/matches/:slug.json
                var seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (seg.Length >= 3 &&
                    seg[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                    seg[1].Equals("matches", StringComparison.OrdinalIgnoreCase))
                {
                    var last = seg[2];
                    if (!string.IsNullOrWhiteSpace(last))
                    {
                        return Path.GetFileNameWithoutExtension(last);
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private (int? buildId, string? versionString) GetCachedCs2BuildInfo()
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                const long refreshEverySeconds = 10 * 60;
                if (cachedCs2BuildAt > 0 && now - cachedCs2BuildAt < refreshEverySeconds)
                {
                    return (cachedCs2BuildId, cachedCs2VersionString);
                }

                string steamInfFilePath = Path.Combine(Server.GameDirectory, "csgo", "steam.inf");
                if (!File.Exists(steamInfFilePath))
                {
                    cachedCs2BuildAt = now;
                    cachedCs2BuildId = null;
                    cachedCs2VersionString = null;
                    return (null, null);
                }

                var steamInfContent = File.ReadAllText(steamInfFilePath);
                Regex regex = new(@"ServerVersion=(\\d+)");
                Match match = regex.Match(steamInfContent);
                if (!match.Success)
                {
                    cachedCs2BuildAt = now;
                    cachedCs2BuildId = null;
                    cachedCs2VersionString = null;
                    return (null, null);
                }

                string ver = match.Groups[1].Value;
                if (int.TryParse(ver, out int buildId))
                {
                    cachedCs2BuildAt = now;
                    cachedCs2BuildId = buildId;
                    cachedCs2VersionString = $"Protocol version {ver} [{ver}/{ver}]";
                    return (cachedCs2BuildId, cachedCs2VersionString);
                }

                cachedCs2BuildAt = now;
                cachedCs2BuildId = null;
                cachedCs2VersionString = null;
                return (null, null);
            }
            catch
            {
                return (null, null);
            }
        }
    }
}

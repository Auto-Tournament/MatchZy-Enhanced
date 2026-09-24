using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace AutoTournamentCS2
{
    public partial class AutoTournamentCS2
    {
        private sealed class BootstrapPayload
        {
            public bool success { get; set; }
            public string? serverId { get; set; }
            public string[]? commands { get; set; }
        }

        /// <summary>Runs debounce callbacks on the game thread through CounterStrikeSharp timers.</summary>
        private sealed class PluginTimerScheduler : IBootstrapScheduler
        {
            private readonly AutoTournamentCS2 plugin;

            public PluginTimerScheduler(AutoTournamentCS2 plugin) => this.plugin = plugin;

            public IDisposable Schedule(TimeSpan delay, Action callback)
            {
                Timer timer = plugin.AddTimer((float)delay.TotalSeconds, callback);
                return new KillOnDispose(timer);
            }

            private sealed class KillOnDispose : IDisposable
            {
                private Timer? timer;

                public KillOnDispose(Timer timer) => this.timer = timer;

                public void Dispose()
                {
                    try { timer?.Kill(); } catch { /* already fired or killed */ }
                    timer = null;
                }
            }
        }

        private BootstrapFetchDebouncer? bootstrapDebouncerInstance;

        private BootstrapFetchDebouncer BootstrapDebouncer => bootstrapDebouncerInstance ??= new BootstrapFetchDebouncer(
            new PluginTimerScheduler(this),
            () => DateTimeOffset.UtcNow,
            () => (bootstrapUrl, bootstrapToken),
            StartBootstrapFetch,
            message => Log(message));

        [ConsoleCommand("at_bootstrap_url", "HTTP URL to fetch bootstrap config payload (server-only)")]
        public void AutoTournamentCS2BootstrapUrl(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string url = command.ArgByIndex(1);

            if (string.IsNullOrWhiteSpace(url))
            {
                Log("[AutoTournamentCS2BootstrapUrl] Usage: at_bootstrap_url <url>");
                return;
            }

            string previous = bootstrapUrl;
            bootstrapUrl = url.Trim();
            database.SaveConfigValue("at_bootstrap_url", bootstrapUrl);
            Log("[AutoTournamentCS2BootstrapUrl] Bootstrap URL set and persisted to database");

            ScheduleBootstrapFetch(previous, bootstrapUrl);
        }

        [ConsoleCommand("at_bootstrap_token", "Authentication token for bootstrap endpoint (sent as X-Auto-Tournament-Token)")]
        public void AutoTournamentCS2BootstrapToken(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string token = command.ArgByIndex(1);

            if (string.IsNullOrWhiteSpace(token))
            {
                Log("[AutoTournamentCS2BootstrapToken] Usage: at_bootstrap_token <token>");
                return;
            }

            string previous = bootstrapToken;
            bootstrapToken = token.Trim();
            database.SaveConfigValue("at_bootstrap_token", bootstrapToken);
            Log("[AutoTournamentCS2BootstrapToken] Bootstrap token set and persisted to database");

            ScheduleBootstrapFetch(previous, bootstrapToken);
        }

        /// <summary>
        /// Controllers set the URL and token with separate commands, in either order. Fetching on
        /// each one would use the other's stale value, so each change restarts a short timer and
        /// the fetch runs once with the final values.
        /// </summary>
        private void ScheduleBootstrapFetch(string previous, string next)
        {
            if (BootstrapDebouncer.OnValueSet(previous, next))
            {
                Log($"[Bootstrap] Fetch scheduled in {BootstrapDebouncer.Delay.TotalSeconds:0.#}s (restarts on further URL/token changes)");
            }
        }

        /// <summary>Immediate fetch from persisted config at startup.</summary>
        private void TryBootstrapFetch(string reason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(bootstrapUrl)) return;
                if (string.IsNullOrWhiteSpace(bootstrapToken)) return;

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (lastBootstrapAttemptAt > 0 && now - lastBootstrapAttemptAt < 5)
                {
                    return; // anti-spam
                }
                lastBootstrapAttemptAt = now;

                BootstrapDebouncer.FetchNow(reason);
            }
            catch (Exception ex)
            {
                // best-effort only
                Log($"[Bootstrap] Startup fetch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Fetches the payload with the URL and token captured when the fetch started, then hands
        /// the result to the game thread. Always reports back to the debouncer.
        /// </summary>
        private void StartBootstrapFetch(string url, string token, string reason)
        {
            Task.Run(async () =>
            {
                string[]? commands = null;
                try
                {
                    using var httpClient = new HttpClient();
                    httpClient.DefaultRequestHeaders.Add("X-Auto-Tournament-Token", token);

                    var response = await httpClient.GetAsync(url);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"[Bootstrap] Fetch failed ({(int)response.StatusCode}): {SecretRedactor.RedactText(body)}");
                        return;
                    }

                    BootstrapPayload? payload;
                    try
                    {
                        payload = JsonSerializer.Deserialize<BootstrapPayload>(body);
                    }
                    catch (Exception ex)
                    {
                        Log($"[Bootstrap] Failed to parse payload JSON: {ex.Message}");
                        return;
                    }

                    if (payload?.commands == null || payload.commands.Length == 0)
                    {
                        Log("[Bootstrap] Payload contained no commands; skipping");
                        return;
                    }

                    commands = payload.commands;
                }
                catch (Exception ex)
                {
                    Log($"[Bootstrap] Fetch error: {ex.Message}");
                }
                finally
                {
                    string[]? toApply = commands;
                    Server.NextFrame(() => ApplyBootstrapPayload(url, reason, toApply));
                }
            });
        }

        private void ApplyBootstrapPayload(string url, string reason, string[]? commands)
        {
            bool applied = commands != null && commands.Length > 0;

            // Before executing: URL/token commands inside the payload must count as its echo.
            BootstrapDebouncer.OnFetchCompleted(applied);
            if (!applied) return;

            try
            {
                foreach (string warning in BootstrapPayloadCheck.ServerIdWarnings(url, commands, matchReportServerId.Value))
                {
                    Log(SecretRedactor.RedactText(warning));
                }

                Log($"[Bootstrap] Applying {commands!.Length} bootstrap commands ({reason})");
                foreach (var cmd in commands)
                {
                    if (string.IsNullOrWhiteSpace(cmd)) continue;
                    Server.ExecuteCommand(cmd);
                }
            }
            catch (Exception ex)
            {
                Log($"[Bootstrap] Apply error: {ex.Message}");
            }
        }
    }
}

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;

namespace AutoTournamentCS2
{
    public partial class AutoTournamentCS2
    {
        [ConsoleCommand("get5_remote_log_url", "If defined, all events are sent to this URL over HTTP. If no protocol is provided")]
        [ConsoleCommand("at_remote_log_url", "If defined, all events are sent to this URL over HTTP. If no protocol is provided")]
        public void RemoteLogURLCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string url = command.ArgByIndex(1);

            if (!IsValidUrl(url))
            {
                Log($"[RemoteLogURLCommand] Invalid URL: {SecretRedactor.RedactText(url)}. Please provide a valid URL!");
                return;
            }

            string oldUrl = matchConfig.RemoteLogURL;
            matchConfig.RemoteLogURL = url;

            // Mark that a remote log URL has been configured at least once this session so that
            // we can stop treating missing URLs as a noisy but expected startup condition.
            remoteLogUrlEverConfigured = true;
            remoteLogUrlMissingWarningLogged = false;
            
            // Persist to database so it survives server restarts
            database.SaveConfigValue("at_remote_log_url", url);
            Log($"[RemoteLogURLCommand] Remote log URL set and persisted to database: {SecretRedactor.RedactText(url)}");
            
            // If URL changed, clear old failed events that were queued with the previous URL
            // This prevents retrying events to the wrong endpoint
            if (!string.IsNullOrEmpty(oldUrl) && oldUrl != url)
            {
                int cleared = database.ClearEventQueue();
                if (cleared > 0)
                {
                    Log($"[RemoteLogURLCommand] Cleared {cleared} pending/failed events from old URL configuration.");
                }
            }
            
            // Send server_configured event so API knows this server is active and configured
            SendServerConfiguredEvent("Console");
        }

        [ConsoleCommand("get5_remote_log_header_key", "If defined, a custom HTTP header with this name is added to the HTTP requests for events")]
        [ConsoleCommand("at_remote_log_header_key", "If defined, a custom HTTP header with this name is added to the HTTP requests for events")]
        public void RemoteLogHeaderKeyCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string header = command.ArgByIndex(1).Trim();

            if (header != "")
            {
                matchConfig.RemoteLogHeaderKey = header;
                
                // Persist to database so it survives server restarts
                database.SaveConfigValue("at_remote_log_header_key", header);
                Log($"[RemoteLogHeaderKeyCommand] Remote log header key set and persisted to database: {header}");
            }
        }

        [ConsoleCommand("get5_remote_log_header_value", "If defined, the value of the custom header added to the events sent over HTTP")]
        [ConsoleCommand("at_remote_log_header_value", "If defined, the value of the custom header added to the events sent over HTTP")]
        public void RemoteLogHeaderValueCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string headerValue = command.ArgByIndex(1).Trim();

            if (headerValue != "")
            {
                matchConfig.RemoteLogHeaderValue = headerValue;
                
                // Persist to database so it survives server restarts
                database.SaveConfigValue("at_remote_log_header_value", headerValue);
                Log($"[RemoteLogHeaderValueCommand] Remote log header value set and persisted to database");
            }
        }

        /// <summary>
        /// Sends a server_configured event to the API so it can track active servers
        /// </summary>
        private void SendServerConfiguredEvent(string configuredBy)
        {
            try
            {
                if (string.IsNullOrEmpty(matchConfig.RemoteLogURL))
                {
                    Log("[SendServerConfiguredEvent] Skipping: Remote log URL not configured");
                    return;
                }

                // Require server_id to be set before sending server_configured event
                // This ensures the API receives a valid server identifier
                if (string.IsNullOrEmpty(matchReportServerId.Value))
                {
                    Log("[SendServerConfiguredEvent] Skipping: Server ID not configured. Set at_server_id before configuring remote log URL.");
                    return;
                }

                // Get server hostname from ConVar
                var hostnameConvar = ConVar.Find("hostname");
                string hostname = hostnameConvar?.StringValue ?? "Unknown Server";

                var serverConfiguredEvent = new AutoTournamentCS2ServerConfiguredEvent
                {
                    ServerId = matchReportServerId.Value,
                    Hostname = hostname,
                    PluginVersion = ModuleVersion,
                    RemoteLogUrl = matchConfig.RemoteLogURL,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ConfiguredBy = configuredBy
                };

                Task.Run(async () =>
                {
                    await SendEventAsync(serverConfiguredEvent);
                });

                Log($"[SendServerConfiguredEvent] Sent server_configured event for server '{hostname}' (ID: {matchReportServerId.Value})");
            }
            catch (Exception ex)
            {
                Log($"[SendServerConfiguredEvent] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a server_health event to the API so controllers can surface
        /// important server health signals (e.g. plugin DB connectivity).
        /// </summary>
        private void SendServerHealthEvent(string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(matchConfig.RemoteLogURL))
                {
                    // No remote log URL; we can't reach the API anyway.
                    return;
                }

                if (string.IsNullOrEmpty(matchReportServerId.Value))
                {
                    return;
                }

                var (ok, dbType, error) = database.CheckHealth();

                var ev = new AutoTournamentCS2ServerHealthEvent
                {
                    ServerId = matchReportServerId.Value,
                    PluginVersion = ModuleVersion,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    DbOk = ok,
                    DbType = dbType,
                    DbError = ok ? null : error,
                    Reason = reason
                };

                Task.Run(async () =>
                {
                    await SendEventAsync(ev);
                });
            }
            catch (Exception ex)
            {
                Log($"[SendServerHealthEvent] Error: {ex.Message}");
            }
        }
    }
}

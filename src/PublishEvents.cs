using System.Text;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Utils;


namespace MatchZy
{
    public partial class MatchZy
    {
        public async Task SendEventAsync(MatchZyEvent @event)
        {
            try
            {
                if (!eventsEnabled.Value) return;
                if (string.IsNullOrEmpty(matchConfig.RemoteLogURL)) return;

                Log($"[SendEventAsync] Sending Event: {@event.EventName} for matchId: {liveMatchId} mapNumber: {matchConfig.CurrentMapNumber} on {SecretRedactor.RedactText(matchConfig.RemoteLogURL)}");
                
                // Print to server console, and optionally to chat for visibility
                Server.NextFrame(() => {
                    Server.PrintToConsole($"[MatchZy Events] Sending '{@event.EventName}' to {SecretRedactor.RedactText(matchConfig.RemoteLogURL)}");
                    if (debugChatEnabled.Value)
                    {
                        Server.PrintToChatAll($"{chatPrefix} {ChatColors.Grey}Event:{ChatColors.Default} {ChatColors.Lime}{@event.EventName}{ChatColors.Default} → {ChatColors.Grey}{GetShortUrl(matchConfig.RemoteLogURL)}");
                    }
                });

                using var httpClient = new HttpClient();
                using var jsonContent = new StringContent(JsonSerializer.Serialize(@event, @event.GetType()), Encoding.UTF8, "application/json");

                string jsonString = await jsonContent.ReadAsStringAsync();

                Log($"[SendEventAsync] SENDING DATA: {SecretRedactor.RedactText(jsonString)}");

                if (!string.IsNullOrEmpty(matchConfig.RemoteLogHeaderKey) && !string.IsNullOrEmpty(matchConfig.RemoteLogHeaderValue))
                {
                    httpClient.DefaultRequestHeaders.Add(matchConfig.RemoteLogHeaderKey, matchConfig.RemoteLogHeaderValue);
                }

                var httpResponseMessage = await httpClient.PostAsync(matchConfig.RemoteLogURL, jsonContent);

                if (httpResponseMessage.IsSuccessStatusCode)
                {
                    Log($"[SendEventAsync] Sending {@event.EventName} for matchId: {liveMatchId} mapNumber: {matchConfig.CurrentMapNumber} successful with status code: {httpResponseMessage.StatusCode}");
                    
                    // Print success to console, and optionally to chat
                    Server.NextFrame(() => {
                        Server.PrintToConsole($"[MatchZy Events] ✓ '{@event.EventName}' sent successfully ({httpResponseMessage.StatusCode})");
                        if (debugChatEnabled.Value)
                        {
                            Server.PrintToChatAll($"{chatPrefix} {ChatColors.Green}✓{ChatColors.Default} {ChatColors.Lime}{@event.EventName}{ChatColors.Default} sent");
                        }
                    });
                }
                else
                {
                    string errorContent = await httpResponseMessage.Content.ReadAsStringAsync();
                    string errorMsg = $"HTTP {httpResponseMessage.StatusCode}: {errorContent}";
                    Log($"[SendEventAsync] Sending {@event.EventName} for matchId: {liveMatchId} mapNumber: {matchConfig.CurrentMapNumber} failed with status code: {httpResponseMessage.StatusCode}, ResponseContent: {SecretRedactor.RedactText(errorContent)}");
                    
                    // Queue the event for retry (jsonString already contains the serialized event)
                    string eventDataForQueue = jsonString;
                    Server.NextFrame(() => {
                        database.QueueEvent(@event.EventName, eventDataForQueue, liveMatchId, matchConfig.CurrentMapNumber, errorMsg);
                    });
                    
                    // Print error to console, and optionally to chat
                    Server.NextFrame(() => {
                        Server.PrintToConsole($"[MatchZy Events] ✗ FAILED to send '{@event.EventName}' (HTTP {httpResponseMessage.StatusCode})");
                        Server.PrintToConsole($"[MatchZy Events] Error: {SecretRedactor.RedactText(errorContent)}");
                        Server.PrintToConsole($"[MatchZy Events] → Event queued for retry");
                        if (debugChatEnabled.Value)
                        {
                            Server.PrintToChatAll($"{chatPrefix} {ChatColors.Red}✗{ChatColors.Default} {ChatColors.Lime}{@event.EventName}{ChatColors.Default} {ChatColors.Red}FAILED{ChatColors.Default} ({httpResponseMessage.StatusCode}) - {ChatColors.Yellow}queued for retry");
                        }
                    });
                }
            }
            catch (Exception e)
            {
                Log($"[SendEventAsync FATAL] An error occurred: {e.Message}");
                
                // Queue the event for retry
                try
                {
                    string eventDataForQueue = JsonSerializer.Serialize(@event, @event.GetType());
                    Server.NextFrame(() => {
                        database.QueueEvent(@event.EventName, eventDataForQueue, liveMatchId, matchConfig.CurrentMapNumber, $"Exception: {e.Message}");
                    });
                }
                catch (Exception queueEx)
                {
                    Log($"[SendEventAsync] Failed to queue event: {queueEx.Message}");
                }
                
                // Print exception to console, and optionally to chat
                Server.NextFrame(() => {
                    Server.PrintToConsole($"[MatchZy Events] ✗ EXCEPTION sending '{@event.EventName}': {e.Message}");
                    Server.PrintToConsole($"[MatchZy Events] → Event queued for retry");
                    if (debugChatEnabled.Value)
                    {
                        Server.PrintToChatAll($"{chatPrefix} {ChatColors.Red}✗{ChatColors.Default} {ChatColors.Lime}{@event.EventName}{ChatColors.Default} {ChatColors.Red}ERROR:{ChatColors.Default} {e.Message} - {ChatColors.Yellow}queued for retry");
                    }
                });
            }
        }

        private string GetShortUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                return SecretRedactor.RedactText(uri.Host + uri.PathAndQuery);
            }
            catch
            {
                return SecretRedactor.RedactText(url.Length > 30 ? url.Substring(0, 27) + "..." : url);
            }
        }

        private bool eventRetryTimerStarted = false;
        private bool cleanupTimerStarted = false;

        /// <summary>
        /// Starts background timer to retry failed events (only once)
        /// </summary>
        private void StartEventRetryTimer()
        {
            if (!eventsEnabled.Value) return;
            if (eventRetryTimerStarted) return;
            eventRetryTimerStarted = true;
            
            // Process retry queue at configured interval (repeating)
            void RetryTimerCallback()
            {
                ProcessEventRetryQueue();
                // Reschedule for next run
                AddTimer(eventRetryInterval.Value, RetryTimerCallback);
            }
            
            AddTimer(eventRetryInterval.Value, RetryTimerCallback);
            
            // Also cleanup old events at configured interval (only start once)
            if (!cleanupTimerStarted)
            {
                cleanupTimerStarted = true;
                AddTimer(eventCleanupInterval.Value, () =>
                {
                    database.CleanupOldEvents();
                });
            }
            
            Log($"[EventRetryTimer] Event retry system started ({eventRetryInterval.Value}s interval)");
        }

        /// <summary>
        /// Processes the event retry queue
        /// </summary>
        private async void ProcessEventRetryQueue()
        {
            try
            {
                if (!eventsEnabled.Value) return;
                if (string.IsNullOrEmpty(matchConfig.RemoteLogURL))
                {
                    // No webhook configured, skip retry processing
                    return;
                }

                var pendingEvents = database.GetPendingEvents(50);
                
                if (pendingEvents.Count == 0)
                {
                    return;
                }

                Log($"[EventRetryQueue] Processing {pendingEvents.Count} pending events...");

                foreach (var evt in pendingEvents)
                {
                    try
                    {
                        Log($"[EventRetryQueue] Retrying event {evt.id}: {evt.event_type} (attempt {evt.retry_count + 1})");

                        using var httpClient = new HttpClient();
                        using var jsonContent = new StringContent(evt.event_data, Encoding.UTF8, "application/json");

                        if (!string.IsNullOrEmpty(matchConfig.RemoteLogHeaderKey) && !string.IsNullOrEmpty(matchConfig.RemoteLogHeaderValue))
                        {
                            httpClient.DefaultRequestHeaders.Add(matchConfig.RemoteLogHeaderKey, matchConfig.RemoteLogHeaderValue);
                        }

                        var httpResponseMessage = await httpClient.PostAsync(matchConfig.RemoteLogURL, jsonContent);

                        if (httpResponseMessage.IsSuccessStatusCode)
                        {
                            // Run on main thread to avoid CS2 API threading errors
                            int eventId = evt.id;
                            string eventType = evt.event_type;
                            int attemptNumber = evt.retry_count + 1;
                            
                            Server.NextFrame(() => {
                                database.MarkEventSent(eventId);
                                Server.PrintToConsole($"[MatchZy Events] ✓ Retry successful: '{eventType}' (attempt {attemptNumber})");
                            });
                            
                            Log($"[EventRetryQueue] ✓ Event {eventId} ({eventType}) sent successfully on retry {attemptNumber}");
                        }
                        else
                        {
                            string errorContent = await httpResponseMessage.Content.ReadAsStringAsync();
                            string errorMsg = $"HTTP {httpResponseMessage.StatusCode}: {errorContent}";
                            
                            // Run on main thread to avoid CS2 API threading errors
                            int eventId = evt.id;
                            int retryCount = evt.retry_count + 1;
                            string eventType = evt.event_type;
                            
                            Server.NextFrame(() => {
                                database.MarkEventRetry(eventId, retryCount, errorMsg);
                            });
                            
                            Log($"[EventRetryQueue] ✗ Event {eventId} ({eventType}) retry failed: {errorMsg}");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Run on main thread to avoid CS2 API threading errors
                        int eventId = evt.id;
                        int retryCount = evt.retry_count + 1;
                        string eventType = evt.event_type;
                        string exceptionMsg = ex.Message;
                        
                        Server.NextFrame(() => {
                            database.MarkEventRetry(eventId, retryCount, $"Exception: {exceptionMsg}");
                        });
                        
                        Log($"[EventRetryQueue] ✗ Event {eventId} ({eventType}) retry exception: {exceptionMsg}");
                    }

                    // Small delay between retries to avoid overwhelming the API
                    await Task.Delay(100);
                }

                Log($"[EventRetryQueue] Finished processing {pendingEvents.Count} events");
            }
            catch (Exception ex)
            {
                Log($"[EventRetryQueue FATAL] Error processing retry queue: {ex.Message}");
            }
        }
    }
}

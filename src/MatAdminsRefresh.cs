using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Timers;

namespace MatchZy
{
    public partial class MatchZy
    {
        private static readonly HttpClient MatAdminsHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        private string matchzyAdminsUrl = "";
        private int matchzyAdminsRefreshSeconds = 0;
        private CounterStrikeSharp.API.Modules.Timers.Timer? matchzyAdminsRefreshTimer = null;
        private bool matchzyAdminsFetchInFlight = false;

        private void StartMatchzyAdminsRefreshTimerIfConfigured(string reason)
        {
            try
            {
                matchzyAdminsRefreshTimer?.Kill();
            }
            catch
            {
                // ignore
            }
            matchzyAdminsRefreshTimer = null;

            if (string.IsNullOrWhiteSpace(matchzyAdminsUrl) || matchzyAdminsRefreshSeconds <= 0)
            {
                return;
            }

            // Fire once immediately, then poll.
            FetchAndApplyMatchzyAdmins(reason);
            matchzyAdminsRefreshTimer = AddTimer(matchzyAdminsRefreshSeconds, () =>
            {
                FetchAndApplyMatchzyAdmins("timer");
            }, TimerFlags.REPEAT);
        }

        private void FetchAndApplyMatchzyAdmins(string reason)
        {
            if (matchzyAdminsFetchInFlight) return;
            if (string.IsNullOrWhiteSpace(matchzyAdminsUrl)) return;

            var url = matchzyAdminsUrl.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Log($"[matchzy_admins] Invalid admins URL (must be http/https): {SecretRedactor.RedactText(url)}");
                return;
            }

            matchzyAdminsFetchInFlight = true;
            Task.Run(async () =>
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");

                    using var response = await MatAdminsHttpClient.SendAsync(request).ConfigureAwait(false);
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"[matchzy_admins] Fetch failed ({(int)response.StatusCode}) ({reason})");
                        return;
                    }

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    };

                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("admins", out var adminsEl) ||
                        adminsEl.ValueKind != JsonValueKind.Array)
                    {
                        Log($"[matchzy_admins] Invalid payload (missing 'admins' array) ({reason})");
                        return;
                    }

                    var dict = new Dictionary<string, string>();
                    foreach (var el in adminsEl.EnumerateArray())
                    {
                        if (el.ValueKind != JsonValueKind.String) continue;
                        var id = el.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        dict[id.Trim()] = "admin";
                    }

                    Server.NextFrame(() =>
                    {
                        try
                        {
                            WriteLegacyMatchzyAdminsJson(dict);
                            loadedAdmins = dict;
                            Log($"[matchzy_admins] Updated admins list: {loadedAdmins.Count} entries ({reason})");
                        }
                        catch (Exception ex)
                        {
                            Log($"[matchzy_admins] Failed to apply admins list: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log($"[matchzy_admins] Fetch exception ({reason}): {ex.Message}");
                }
                finally
                {
                    matchzyAdminsFetchInFlight = false;
                }
            });
        }

        private void WriteLegacyMatchzyAdminsJson(Dictionary<string, string> admins)
        {
            string fileName = "MatchZy/admins.json";
            string filePath = Path.Join(Server.GameDirectory + "/csgo/cfg", fileName);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(admins, options);
            File.WriteAllText(filePath, json);
        }
    }
}

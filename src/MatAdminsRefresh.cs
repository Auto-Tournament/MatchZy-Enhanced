using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Timers;

namespace AutoTournamentCS2
{
    public partial class AutoTournamentCS2
    {
        private static readonly HttpClient MatAdminsHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        private string atAdminsUrl = "";
        private int atAdminsRefreshSeconds = 0;
        private CounterStrikeSharp.API.Modules.Timers.Timer? atAdminsRefreshTimer = null;
        private bool atAdminsFetchInFlight = false;

        private void StartAutoTournamentCS2AdminsRefreshTimerIfConfigured(string reason)
        {
            try
            {
                atAdminsRefreshTimer?.Kill();
            }
            catch
            {
                // ignore
            }
            atAdminsRefreshTimer = null;

            if (string.IsNullOrWhiteSpace(atAdminsUrl) || atAdminsRefreshSeconds <= 0)
            {
                return;
            }

            // Fire once immediately, then poll.
            FetchAndApplyAutoTournamentCS2Admins(reason);
            atAdminsRefreshTimer = AddTimer(atAdminsRefreshSeconds, () =>
            {
                FetchAndApplyAutoTournamentCS2Admins("timer");
            }, TimerFlags.REPEAT);
        }

        private void FetchAndApplyAutoTournamentCS2Admins(string reason)
        {
            if (atAdminsFetchInFlight) return;
            if (string.IsNullOrWhiteSpace(atAdminsUrl)) return;

            var url = atAdminsUrl.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Log($"[at_admins] Invalid admins URL (must be http/https): {SecretRedactor.RedactText(url)}");
                return;
            }

            atAdminsFetchInFlight = true;
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
                        Log($"[at_admins] Fetch failed ({(int)response.StatusCode}) ({reason})");
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
                        Log($"[at_admins] Invalid payload (missing 'admins' array) ({reason})");
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
                            WriteLegacyAutoTournamentCS2AdminsJson(dict);
                            loadedAdmins = dict;
                            Log($"[at_admins] Updated admins list: {loadedAdmins.Count} entries ({reason})");
                        }
                        catch (Exception ex)
                        {
                            Log($"[at_admins] Failed to apply admins list: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log($"[at_admins] Fetch exception ({reason}): {ex.Message}");
                }
                finally
                {
                    atAdminsFetchInFlight = false;
                }
            });
        }

        private void WriteLegacyAutoTournamentCS2AdminsJson(Dictionary<string, string> admins)
        {
            string fileName = "AutoTournamentCS2/admins.json";
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

using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;

namespace AutoTournamentCS2
{
    public partial class AutoTournamentCS2
    {
        private bool atWarmupEnabled = false;
        private string atWarmupMessageHtml = "";
        private bool atWarmupRespawn = false;
        private bool atWarmupIgnoreWinConditions = false;
        private float atWarmupRoundtimeMinutes = 10.0f;
        private int atWarmupStartmoney = 16000;
        private int atWarmupMaxmoney = 16000;
        private bool atWarmupBuyAnywhere = false;
        private bool atWarmupInfiniteAmmo = false;

        private CounterStrikeSharp.API.Modules.Timers.Timer? atWarmupMessageTimer = null;

        private static string JoinArgs(CommandInfo command, int startIndex)
        {
            if (command.ArgCount <= startIndex) return "";
            return string.Join(" ", Enumerable.Range(startIndex, command.ArgCount - startIndex).Select(command.ArgByIndex));
        }

        private bool CanApplyAutoTournamentCS2WarmupSettings()
        {
            // Guardrail: these server-level warmup controls should only apply when the server is idle
            // and no match is currently loaded, so match configs remain authoritative.
            if (isMatchSetup) return false;
            var status = (tournamentStatus?.Value ?? "").Trim();
            if (!string.IsNullOrEmpty(status) && !string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        private void ApplyAutoTournamentCS2WarmupSettings(string reason)
        {
            if (!CanApplyAutoTournamentCS2WarmupSettings())
            {
                Log($"[at_warmup] Skipping apply ({reason}) - server not idle or match loaded.");
                return;
            }

            if (!atWarmupEnabled)
            {
                atWarmupMessageTimer?.Kill();
                atWarmupMessageTimer = null;

                // Best-effort revert to sane defaults.
                Server.ExecuteCommand(
                    "mp_ignore_round_win_conditions 0;" +
                    "mp_respawn_on_death_ct 0;" +
                    "mp_respawn_on_death_t 0;" +
                    "mp_buy_anywhere 0;" +
                    "sv_infinite_ammo 0;" +
                    "mp_startmoney 800;" +
                    "mp_maxmoney 16000;" +
                    "mp_roundtime 1.92;" +
                    "mp_roundtime_defuse 1.92;" +
                    "mp_roundtime_hostage 1.92;"
                );

                Log($"[at_warmup] Disabled ({reason})");
                return;
            }

            var ignoreWin = atWarmupIgnoreWinConditions ? 1 : 0;
            var respawn = atWarmupRespawn ? 1 : 0;
            var buy = atWarmupBuyAnywhere ? 1 : 0;
            var infAmmo = atWarmupInfiniteAmmo ? 2 : 0;
            var roundtime = Math.Clamp(atWarmupRoundtimeMinutes, 1.0f, 120.0f);
            var startmoney = Math.Clamp(atWarmupStartmoney, 0, 60000);
            var maxmoney = Math.Clamp(atWarmupMaxmoney, 0, 60000);

            Server.ExecuteCommand(
                "mp_warmup_start;" +
                "mp_warmup_pausetimer 1;" +
                "mp_warmuptime 9999;" +
                $"mp_ignore_round_win_conditions {ignoreWin};" +
                $"mp_respawn_on_death_ct {respawn};" +
                $"mp_respawn_on_death_t {respawn};" +
                $"mp_roundtime {roundtime};" +
                $"mp_roundtime_defuse {roundtime};" +
                $"mp_roundtime_hostage {roundtime};" +
                $"mp_startmoney {startmoney};" +
                $"mp_maxmoney {maxmoney};" +
                $"mp_buy_anywhere {buy};" +
                $"sv_infinite_ammo {infAmmo};"
            );

            atWarmupMessageTimer?.Kill();
            atWarmupMessageTimer = null;
            if (!string.IsNullOrWhiteSpace(atWarmupMessageHtml))
            {
                // Show immediately then repeat while enabled.
                PrintToCenterHtmlAll(atWarmupMessageHtml);
                atWarmupMessageTimer = AddTimer(10.0f, () =>
                {
                    if (!atWarmupEnabled) return;
                    if (!string.IsNullOrWhiteSpace(atWarmupMessageHtml))
                    {
                        PrintToCenterHtmlAll(atWarmupMessageHtml);
                    }
                }, TimerFlags.REPEAT);
            }

            Log($"[at_warmup] Applied settings ({reason})");
        }

        [ConsoleCommand("at_warmup_enable", "Enable/disable server-level warmup settings (idle servers only). Usage: at_warmup_enable 0|1")]
        public void AutoTournamentCS2WarmupEnable(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            var raw = command.ArgByIndex(1)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                Log("[at_warmup_enable] Usage: at_warmup_enable 0|1");
                return;
            }

            var enable = raw != "0";
            atWarmupEnabled = enable;
            database.SaveConfigValue("at_warmup_enable", enable ? "1" : "0");
            ApplyAutoTournamentCS2WarmupSettings("console");
        }

        [ConsoleCommand("at_warmup_message_html", "Set center HTML message shown during warmup (idle servers only). Usage: at_warmup_message_html <html|clear>")]
        public void AutoTournamentCS2WarmupMessageHtml(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            var value = JoinArgs(command, 1).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                Log("[at_warmup_message_html] Usage: at_warmup_message_html <html|clear>");
                return;
            }
            if (string.Equals(value, "clear", StringComparison.OrdinalIgnoreCase))
            {
                value = "";
            }

            atWarmupMessageHtml = value;
            database.SaveConfigValue("at_warmup_message_html", value);
            ApplyAutoTournamentCS2WarmupSettings("console");
        }

        [ConsoleCommand("at_warmup_respawn", "Enable/disable respawn on death during warmup (idle servers only). Usage: at_warmup_respawn 0|1")]
        public void AutoTournamentCS2WarmupRespawn(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            var raw = command.ArgByIndex(1)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                Log("[at_warmup_respawn] Usage: at_warmup_respawn 0|1");
                return;
            }
            atWarmupRespawn = raw != "0";
            database.SaveConfigValue("at_warmup_respawn", atWarmupRespawn ? "1" : "0");
            ApplyAutoTournamentCS2WarmupSettings("console");
        }

        [ConsoleCommand("at_warmup_ignore_win_conditions", "Enable/disable ignore win conditions during warmup (idle servers only). Usage: at_warmup_ignore_win_conditions 0|1")]
        public void AutoTournamentCS2WarmupIgnoreWinConditions(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            var raw = command.ArgByIndex(1)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                Log("[at_warmup_ignore_win_conditions] Usage: at_warmup_ignore_win_conditions 0|1");
                return;
            }
            atWarmupIgnoreWinConditions = raw != "0";
            database.SaveConfigValue("at_warmup_ignore_win_conditions", atWarmupIgnoreWinConditions ? "1" : "0");
            ApplyAutoTournamentCS2WarmupSettings("console");
        }

        [ConsoleCommand("at_warmup_roundtime_minutes", "Set warmup roundtime in minutes (idle servers only). Usage: at_warmup_roundtime_minutes <1..120>")]
        public void AutoTournamentCS2WarmupRoundtimeMinutes(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            var raw = command.ArgByIndex(1)?.Trim() ?? "";
            if (!float.TryParse(raw, out var minutes))
            {
                Log("[at_warmup_roundtime_minutes] Usage: at_warmup_roundtime_minutes <1..120>");
                return;
            }
            atWarmupRoundtimeMinutes = Math.Clamp(minutes, 1.0f, 120.0f);
            database.SaveConfigValue("at_warmup_roundtime_minutes", atWarmupRoundtimeMinutes.ToString());
            ApplyAutoTournamentCS2WarmupSettings("console");
        }

        [ConsoleCommand("at_warmup_startmoney", "Set warmup startmoney (idle servers only). Usage: at_warmup_startmoney <0..60000>")]
        public void AutoTournamentCS2WarmupStartmoney(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            var raw = command.ArgByIndex(1)?.Trim() ?? "";
            if (!int.TryParse(raw, out var v))
            {
                Log("[at_warmup_startmoney] Usage: at_warmup_startmoney <0..60000>");
                return;
            }
            atWarmupStartmoney = Math.Clamp(v, 0, 60000);
            database.SaveConfigValue("at_warmup_startmoney", atWarmupStartmoney.ToString());
            ApplyAutoTournamentCS2WarmupSettings("console");
        }

        [ConsoleCommand("at_warmup_maxmoney", "Set warmup maxmoney (idle servers only). Usage: at_warmup_maxmoney <0..60000>")]
        public void AutoTournamentCS2WarmupMaxmoney(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            var raw = command.ArgByIndex(1)?.Trim() ?? "";
            if (!int.TryParse(raw, out var v))
            {
                Log("[at_warmup_maxmoney] Usage: at_warmup_maxmoney <0..60000>");
                return;
            }
            atWarmupMaxmoney = Math.Clamp(v, 0, 60000);
            database.SaveConfigValue("at_warmup_maxmoney", atWarmupMaxmoney.ToString());
            ApplyAutoTournamentCS2WarmupSettings("console");
        }

        [ConsoleCommand("at_warmup_buy_anywhere", "Enable/disable buy anywhere during warmup (idle servers only). Usage: at_warmup_buy_anywhere 0|1")]
        public void AutoTournamentCS2WarmupBuyAnywhere(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            var raw = command.ArgByIndex(1)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                Log("[at_warmup_buy_anywhere] Usage: at_warmup_buy_anywhere 0|1");
                return;
            }
            atWarmupBuyAnywhere = raw != "0";
            database.SaveConfigValue("at_warmup_buy_anywhere", atWarmupBuyAnywhere ? "1" : "0");
            ApplyAutoTournamentCS2WarmupSettings("console");
        }

        [ConsoleCommand("at_warmup_infinite_ammo", "Enable/disable infinite ammo during warmup (idle servers only). Usage: at_warmup_infinite_ammo 0|1")]
        public void AutoTournamentCS2WarmupInfiniteAmmo(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            var raw = command.ArgByIndex(1)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                Log("[at_warmup_infinite_ammo] Usage: at_warmup_infinite_ammo 0|1");
                return;
            }
            atWarmupInfiniteAmmo = raw != "0";
            database.SaveConfigValue("at_warmup_infinite_ammo", atWarmupInfiniteAmmo ? "1" : "0");
            ApplyAutoTournamentCS2WarmupSettings("console");
        }
    }
}

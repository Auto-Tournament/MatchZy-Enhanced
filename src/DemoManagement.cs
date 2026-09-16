using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;

namespace MatchZy
{
    public partial class MatchZy
    {
        public string demoPath = "MatchZy/";
        public string demoNameFormat = "{TIME}_{MATCH_ID}_{MAP}_{TEAM1}_vs_{TEAM2}";
        public string demoUploadURL = "";
        public string demoUploadHeaderKey = "";
        public string demoUploadHeaderValue = "";

        public string activeDemoFile = "";

        public bool isDemoRecording = false;

        // When the current tv_record was issued; bounds the fallback demo search so an older
        // demo of the same match id is never uploaded instead.
        private DateTime? demoRecordingStartedUtc = null;

        /// <summary>
        /// True when a SourceTV master exists. It is only created on map load with tv_enable 1;
        /// without it tv_record prints "Only TV Master can record demos instantly." and writes nothing.
        /// </summary>
        private bool IsSourceTvActive()
        {
            try
            {
                foreach (var controller in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
                {
                    if (controller != null && controller.IsValid && controller.IsHLTV) return true;
                }
            }
            catch (Exception ex)
            {
                Log($"[IsSourceTvActive] Failed to inspect controllers: {ex.Message}");
            }
            return false;
        }
        public bool isDemoRecordingEnabled = true;
        
        // Track if demoUploadURL was set dynamically (via API/web panel) to prevent config file from overwriting it
        private bool demoUploadURLSetDynamically = false;

        public void StartDemoRecording()
        {
            if (!isDemoRecordingEnabled)
            {
                Log("[StartDemoRecording] Demo recording is disabled. Set matchzy_demo_recording_enabled to true to enable.");
                Log("[DEMO_RECORDING] DISABLED (matchzy_demo_recording_enabled=0)");
                return;
            }
            if (isDemoRecording)
            {
                Log("[StartDemoRecording] Demo recording is already in progress.");
                Log("[DEMO_RECORDING] ALREADY_RECORDING");
                return;
            }
            
            // Ensure GOTV is enabled (required for tv_record demos).
            // We force this to reduce operational foot-guns (server configs differ).
            bool tvEnable = false;
            try
            {
                var tv = ConVar.Find("tv_enable");
                if (tv != null)
                {
                    tvEnable = tv.GetPrimitiveValue<bool>();
                    if (!tvEnable)
                    {
                        Log("[StartDemoRecording] tv_enable is 0. Forcing tv_enable 1 to ensure demo recording works.");
                        tv.SetValue(1);
                        // Re-read after forcing.
                        tvEnable = tv.GetPrimitiveValue<bool>();
                        Log($"[StartDemoRecording] tv_enable now: {(tvEnable ? 1 : 0)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[StartDemoRecording] Failed to force tv_enable: {ex.Message}");
            }
            
            string demoFileName = FormatCvarValue(demoNameFormat.Replace(" ", "_")) + ".dem";
            try
            {
                // Make sure the target directory exists before tv_record (fresh servers have no MatchZy/ yet).
                string directoryPath = Path.Join(Server.GameDirectory + "/csgo/", DemoFileLocator.NormalizeDemoPath(demoPath));
                if (!Directory.Exists(directoryPath))
                {
                    Directory.CreateDirectory(directoryPath);
                    Log($"[StartDemoRecording] Created demo directory: {directoryPath}");
                }

                bool sourceTvActive = IsSourceTvActive();
                if (!sourceTvActive)
                {
                    // Nothing we can do mid-match without a map reload; LoadMatch reloads the map for
                    // this case. Log loudly so a missing demo is explained in the server log.
                    Log("[StartDemoRecording] WARNING: no SourceTV master is running (it is only created on map load with tv_enable 1). tv_record will not write a demo.");
                    Log($"[DEMO_RECORDING] NO_SOURCETV file=\"{demoFileName}\"");
                }
                string tempDemoPath = demoPath == "" ? demoFileName : demoPath + demoFileName;
                activeDemoFile = tempDemoPath;
                string fullPath = Path.Join(Server.GameDirectory + "/csgo/", tempDemoPath);
                Log($"[StartDemoRecording] Starting demo recording:");
                Log($"[StartDemoRecording]   - Demo file: {demoFileName}");
                Log($"[StartDemoRecording]   - Relative path: {tempDemoPath}");
                Log($"[StartDemoRecording]   - Full path: {fullPath}");
                Log($"[StartDemoRecording]   - GOTV enabled: {tvEnable}");
                Log($"[DEMO_RECORDING] START file=\"{demoFileName}\" rel=\"{tempDemoPath}\" gotv={(tvEnable ? 1 : 0)}");
                Server.ExecuteCommand($"tv_record {tempDemoPath}");
                isDemoRecording = true;
                demoRecordingStartedUtc = DateTime.UtcNow;
                Log($"[StartDemoRecording] Demo recording started{(sourceTvActive ? " successfully" : " (SourceTV missing, demo likely not written)")}.");

                // Emit structured event for MAT admin event viewer (best-effort).
                // This will be queued for retry automatically if MAT is temporarily unreachable.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendEventAsync(new MatchZyDemoRecordingStartedEvent
                        {
                            MatchId = liveMatchId,
                            MapNumber = matchConfig.CurrentMapNumber,
                            FileName = demoFileName
                        });
                    }
                    catch { /* best-effort */ }
                });
            }
            catch (Exception ex)
            {
                Log($"[StartDemoRecording - FATAL] Error: {ex.Message}. Starting demo recording with path. Name: {demoFileName}");
                // This is to avoid demo loss in any case of exception
                Server.ExecuteCommand($"tv_record {demoFileName}");
                isDemoRecording = true;
                demoRecordingStartedUtc = DateTime.UtcNow;
                Log($"[DEMO_RECORDING] START_FALLBACK file=\"{demoFileName}\"");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendEventAsync(new MatchZyDemoRecordingStartedEvent
                        {
                            MatchId = liveMatchId,
                            MapNumber = matchConfig.CurrentMapNumber,
                            FileName = demoFileName
                        });
                    }
                    catch { /* best-effort */ }
                });
            }

        }

        public void StopDemoRecording(float delay, string activeDemoFile, long liveMatchId, int currentMapNumber)
        {
            Log($"[StopDemoRecording] Going to stop demorecording in {delay}s");
            string demoPath = Path.Join(Server.GameDirectory + "/csgo/", activeDemoFile);
            // Captured now: by the time the upload runs the next map may already be loaded.
            string mapName = Server.MapName;
            DateTime? recordingStartedUtc = demoRecordingStartedUtc;
            (int t1score, int t2score) = GetTeamsScore();
            int roundNumber = t1score + t2score;
            Log($"[StopDemoRecording] Demo info - MatchId: {liveMatchId}, MapNumber: {currentMapNumber}, Rounds: {roundNumber}");
            Log($"[StopDemoRecording] Demo file path: {demoPath}");
            Log($"[StopDemoRecording] Upload URL configured: {(string.IsNullOrEmpty(demoUploadURL) ? "NO (demos will only be saved locally)" : $"YES ({demoUploadURL})")}");
            Log($"[DEMO_RECORDING] STOP_SCHEDULED in={delay:0.##}s matchId={liveMatchId} map={currentMapNumber} rounds={roundNumber} upload={(string.IsNullOrEmpty(demoUploadURL) ? 0 : 1)}");
            
            AddTimer(delay, () =>
            {
                if (isDemoRecording)
                {
                    Log($"[StopDemoRecording] Executing tv_stoprecord command...");
                    Server.ExecuteCommand($"tv_stoprecord");
                }
                isDemoRecording = false;
                Log($"[DEMO_RECORDING] STOPPED matchId={liveMatchId} map={currentMapNumber}");

                // Emit structured stop marker (best-effort).
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendEventAsync(new MatchZyDemoRecordingStoppedEvent
                        {
                            MatchId = liveMatchId,
                            MapNumber = currentMapNumber,
                            FileName = Path.GetFileName(demoPath)
                        });
                    }
                    catch { /* best-effort */ }
                });

                Log($"[StopDemoRecording] Demo recording stopped. Waiting 15s for file to be written to disk before upload...");
                AddTimer(15, () =>
                {
                    // Notify players that upload is starting
                    if (!string.IsNullOrEmpty(demoUploadURL))
                    {
                        PrintToAllChat($"{ChatColors.Grey}Uploading demo to API...{ChatColors.Default}");
                        Log($"[DEMO_UPLOAD] QUEUED matchId={liveMatchId} map={currentMapNumber} round={roundNumber} file=\"{Path.GetFileName(demoPath)}\"");
                    }
                    string csgoDirectory = Server.GameDirectory + "/csgo";
                    var searchDirs = DemoFileLocator.CandidateDirectories(csgoDirectory, demoPath, this.demoPath);
                    // Allow some clock/flush slack before the recording start.
                    DateTime? notBefore = recordingStartedUtc?.AddMinutes(-2);
                    _ = Task.Run(async () =>
                    {
                        string uploadPath = ResolveDemoFileForUpload(demoPath, searchDirs, liveMatchId, mapName, notBefore);
                        await UploadFileAsync(uploadPath, demoUploadURL, demoUploadHeaderKey, demoUploadHeaderValue, liveMatchId, currentMapNumber, roundNumber);
                    });
                });
            });
        }

        /// <summary>
        /// Returns the demo file to upload: the expected path, or the match's demo found by searching
        /// the demo directories when the expected file is missing. Runs off the game thread (file IO only).
        /// </summary>
        private string ResolveDemoFileForUpload(string expectedPath, IReadOnlyList<string> searchDirs, long matchId, string mapName, DateTime? notBeforeUtc)
        {
            string? resolved = DemoFileLocator.Resolve(
                expectedPath,
                searchDirs,
                matchId,
                mapName,
                notBeforeUtc,
                File.Exists,
                dir => Directory.Exists(dir)
                    ? new DirectoryInfo(dir).EnumerateFiles("*.dem", SearchOption.TopDirectoryOnly)
                        .Select(f => new DemoFileCandidate(f.FullName, f.LastWriteTimeUtc))
                    : Enumerable.Empty<DemoFileCandidate>(),
                msg => Log($"[DEMO_UPLOAD] RESOLVE matchId={matchId} {msg}"));

            if (resolved == null)
            {
                Log($"[DEMO_UPLOAD] RESOLVE matchId={matchId} no demo found in: {string.Join(", ", searchDirs)}");
                return expectedPath;
            }
            if (resolved != expectedPath)
            {
                Log($"[DEMO_UPLOAD] RESOLVE matchId={matchId} expected file missing, uploading found demo {resolved}");
            }
            return resolved;
        }

        public int GetTvDelay()
        {
            bool tvEnable = ConVar.Find("tv_enable")!.GetPrimitiveValue<bool>();
            if (!tvEnable) return 0;

            bool tvEnable1 = ConVar.Find("tv_enable1")!.GetPrimitiveValue<bool>();
            int tvDelay = ConVar.Find("tv_delay")!.GetPrimitiveValue<int>();

            if (!tvEnable1) return tvDelay;
            int tvDelay1 = ConVar.Find("tv_delay1")!.GetPrimitiveValue<int>();

            if (tvDelay < tvDelay1) return tvDelay1;
            return tvDelay;
        }

        [ConsoleCommand("get5_demo_upload_header_key", "If defined, a custom HTTP header with this name is added to the HTTP requests for demos")]
        [ConsoleCommand("matchzy_demo_upload_header_key", "If defined, a custom HTTP header with this name is added to the HTTP requests for demos")]
        public void DemoUploadHeaderKeyCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string header = command.ArgByIndex(1).Trim();

            if (header != "") demoUploadHeaderKey = header;
        }

        [ConsoleCommand("get5_demo_upload_header_value", "If defined, the value of the custom header added to the demos sent over HTTP")]
        [ConsoleCommand("matchzy_demo_upload_header_value", "If defined, the value of the custom header added to the demos sent over HTTP")]
        public void DemoUploadHeaderValueCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string headerValue = command.ArgByIndex(1).Trim();

            if (headerValue != "") demoUploadHeaderValue = headerValue;
        }
    }
}

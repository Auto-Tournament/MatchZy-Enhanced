using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MatchZy;

/// <summary>A .dem file seen on disk.</summary>
public sealed record DemoFileCandidate(string Path, DateTime LastWriteUtc);

/// <summary>
/// Pure rules for where a match demo is written and how to find it again, kept free of
/// CounterStrikeSharp so they can be unit tested. File-system access is passed in.
///
/// QA (match 62, s_1, first match after a server restart): the plugin sent demo_upload_fail
/// reason file_not_found. The server had booted straight onto the match map with no SourceTV
/// master, the match load did not change level, and the engine answered tv_record with
/// "Only TV Master can record demos instantly." so nothing was written.
/// </summary>
public static class DemoFileLocator
{
    /// <summary>
    /// matchzy_demo_path relative to csgo/, always ending in '/' (or empty for csgo/ itself).
    /// </summary>
    public static string NormalizeDemoPath(string? demoPath)
    {
        string p = (demoPath ?? "").Trim().Replace('\\', '/');
        while (p.StartsWith("/")) p = p.Substring(1);
        if (p == "" || p == ".") return "";
        return p.EndsWith("/") ? p : p + "/";
    }

    /// <summary>
    /// A match load has to reload the map when demos are wanted but no SourceTV master exists:
    /// the master is only created on map load, and tv_record without it records nothing.
    /// </summary>
    public static bool RequiresMapReloadForSourceTv(bool demoRecordingEnabled, bool sourceTvActive)
    {
        return demoRecordingEnabled && !sourceTvActive;
    }

    /// <summary>
    /// The absolute path tv_record is given: &lt;game&gt;/csgo/&lt;demo path&gt;&lt;file name&gt;, with '/'
    /// separators on every OS (Windows accepts them, and a game directory's backslashes are
    /// converted).
    ///
    /// Issue #35: with a relative path, tv_record writes to the first writable Game search path.
    /// Metamod puts csgo/addons/metamod first in gameinfo.gi, so demos landed in
    /// csgo/addons/metamod/MatchZy/ while the plugin logged, and looked for them in, csgo/MatchZy/.
    /// </summary>
    public static string TvRecordPath(string gameDirectory, string? demoPath, string fileName)
    {
        string game = (gameDirectory ?? "").Replace('\\', '/').TrimEnd('/');
        return $"{game}/csgo/{NormalizeDemoPath(demoPath)}{fileName}";
    }

    /// <summary>
    /// The tv_record argument for <paramref name="path"/>: quoted only when it contains a space
    /// (a Windows install under "Program Files"), so the usual Linux path is passed exactly as an
    /// operator would type it.
    /// </summary>
    public static string TvRecordArgument(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>
    /// Directories to look in, most specific first, without duplicates. The csgo/addons/metamod
    /// ones find demos that builds before 1.4.35 recorded there (see <see cref="TvRecordPath"/>).
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories(string csgoDirectory, string expectedFullPath, string? demoPath)
    {
        var dirs = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return;
            string norm = d.Replace('\\', '/').TrimEnd('/');
            if (norm == "") return;
            if (!dirs.Contains(norm)) dirs.Add(norm);
        }

        string metamodDirectory = System.IO.Path.Join(csgoDirectory, "addons", "metamod");
        Add(System.IO.Path.GetDirectoryName(expectedFullPath));
        Add(System.IO.Path.Join(csgoDirectory, NormalizeDemoPath(demoPath)));
        Add(System.IO.Path.Join(metamodDirectory, NormalizeDemoPath(demoPath)));
        Add(csgoDirectory);
        Add(metamodDirectory);
        return dirs;
    }

    /// <summary>
    /// Picks the demo for this match among files found on disk: the expected file name first,
    /// otherwise the newest .dem whose name carries the match id as its own token and the map
    /// name, written after <paramref name="notBeforeUtc"/> (when known) so an older demo of a
    /// reloaded match is not uploaded in its place.
    /// </summary>
    public static string? FindDemoForMatch(
        IEnumerable<DemoFileCandidate> files, string expectedFileName, long matchId, string? mapName, DateTime? notBeforeUtc)
    {
        var list = files.Where(f => f.Path.EndsWith(".dem", StringComparison.OrdinalIgnoreCase)).ToList();

        var exact = list.FirstOrDefault(f =>
            string.Equals(System.IO.Path.GetFileName(f.Path), expectedFileName, StringComparison.Ordinal));
        if (exact != null) return exact.Path;

        string idToken = $"_{matchId}_";
        return list
            .Where(f =>
            {
                string name = System.IO.Path.GetFileName(f.Path);
                if (!("_" + name).Contains(idToken, StringComparison.Ordinal)) return false;
                if (!string.IsNullOrEmpty(mapName) && !name.Contains(mapName, StringComparison.OrdinalIgnoreCase)) return false;
                if (notBeforeUtc.HasValue && f.LastWriteUtc < notBeforeUtc.Value) return false;
                return true;
            })
            .OrderByDescending(f => f.LastWriteUtc)
            .Select(f => f.Path)
            .FirstOrDefault();
    }

    /// <summary>
    /// Resolves the demo to upload: the expected path when it exists, otherwise a search of the
    /// candidate directories. Every path checked is reported through <paramref name="log"/>.
    /// </summary>
    public static string? Resolve(
        string expectedFullPath,
        IReadOnlyList<string> directories,
        long matchId,
        string? mapName,
        DateTime? notBeforeUtc,
        Func<string, bool> fileExists,
        Func<string, IEnumerable<DemoFileCandidate>> listDemos,
        Action<string> log)
    {
        log($"checking expected path {expectedFullPath}");
        if (fileExists(expectedFullPath)) return expectedFullPath;

        string expectedName = System.IO.Path.GetFileName(expectedFullPath);
        foreach (var dir in directories)
        {
            IEnumerable<DemoFileCandidate> files;
            try
            {
                files = listDemos(dir).ToList();
            }
            catch (Exception ex)
            {
                log($"searching {dir}: {ex.Message}");
                continue;
            }

            string? found = FindDemoForMatch(files, expectedName, matchId, mapName, notBeforeUtc);
            log($"searching {dir} for matchId={matchId} map={mapName}: {(found ?? "no match")}");
            if (found != null) return found;
        }

        return null;
    }
}

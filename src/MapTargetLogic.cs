using System.Text.RegularExpressions;

namespace AutoTournamentCS2;

public enum MapChangeKind
{
    /// <summary>Unusable input; nothing is sent to the server.</summary>
    Invalid,
    /// <summary>An installed map: <c>changelevel &lt;name&gt;</c>.</summary>
    Standard,
    /// <summary>A workshop item id: <c>host_workshop_map &lt;id&gt;</c>.</summary>
    WorkshopId,
    /// <summary>A workshop map known only by name: <c>ds_workshop_changelevel &lt;name&gt;</c>.</summary>
    WorkshopName,
}

/// <summary>
/// Pure map-change rules with no CounterStrikeSharp dependency, so they can be unit
/// tested outside the game server (see tests/AutoTournamentCS2.Tests).
///
/// Workshop maps broke the admin map commands because <c>Server.MapName</c> on a
/// workshop map is the map's own name (e.g. "de_cache_classic"), not its workshop id,
/// and <c>Server.IsMapValid</c> only knows installed maps. So .rmap, .map &lt;name&gt;,
/// .restore and the match map change all fell through to "Invalid map name!" or did
/// nothing at all.
/// </summary>
public static class MapTargetLogic
{
    // Only characters a map name or workshop path can contain. The value is spliced
    // into a server command, so quotes and ';' must never get through.
    private static readonly Regex SafeMapName = new(@"^[A-Za-z0-9_\-./]+$", RegexOptions.Compiled);
    private static readonly Regex WorkshopPath = new(@"^workshop/(\d+)(?:/.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WorkshopUrlId = new(@"[?&]id=(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Workshop id from a bare id ("3070212801"), a workshop path
    /// ("workshop/3070212801/de_foo") or a Steam workshop URL (".../?id=3070212801").
    /// </summary>
    public static string? ParseWorkshopId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string s = input.Trim();
        if (s.All(char.IsAsciiDigit) && s.Length > 0) return s;
        var path = WorkshopPath.Match(s);
        if (path.Success) return path.Groups[1].Value;
        if (s.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
        {
            var url = WorkshopUrlId.Match(s);
            if (url.Success) return url.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// Decides how to change to <paramref name="input"/>.
    /// <paramref name="isMapValid"/> is Server.IsMapValid; <paramref name="knownWorkshopId"/>
    /// looks up the workshop id of a map name this plugin has loaded before.
    /// Short names ("mirage") get "de_" only when that makes an installed map.
    /// Anything else not installed is treated as a workshop map name.
    /// </summary>
    public static (MapChangeKind Kind, string Argument) Resolve(
        string? input,
        Func<string, bool> isMapValid,
        Func<string, string?> knownWorkshopId)
    {
        if (string.IsNullOrWhiteSpace(input)) return (MapChangeKind.Invalid, "");
        string name = input.Trim();

        string? id = ParseWorkshopId(name);
        if (id != null) return (MapChangeKind.WorkshopId, id);

        if (!SafeMapName.IsMatch(name)) return (MapChangeKind.Invalid, "");

        // A name whose last load came from a workshop id goes back to that id, even if
        // an installed map shares the name (a workshop dust2 variant, say).
        string? knownId = knownWorkshopId(name);
        if (knownId != null) return (MapChangeKind.WorkshopId, knownId);

        if (isMapValid(name)) return (MapChangeKind.Standard, name);

        if (!name.Contains('_') && isMapValid("de_" + name)) return (MapChangeKind.Standard, "de_" + name);

        // Not installed and not seen before: ask the server to find it among its
        // workshop maps (host_workshop_collection / already downloaded).
        return (MapChangeKind.WorkshopName, name);
    }

    /// <summary>
    /// Whether the server is already on <paramref name="target"/> (a map name or a
    /// workshop id). A workshop id matches when that id is what loaded the current map.
    /// </summary>
    public static bool IsCurrentMap(string? target, string? currentMapName, Func<string, string?> knownWorkshopId)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(currentMapName)) return false;
        string t = target.Trim();
        if (string.Equals(t, currentMapName, StringComparison.OrdinalIgnoreCase)) return true;
        string? id = ParseWorkshopId(t);
        return id != null && knownWorkshopId(currentMapName) == id;
    }
}

/// <summary>
/// Remembers which workshop id produced which map name, so a workshop map can be
/// reloaded or recognised later even though Server.MapName only reports its name.
/// </summary>
public sealed class WorkshopMapRegistry
{
    private readonly Dictionary<string, string> idByMapName = new(StringComparer.OrdinalIgnoreCase);
    private string? pendingId;

    /// <summary>Call right before issuing host_workshop_map &lt;id&gt;.</summary>
    public void MarkPending(string workshopId) => pendingId = workshopId;

    /// <summary>Call from OnMapStart: binds a pending workshop id to the map that loaded.</summary>
    public void OnMapStarted(string? mapName)
    {
        if (!string.IsNullOrWhiteSpace(mapName))
        {
            // Only names whose latest load came from a workshop id are remembered, so a
            // later changelevel to an installed map of the same name clears the link.
            if (pendingId != null) idByMapName[mapName] = pendingId;
            else idByMapName.Remove(mapName);
        }
        pendingId = null;
    }

    public string? IdFor(string mapName) =>
        idByMapName.TryGetValue(mapName, out var id) ? id : null;
}

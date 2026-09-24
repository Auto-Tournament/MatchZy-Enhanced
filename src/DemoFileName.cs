using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MatchZy;

/// <summary>
/// Builds the demo file name from matchzy_demo_name_format. Kept free of CounterStrikeSharp so
/// it can be unit tested.
///
/// Issue #35: the name used to be the format with only spaces replaced. A workshop map name
/// (workshop/123/de_mirage) added slashes, so tv_record got sub-folders that don't exist, and a
/// team name with ; " ' | ? * : \ / broke the path or split the console command. The name is
/// now limited to ASCII letters, digits, '-', '_' and '.'.
///
/// Non-ASCII letters are transliterated (Cyrillic to Latin, accents dropped), not kept. Besides
/// tv_record and the disk, the name is sent in the MatchZy-FileName and Get5-FileName headers of
/// the demo upload, and .NET's HttpClient refuses non-ASCII header values ("Request headers must
/// contain only ASCII characters"), so a demo named after a Cyrillic team, or "SadBoy ♥", could
/// never be uploaded even if tv_record wrote it.
/// </summary>
public static class DemoFileName
{
    /// <summary>Longest name returned, without ".dem". Well under the 255-byte file name limit.</summary>
    public const int MaxLength = 180;

    /// <summary>
    /// Expands the placeholders in <paramref name="format"/> and returns a safe file name without
    /// the ".dem" extension. Never returns an empty name: when nothing usable is left, the name
    /// is {TIME}_{MATCH_ID}.
    /// </summary>
    public static string Build(
        string? format,
        DateTime now,
        long matchId,
        string? mapName,
        int mapNumber,
        string? team1Name,
        string? team2Name,
        int team1Score,
        int team2Score)
    {
        string time = FormatTime(now);
        string expanded = (format ?? "")
            .Replace("{TIME}", time)
            .Replace("{MATCH_ID}", matchId.ToString(CultureInfo.InvariantCulture))
            .Replace("{MAP}", MapToken(mapName))
            .Replace("{MAPNUMBER}", mapNumber.ToString(CultureInfo.InvariantCulture))
            .Replace("{TEAM1}", team1Name ?? "")
            .Replace("{TEAM2}", team2Name ?? "")
            .Replace("{TEAM1_SCORE}", team1Score.ToString(CultureInfo.InvariantCulture))
            .Replace("{TEAM2_SCORE}", team2Score.ToString(CultureInfo.InvariantCulture));

        string name = Sanitize(expanded);
        if (!HasLetterOrDigit(name))
        {
            name = Sanitize($"{time}_{matchId.ToString(CultureInfo.InvariantCulture)}");
        }
        return name;
    }

    /// <summary>The time as used in demo names: 2026-09-24_18-05-09.</summary>
    public static string FormatTime(DateTime now) => now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The map as it appears in a demo name: the last path segment of a workshop map name
    /// (workshop/3070284539/de_mirage becomes de_mirage), sanitised.
    /// </summary>
    public static string MapToken(string? mapName)
    {
        string map = (mapName ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        int slash = map.LastIndexOf('/');
        if (slash >= 0) map = map.Substring(slash + 1);
        return Sanitize(map);
    }

    /// <summary>
    /// Keeps ASCII letters, digits, '-', '_' and '.'. Cyrillic is transliterated and accents are
    /// dropped; anything else becomes '_'. Runs of '_' or '.' are collapsed, and '_' and '.' are
    /// trimmed from both ends (no hidden files, no "..").
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (IsSafeAscii(c))
            {
                sb.Append(c);
            }
            else if (TryTransliterate(c, out string latin))
            {
                sb.Append(latin);
            }
            else
            {
                sb.Append('_');
            }
        }

        string collapsed = Collapse(sb.ToString());
        if (collapsed.Length > MaxLength)
        {
            collapsed = collapsed.Substring(0, MaxLength).Trim('_', '.');
        }
        return collapsed;
    }

    private static bool IsSafeAscii(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';

    private static bool HasLetterOrDigit(string s)
    {
        foreach (char c in s)
        {
            if (char.IsAsciiLetterOrDigit(c)) return true;
        }
        return false;
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if ((c == '_' || c == '.') && sb.Length > 0 && sb[^1] == c) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim('_', '.');
    }

    private static readonly Dictionary<char, string> Cyrillic = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
        // Ukrainian and Belarusian letters.
        ['і'] = "i", ['ї'] = "yi", ['є'] = "ye", ['ґ'] = "g", ['ў'] = "u",
    };

    // Latin letters that don't decompose into a base letter plus accents.
    private static readonly Dictionary<char, string> LatinSpecial = new()
    {
        ['ø'] = "o", ['æ'] = "ae", ['œ'] = "oe", ['ß'] = "ss", ['ð'] = "d", ['þ'] = "th",
        ['đ'] = "d", ['ł'] = "l", ['ı'] = "i",
    };

    private static bool TryTransliterate(char c, out string latin)
    {
        latin = "";
        // Emoji and other characters outside the basic plane come as surrogate pairs; each half
        // becomes '_' (and the two collapse into one).
        if (char.IsSurrogate(c) || char.IsControl(c)) return false;

        char lower = char.ToLowerInvariant(c);
        bool upper = lower != c;

        if (Cyrillic.TryGetValue(lower, out string? mapped) || LatinSpecial.TryGetValue(lower, out mapped))
        {
            latin = upper && mapped.Length > 0 ? char.ToUpperInvariant(mapped[0]) + mapped.Substring(1) : mapped;
            // A letter that transliterates to nothing (ъ, ь) is dropped, not replaced.
            return true;
        }

        // é -> e, å -> a, ñ -> n: keep the base letter, drop the accents.
        string decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char d in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(d) == UnicodeCategory.NonSpacingMark) continue;
            if (!IsSafeAscii(d)) return false;
            sb.Append(d);
        }
        latin = sb.ToString();
        return latin.Length > 0;
    }
}

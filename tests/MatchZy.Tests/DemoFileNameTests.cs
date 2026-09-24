using MatchZy;
using Xunit;

namespace MatchZy.Tests;

/// <summary>Issue #35: the demo file name must be a single, safe file name for any map or team.</summary>
public class DemoFileNameTests
{
    private const string DefaultFormat = "{TIME}_{MATCH_ID}_{MAP}_{TEAM1}_vs_{TEAM2}";
    private static readonly DateTime Now = new(2026, 9, 24, 20, 57, 56);

    private static string Build(string? format, string? map = "de_mirage", string? team1 = "Alpha", string? team2 = "Bravo", long matchId = 9) =>
        DemoFileName.Build(format, Now, matchId, map, 1, team1, team2, 13, 7);

    private static void AssertSafe(string name)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.All(name, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '.',
            $"unsafe character '{c}' in \"{name}\""));
        Assert.DoesNotContain("__", name);
        Assert.DoesNotContain("..", name);
        Assert.False(name.StartsWith(".") || name.StartsWith("_") || name.EndsWith("_") || name.EndsWith("."), name);
    }

    [Fact]
    public void PlainNamesAreUnchanged()
    {
        Assert.Equal("2026-09-24_20-57-56_9_de_mirage_Alpha_vs_Bravo", Build(DefaultFormat));
    }

    [Fact]
    public void SpacesStillBecomeUnderscores()
    {
        Assert.Equal("2026-09-24_20-57-56_9_de_mirage_Team_Alpha_vs_Team_Bravo", Build(DefaultFormat, team1: "Team Alpha", team2: "Team Bravo"));
    }

    [Fact]
    public void AllPlaceholdersAreExpanded()
    {
        string name = DemoFileName.Build("{TIME}-{MATCH_ID}-{MAP}-{MAPNUMBER}-{TEAM1}-{TEAM2}-{TEAM1_SCORE}-{TEAM2_SCORE}", Now, 42, "de_nuke", 2, "A", "B", 13, 11);
        Assert.Equal("2026-09-24_20-57-56-42-de_nuke-2-A-B-13-11", name);
    }

    [Theory]
    [InlineData("workshop/3070284539/de_mirage", "de_mirage")]
    [InlineData("workshop\\3070284539\\de_mirage", "de_mirage")]
    [InlineData("workshop/3070284539/de_mirage/", "de_mirage")]
    [InlineData("de_dust2", "de_dust2")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void MapTokenIsTheLastPathSegment(string? map, string expected)
    {
        Assert.Equal(expected, DemoFileName.MapToken(map));
    }

    [Fact]
    public void WorkshopMapGivesNoSubFolders()
    {
        string name = Build(DefaultFormat, map: "workshop/3070284539/de_mirage");
        Assert.Equal("2026-09-24_20-57-56_9_de_mirage_Alpha_vs_Bravo", name);
        AssertSafe(name);
    }

    // The reporter's name on issue #35.
    [Fact]
    public void HeartSymbolIsReplaced()
    {
        string name = Build(DefaultFormat, team1: "SadBoy ♥", team2: "team_SadBoy ♥");
        Assert.Equal("2026-09-24_20-57-56_9_de_mirage_SadBoy_vs_team_SadBoy", name);
        AssertSafe(name);
    }

    [Theory]
    [InlineData("a/b\\c:d;e\"f'g|h?i*j<k>l", "a_b_c_d_e_f_g_h_i_j_k_l")]
    [InlineData("Na'Vi", "Na_Vi")]
    [InlineData("rm -rf; quit", "rm_-rf_quit")]
    [InlineData("tab\there\nnewline", "tab_here_newline")]
    [InlineData("🔥 Fire 🔥", "Fire")]
    [InlineData("Team.Liquid", "Team.Liquid")]
    [InlineData("...hidden", "hidden")]
    [InlineData("a....b", "a.b")]
    [InlineData("___x___", "x")]
    public void UnsafeCharactersAreReplacedAndCollapsed(string input, string expected)
    {
        Assert.Equal(expected, DemoFileName.Sanitize(input));
    }

    [Theory]
    [InlineData("Команда", "Komanda")]
    [InlineData("Щука ЖЁЛТАЯ", "Shchuka_ZhELTAYa")]
    [InlineData("Объект", "Obekt")]
    [InlineData("Київ", "Kiyiv")]
    [InlineData("Æsir Øl Blåbær", "Aesir_Ol_Blabaer")]
    [InlineData("Café Señor Straße", "Cafe_Senor_Strasse")]
    public void NonAsciiLettersAreTransliterated(string input, string expected)
    {
        Assert.Equal(expected, DemoFileName.Sanitize(input));
    }

    [Theory]
    [InlineData("战队")]
    [InlineData("♥♥♥")]
    [InlineData("/;:")]
    [InlineData("")]
    public void UnusableScriptsAreReplaced(string input)
    {
        Assert.DoesNotContain(DemoFileName.Sanitize(input), c => !char.IsAscii(c));
    }

    [Theory]
    [InlineData("{TEAM1}", "战队")]
    [InlineData("{TEAM1}", "♥")]
    [InlineData("", "Alpha")]
    [InlineData(null, "Alpha")]
    [InlineData("///", "Alpha")]
    public void NeverEmptyFallsBackToTimeAndMatchId(string? format, string team1)
    {
        Assert.Equal("2026-09-24_20-57-56_9", Build(format, team1: team1));
    }

    [Fact]
    public void LongNamesAreCapped()
    {
        string name = Build(DefaultFormat, team1: new string('A', 300), team2: new string('B', 300));
        Assert.True(name.Length <= DemoFileName.MaxLength);
        AssertSafe(name);
    }

    [Fact]
    public void FallbackSearchFindsTheSanitisedName()
    {
        // StopDemoRecording searches with DemoFileName.MapToken(Server.MapName); it has to match
        // the name StartDemoRecording wrote.
        string map = "workshop/3070284539/de_mirage";
        string file = $"/srv/game/csgo/MatchZy/{Build(DefaultFormat, map: map, team1: "SadBoy ♥")}.dem";
        var files = new[] { new DemoFileCandidate(file, Now.ToUniversalTime()) };
        string? found = DemoFileLocator.FindDemoForMatch(files, "other.dem", 9, DemoFileName.MapToken(map), null);
        Assert.Equal(file, found);
    }
}

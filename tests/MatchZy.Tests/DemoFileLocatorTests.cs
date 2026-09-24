using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class DemoFileLocatorTests
{
    private const string Csgo = "/srv/game/csgo";
    private static readonly DateTime Start = new(2026, 9, 16, 4, 48, 39, DateTimeKind.Utc);

    [Theory]
    [InlineData("MatchZy/", "MatchZy/")]
    [InlineData("MatchZy", "MatchZy/")]
    [InlineData("/demos/", "demos/")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeDemoPath(string? input, string expected)
    {
        Assert.Equal(expected, DemoFileLocator.NormalizeDemoPath(input));
    }

    [Theory]
    [InlineData(true, false, true)]  // QA match 62: booted on the map without SourceTV
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void MapReloadOnlyWhenDemosWantedWithoutSourceTv(bool enabled, bool active, bool expected)
    {
        Assert.Equal(expected, DemoFileLocator.RequiresMapReloadForSourceTv(enabled, active));
    }

    [Fact]
    public void CandidateDirectoriesAreDistinctAndMostSpecificFirst()
    {
        var dirs = DemoFileLocator.CandidateDirectories(Csgo, $"{Csgo}/MatchZy/x.dem", "MatchZy/");
        Assert.Equal(new[] { $"{Csgo}/MatchZy", $"{Csgo}/addons/metamod/MatchZy", Csgo, $"{Csgo}/addons/metamod" }, dirs);
    }

    // Issue #35: builds before 1.4.35 passed tv_record a relative path, and Metamod's search
    // path put the demo in csgo/addons/metamod/<demo path>.
    [Fact]
    public void CandidateDirectoriesIncludeMetamodFolders()
    {
        var dirs = DemoFileLocator.CandidateDirectories(Csgo, $"{Csgo}/demos/x.dem", "demos");
        Assert.Contains($"{Csgo}/addons/metamod/demos", dirs);
        Assert.Contains($"{Csgo}/addons/metamod", dirs);
        Assert.True(dirs.ToList().IndexOf($"{Csgo}/demos") < dirs.ToList().IndexOf($"{Csgo}/addons/metamod/demos"));
    }

    [Fact]
    public void CandidateDirectoriesWithEmptyDemoPathHaveNoDuplicates()
    {
        var dirs = DemoFileLocator.CandidateDirectories(Csgo, $"{Csgo}/x.dem", "");
        Assert.Equal(new[] { Csgo, $"{Csgo}/addons/metamod" }, dirs);
    }

    [Fact]
    public void DemoRecordedUnderMetamodIsFound()
    {
        var dirs = DemoFileLocator.CandidateDirectories(Csgo, $"{Csgo}/MatchZy/2026-09-24_20-57-56_9_0.dem", "MatchZy/");
        string recorded = $"{Csgo}/addons/metamod/MatchZy/2026-09-24_20-57-56_9_0.dem";
        var logs = new List<string>();
        string? found = DemoFileLocator.Resolve(
            $"{Csgo}/MatchZy/2026-09-24_20-57-56_9_0.dem", dirs, 9, null, null,
            path => path == recorded,
            dir => dir == $"{Csgo}/addons/metamod/MatchZy"
                ? new[] { new DemoFileCandidate(recorded, Start) }
                : Array.Empty<DemoFileCandidate>(),
            logs.Add);
        Assert.Equal(recorded, found);
    }

    [Theory]
    [InlineData("/home/container/game", "MatchZy/", "a.dem", "/home/container/game/csgo/MatchZy/a.dem")]
    [InlineData("/home/container/game/", "MatchZy", "a.dem", "/home/container/game/csgo/MatchZy/a.dem")]
    [InlineData("/srv/game", "", "a.dem", "/srv/game/csgo/a.dem")]
    [InlineData("/srv/game", "/demos/sub/", "a.dem", "/srv/game/csgo/demos/sub/a.dem")]
    [InlineData("C:\\cs2\\game", "MatchZy\\", "a.dem", "C:/cs2/game/csgo/MatchZy/a.dem")]
    public void TvRecordPathIsAbsoluteWithForwardSlashes(string game, string demoPath, string file, string expected)
    {
        Assert.Equal(expected, DemoFileLocator.TvRecordPath(game, demoPath, file));
    }

    [Theory]
    [InlineData("/home/container/game/csgo/MatchZy/a.dem", "/home/container/game/csgo/MatchZy/a.dem")]
    [InlineData("C:/Program Files (x86)/Steam/game/csgo/MatchZy/a.dem", "\"C:/Program Files (x86)/Steam/game/csgo/MatchZy/a.dem\"")]
    public void TvRecordArgumentQuotesOnlyPathsWithSpaces(string path, string expected)
    {
        Assert.Equal(expected, DemoFileLocator.TvRecordArgument(path));
    }

    [Fact]
    public void ExactFileNameWins()
    {
        var files = new[]
        {
            new DemoFileCandidate($"{Csgo}/MatchZy/2026-09-16_04-48-40_62_de_dust2_A_vs_B.dem", Start.AddMinutes(9)),
            new DemoFileCandidate($"{Csgo}/MatchZy/2026-09-16_04-48-39_62_de_dust2_A_vs_B.dem", Start.AddMinutes(5)),
        };
        Assert.Equal(files[1].Path, DemoFileLocator.FindDemoForMatch(files, "2026-09-16_04-48-39_62_de_dust2_A_vs_B.dem", 62, "de_dust2", Start));
    }

    [Fact]
    public void FallsBackToNewestDemoWithMatchIdAndMap()
    {
        var files = new[]
        {
            new DemoFileCandidate($"{Csgo}/MatchZy/2026-09-16_04-49-01_62_de_dust2_A_vs_B.dem", Start.AddMinutes(8)),
            new DemoFileCandidate($"{Csgo}/MatchZy/2026-09-16_04-49-02_62_de_dust2_A_vs_B.dem", Start.AddMinutes(10)),
            new DemoFileCandidate($"{Csgo}/MatchZy/2026-09-16_04-49-02_62_de_train_A_vs_B.dem", Start.AddMinutes(20)),
            new DemoFileCandidate($"{Csgo}/MatchZy/2026-09-16_04-49-02_162_de_dust2_A_vs_B.dem", Start.AddMinutes(30)),
            new DemoFileCandidate($"{Csgo}/MatchZy/2026-09-16_04-49-02_621_de_dust2_A_vs_B.dem", Start.AddMinutes(30)),
            new DemoFileCandidate($"{Csgo}/MatchZy/62_de_dust2.txt", Start.AddMinutes(30)),
        };
        Assert.Equal(files[1].Path, DemoFileLocator.FindDemoForMatch(files, "missing.dem", 62, "de_dust2", Start));
    }

    [Fact]
    public void MatchIdAtStartOfNameCounts()
    {
        var files = new[] { new DemoFileCandidate($"{Csgo}/62_de_dust2.dem", Start.AddMinutes(1)) };
        Assert.Equal(files[0].Path, DemoFileLocator.FindDemoForMatch(files, "missing.dem", 62, "de_dust2", null));
    }

    [Fact]
    public void IgnoresDemosOlderThanTheRecording()
    {
        var files = new[] { new DemoFileCandidate($"{Csgo}/MatchZy/old_62_de_dust2.dem", Start.AddHours(-3)) };
        Assert.Null(DemoFileLocator.FindDemoForMatch(files, "missing.dem", 62, "de_dust2", Start));
    }

    [Fact]
    public void ResolveReturnsExpectedPathWhenItExists()
    {
        var logs = new List<string>();
        string expected = $"{Csgo}/MatchZy/a_62_de_dust2.dem";
        string? result = DemoFileLocator.Resolve(expected, new[] { $"{Csgo}/MatchZy" }, 62, "de_dust2", Start,
            p => p == expected, _ => throw new InvalidOperationException("should not list"), logs.Add);
        Assert.Equal(expected, result);
        Assert.Single(logs);
    }

    [Fact]
    public void ResolveSearchesDirectoriesInOrderAndLogsEachOne()
    {
        var logs = new List<string>();
        string expected = $"{Csgo}/MatchZy/2026-09-16_04-48-39_62_de_dust2_A_vs_B.dem";
        var byDir = new Dictionary<string, DemoFileCandidate[]>
        {
            [$"{Csgo}/MatchZy"] = Array.Empty<DemoFileCandidate>(),
            [Csgo] = new[] { new DemoFileCandidate($"{Csgo}/2026-09-16_04-48-41_62_de_dust2_A_vs_B.dem", Start.AddMinutes(6)) },
        };

        string? result = DemoFileLocator.Resolve(expected, new[] { $"{Csgo}/MatchZy", "/broken", Csgo }, 62, "de_dust2", Start,
            _ => false,
            d => byDir.TryGetValue(d, out var f) ? f : throw new DirectoryNotFoundException(d),
            logs.Add);

        Assert.Equal($"{Csgo}/2026-09-16_04-48-41_62_de_dust2_A_vs_B.dem", result);
        Assert.Equal(4, logs.Count);
        Assert.Contains(logs, l => l.Contains("/broken"));
    }

    [Fact]
    public void ResolveReturnsNullWhenNothingFound()
    {
        string? result = DemoFileLocator.Resolve($"{Csgo}/MatchZy/x_62_de_dust2.dem", new[] { Csgo }, 62, "de_dust2", Start,
            _ => false, _ => Array.Empty<DemoFileCandidate>(), _ => { });
        Assert.Null(result);
    }
}

using AutoTournamentCS2;
using Xunit;

namespace AutoTournamentCS2.Tests;

public class DemoFileLocatorTests
{
    private const string Csgo = "/srv/game/csgo";
    private static readonly DateTime Start = new(2026, 9, 16, 4, 48, 39, DateTimeKind.Utc);

    [Theory]
    [InlineData("AutoTournamentCS2/", "AutoTournamentCS2/")]
    [InlineData("AutoTournamentCS2", "AutoTournamentCS2/")]
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
        var dirs = DemoFileLocator.CandidateDirectories(Csgo, $"{Csgo}/AutoTournamentCS2/x.dem", "AutoTournamentCS2/");
        Assert.Equal(new[] { $"{Csgo}/AutoTournamentCS2", Csgo }, dirs);
    }

    [Fact]
    public void ExactFileNameWins()
    {
        var files = new[]
        {
            new DemoFileCandidate($"{Csgo}/AutoTournamentCS2/2026-09-16_04-48-40_62_de_dust2_A_vs_B.dem", Start.AddMinutes(9)),
            new DemoFileCandidate($"{Csgo}/AutoTournamentCS2/2026-09-16_04-48-39_62_de_dust2_A_vs_B.dem", Start.AddMinutes(5)),
        };
        Assert.Equal(files[1].Path, DemoFileLocator.FindDemoForMatch(files, "2026-09-16_04-48-39_62_de_dust2_A_vs_B.dem", 62, "de_dust2", Start));
    }

    [Fact]
    public void FallsBackToNewestDemoWithMatchIdAndMap()
    {
        var files = new[]
        {
            new DemoFileCandidate($"{Csgo}/AutoTournamentCS2/2026-09-16_04-49-01_62_de_dust2_A_vs_B.dem", Start.AddMinutes(8)),
            new DemoFileCandidate($"{Csgo}/AutoTournamentCS2/2026-09-16_04-49-02_62_de_dust2_A_vs_B.dem", Start.AddMinutes(10)),
            new DemoFileCandidate($"{Csgo}/AutoTournamentCS2/2026-09-16_04-49-02_62_de_train_A_vs_B.dem", Start.AddMinutes(20)),
            new DemoFileCandidate($"{Csgo}/AutoTournamentCS2/2026-09-16_04-49-02_162_de_dust2_A_vs_B.dem", Start.AddMinutes(30)),
            new DemoFileCandidate($"{Csgo}/AutoTournamentCS2/2026-09-16_04-49-02_621_de_dust2_A_vs_B.dem", Start.AddMinutes(30)),
            new DemoFileCandidate($"{Csgo}/AutoTournamentCS2/62_de_dust2.txt", Start.AddMinutes(30)),
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
        var files = new[] { new DemoFileCandidate($"{Csgo}/AutoTournamentCS2/old_62_de_dust2.dem", Start.AddHours(-3)) };
        Assert.Null(DemoFileLocator.FindDemoForMatch(files, "missing.dem", 62, "de_dust2", Start));
    }

    [Fact]
    public void ResolveReturnsExpectedPathWhenItExists()
    {
        var logs = new List<string>();
        string expected = $"{Csgo}/AutoTournamentCS2/a_62_de_dust2.dem";
        string? result = DemoFileLocator.Resolve(expected, new[] { $"{Csgo}/AutoTournamentCS2" }, 62, "de_dust2", Start,
            p => p == expected, _ => throw new InvalidOperationException("should not list"), logs.Add);
        Assert.Equal(expected, result);
        Assert.Single(logs);
    }

    [Fact]
    public void ResolveSearchesDirectoriesInOrderAndLogsEachOne()
    {
        var logs = new List<string>();
        string expected = $"{Csgo}/AutoTournamentCS2/2026-09-16_04-48-39_62_de_dust2_A_vs_B.dem";
        var byDir = new Dictionary<string, DemoFileCandidate[]>
        {
            [$"{Csgo}/AutoTournamentCS2"] = Array.Empty<DemoFileCandidate>(),
            [Csgo] = new[] { new DemoFileCandidate($"{Csgo}/2026-09-16_04-48-41_62_de_dust2_A_vs_B.dem", Start.AddMinutes(6)) },
        };

        string? result = DemoFileLocator.Resolve(expected, new[] { $"{Csgo}/AutoTournamentCS2", "/broken", Csgo }, 62, "de_dust2", Start,
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
        string? result = DemoFileLocator.Resolve($"{Csgo}/AutoTournamentCS2/x_62_de_dust2.dem", new[] { Csgo }, 62, "de_dust2", Start,
            _ => false, _ => Array.Empty<DemoFileCandidate>(), _ => { });
        Assert.Null(result);
    }
}

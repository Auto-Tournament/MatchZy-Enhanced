using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class MapTargetLogicTests
{
    private static readonly HashSet<string> Installed = new() { "de_dust2", "de_mirage", "cs_office" };
    private static bool IsInstalled(string name) => Installed.Contains(name);
    private static string? NoneKnown(string _) => null;

    [Theory]
    [InlineData("3070212801", "3070212801")]
    [InlineData("workshop/3070212801/de_cache_classic", "3070212801")]
    [InlineData("workshop/3070212801", "3070212801")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3070212801", "3070212801")]
    [InlineData("de_dust2", null)]
    [InlineData("", null)]
    public void ParsesWorkshopIds(string input, string? expected)
    {
        Assert.Equal(expected, MapTargetLogic.ParseWorkshopId(input));
    }

    [Theory]
    [InlineData("de_dust2", MapChangeKind.Standard, "de_dust2")]
    [InlineData("mirage", MapChangeKind.Standard, "de_mirage")]              // short name still works
    [InlineData("3070212801", MapChangeKind.WorkshopId, "3070212801")]
    [InlineData("workshop/3070212801/de_x", MapChangeKind.WorkshopId, "3070212801")]
    [InlineData("de_cache_classic", MapChangeKind.WorkshopName, "de_cache_classic")] // was "Invalid map name!"
    [InlineData("awp_lego", MapChangeKind.WorkshopName, "awp_lego")]
    [InlineData("de_dust2\"; quit", MapChangeKind.Invalid, "")]            // never spliced into a command
    [InlineData("   ", MapChangeKind.Invalid, "")]
    public void ResolvesMapChange(string input, MapChangeKind kind, string arg)
    {
        var result = MapTargetLogic.Resolve(input, IsInstalled, NoneKnown);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(arg, result.Argument);
    }

    [Fact]
    public void ReloadOfWorkshopMapReusesItsId()
    {
        // .rmap passes Server.MapName, which is the workshop map's name, not its id.
        var registry = new WorkshopMapRegistry();
        registry.MarkPending("3070212801");
        registry.OnMapStarted("de_cache_classic");

        var result = MapTargetLogic.Resolve("de_cache_classic", IsInstalled, registry.IdFor);
        Assert.Equal(MapChangeKind.WorkshopId, result.Kind);
        Assert.Equal("3070212801", result.Argument);
    }

    [Fact]
    public void InstalledLoadClearsWorkshopLink()
    {
        var registry = new WorkshopMapRegistry();
        registry.MarkPending("123");
        registry.OnMapStarted("de_dust2");
        registry.OnMapStarted("de_dust2"); // later plain changelevel de_dust2

        Assert.Null(registry.IdFor("de_dust2"));
        Assert.Equal(MapChangeKind.Standard, MapTargetLogic.Resolve("de_dust2", IsInstalled, registry.IdFor).Kind);
    }

    [Theory]
    [InlineData("de_dust2", "de_dust2", true)]
    [InlineData("3070212801", "de_cache_classic", true)]   // match map list uses the workshop id
    [InlineData("999", "de_cache_classic", false)]
    [InlineData("de_mirage", "de_dust2", false)]
    public void RecognisesCurrentMap(string target, string current, bool expected)
    {
        var registry = new WorkshopMapRegistry();
        registry.MarkPending("3070212801");
        registry.OnMapStarted("de_cache_classic");
        Assert.Equal(expected, MapTargetLogic.IsCurrentMap(target, current, registry.IdFor));
    }
}

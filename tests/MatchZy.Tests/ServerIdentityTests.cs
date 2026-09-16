using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class ServerIdentityTests
{
    // The real start arguments of the three servers that hit the shared-config bug in production.
    private static string[] ServerArgs(int port, int tvPort, string bindIp = "0.0.0.0") => new[]
    {
        "/home/cs2servermanager/server-1/game/bin/linuxsteamrt64/cs2",
        "-dedicated", "-ip", bindIp, "+map", "de_dust2",
        "-port", port.ToString(), "+tv_port", tvPort.ToString(),
        "+maxplayers", "15", "-usercon",
    };

    [Fact]
    public void ThreeServersOnOneBoxGetThreeDistinctScopes()
    {
        string s1 = ServerIdentity.Resolve(null, ServerArgs(27015, 27020), null, null, "cs2");
        string s2 = ServerIdentity.Resolve(null, ServerArgs(27025, 27030), null, null, "cs2");
        string s3 = ServerIdentity.Resolve(null, ServerArgs(27035, 27040), null, null, "cs2");

        Assert.Equal("cs2:27015", s1);
        Assert.Equal("cs2:27025", s2);
        Assert.Equal("cs2:27035", s3);
        Assert.Equal(3, new[] { s1, s2, s3 }.Distinct().Count());
    }

    [Fact]
    public void ScopeIsStableAcrossRestarts()
    {
        string first = ServerIdentity.Resolve(null, ServerArgs(27025, 27030), null, null, "cs2");
        string second = ServerIdentity.Resolve(null, ServerArgs(27025, 27030), null, null, "cs2");

        Assert.Equal(first, second);
    }

    [Fact]
    public void TvPortIsNotMistakenForTheGamePort()
    {
        Assert.Equal(27025, ServerIdentity.ParseGamePort(ServerArgs(27025, 27030)));
    }

    [Theory]
    [InlineData("-port")]
    [InlineData("+port")]
    [InlineData("-hostport")]
    [InlineData("+hostport")]
    public void GamePortIsReadFromAnyOfTheUsualFlags(string flag)
    {
        Assert.Equal(27045, ServerIdentity.ParseGamePort(new[] { "cs2", flag, "27045" }));
    }

    [Fact]
    public void NoArgumentsMeansNoParsedPort()
    {
        Assert.Null(ServerIdentity.ParseGamePort(null));
        Assert.Null(ServerIdentity.ParseGamePort(Array.Empty<string>()));
        // A trailing flag with no value after it must not throw.
        Assert.Null(ServerIdentity.ParseGamePort(new[] { "cs2", "-port" }));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    public void AnInvalidPortValueIsIgnored(string value)
    {
        Assert.Null(ServerIdentity.ParseGamePort(new[] { "cs2", "-port", value }));
    }

    [Fact]
    public void ConvarsAreUsedWhenTheCommandLineHasNoPort()
    {
        Assert.Equal("cs2:27055", ServerIdentity.Resolve(null, new[] { "cs2", "-dedicated" }, "0.0.0.0", 27055, "cs2"));
    }

    [Fact]
    public void TheCommandLineWinsOverTheConvars()
    {
        Assert.Equal("cs2:27025", ServerIdentity.Resolve(null, ServerArgs(27025, 27030), "0.0.0.0", 27015, "cs2"));
    }

    [Fact]
    public void WithoutAnyPortTheDefaultGamePortIsUsed()
    {
        Assert.Equal("cs2:27015", ServerIdentity.Resolve(null, null, null, null, "cs2"));
    }

    [Theory]
    // A real bind address identifies the server on its own, so it is preferred over the box name.
    [InlineData("192.168.50.196", "192.168.50.196:27015")]
    [InlineData("10.0.0.5", "10.0.0.5:27015")]
    // These identify no interface, so the box name is used instead.
    [InlineData("0.0.0.0", "cs2:27015")]
    [InlineData("::", "cs2:27015")]
    [InlineData("127.0.0.1", "cs2:27015")]
    [InlineData("localhost", "cs2:27015")]
    [InlineData("", "cs2:27015")]
    [InlineData(null, "cs2:27015")]
    public void AnUnusableBindAddressFallsBackToTheMachineName(string? bindIp, string expected)
    {
        Assert.Equal(expected, ServerIdentity.Derive(bindIp, 27015, "cs2"));
    }

    [Fact]
    public void WithoutABindAddressOrMachineNameTheHostIsMarkedUnknown()
    {
        Assert.Equal("unknown-host:27015", ServerIdentity.Derive("0.0.0.0", 27015, null));
    }

    [Fact]
    public void TheConvarOverrideWinsOverEverythingDerived()
    {
        Assert.Equal("tournament-eu-3", ServerIdentity.Resolve("tournament-eu-3", ServerArgs(27025, 27030), "10.0.0.5", 27015, "cs2"));
    }

    [Fact]
    public void TheCommandLineOverrideIsUsedWhenTheConvarIsNotSetYet()
    {
        // config.cfg goes through the engine command buffer and may not have run when the scope
        // is first needed, so the start-argument form has to work on its own.
        string[] args = new[] { "cs2", "-port", "27025", "+matchzy_config_scope", "tournament-eu-3" };

        Assert.Equal("tournament-eu-3", ServerIdentity.Resolve(null, args, null, null, "cs2"));
        Assert.Equal("tournament-eu-3", ServerIdentity.Resolve("", args, null, null, "cs2"));
    }

    [Fact]
    public void TheConvarOverrideWinsOverTheCommandLineOverride()
    {
        string[] args = new[] { "cs2", "+matchzy_config_scope", "from-args" };

        Assert.Equal("from-convar", ServerIdentity.Resolve("from-convar", args, null, null, "cs2"));
    }

    [Theory]
    [InlineData("  CS2-Box-01:27015  ", "cs2-box-01:27015")]
    [InlineData("Tournament EU 3", "tournament-eu-3")]
    [InlineData("CS2:27015", "cs2:27015")]
    public void ScopesAreNormalisedSoOneServerAlwaysProducesTheSameRow(string raw, string expected)
    {
        Assert.Equal(expected, ServerIdentity.Sanitize(raw));
    }

    [Fact]
    public void AnOverlongScopeIsTruncatedInsideTheColumnWidth()
    {
        string scope = ServerIdentity.Sanitize(new string('a', 500));

        Assert.Equal(ServerIdentity.MaxScopeLength, scope.Length);
        Assert.True(scope.Length < 190, "scope must fit the VARCHAR(190) column without the database truncating it");
    }

    [Fact]
    public void ABlankScopeNeverCollidesWithTheLegacyScope()
    {
        Assert.NotEqual(ServerIdentity.LegacyScope, ServerIdentity.Sanitize("   "));
        Assert.NotEqual(ServerIdentity.LegacyScope, ServerIdentity.Resolve(null, null, null, null, null));
    }

    [Fact]
    public void ChangingTheGamePortChangesTheScope()
    {
        // Documented failure mode: a port change orphans the scoped rows and the server falls
        // back to the legacy row until something writes again.
        Assert.NotEqual(
            ServerIdentity.Resolve(null, ServerArgs(27015, 27020), null, null, "cs2"),
            ServerIdentity.Resolve(null, ServerArgs(27016, 27020), null, null, "cs2"));
    }

    [Fact]
    public void TwoBoxesOnTheSamePortDoNotCollide()
    {
        Assert.NotEqual(
            ServerIdentity.Resolve(null, ServerArgs(27015, 27020), null, null, "cs2-eu"),
            ServerIdentity.Resolve(null, ServerArgs(27015, 27020), null, null, "cs2-us"));
    }
}

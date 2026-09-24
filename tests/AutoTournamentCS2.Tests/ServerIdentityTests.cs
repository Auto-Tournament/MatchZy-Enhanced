using System.Text;
using AutoTournamentCS2;
using Xunit;

namespace AutoTournamentCS2.Tests;

public class ServerIdentityTests
{
    // Exact /proc/<pid>/cmdline bytes (base64) of the three cs2 game processes on the production
    // box that hit the collision in 1.4.26, started by csm 1.7.8 through cs2.sh. Captured with
    // `base64 -w0 /proc/<pid>/cmdline`, with the scope argument changed to its 2.0.0 name.
    private const string ProcCmdlineServer1 =
        "L2hvbWUvY3Myc2VydmVybWFuYWdlci9zZXJ2ZXItMS9nYW1lL2Jpbi9saW51eHN0ZWFtcnQ2NC9jczIALWRlZGljYXRlZAAtaXAAMC4wLjAuMAArbWFwAGRlX2R1c3QyAC1wb3J0ADI3MDE1ACt0dl9wb3J0ADI3MDIwACttYXhwbGF5ZXJzADE1AC11c2VyY29uACthdF9jb25maWdfc2NvcGUAY3MyLXNlcnZlci0xAA==";
    private const string ProcCmdlineServer2 =
        "L2hvbWUvY3Myc2VydmVybWFuYWdlci9zZXJ2ZXItMi9nYW1lL2Jpbi9saW51eHN0ZWFtcnQ2NC9jczIALWRlZGljYXRlZAAtaXAAMC4wLjAuMAArbWFwAGRlX2R1c3QyAC1wb3J0ADI3MDI1ACt0dl9wb3J0ADI3MDMwACttYXhwbGF5ZXJzADE1AC11c2VyY29uACthdF9jb25maWdfc2NvcGUAY3MyLXNlcnZlci0yAA==";
    private const string ProcCmdlineServer3 =
        "L2hvbWUvY3Myc2VydmVybWFuYWdlci9zZXJ2ZXItMy9nYW1lL2Jpbi9saW51eHN0ZWFtcnQ2NC9jczIALWRlZGljYXRlZAAtaXAAMC4wLjAuMAArbWFwAGRlX2R1c3QyAC1wb3J0ADI3MDM1ACt0dl9wb3J0ADI3MDQwACttYXhwbGF5ZXJzADE1AC11c2VyY29uACthdF9jb25maWdfc2NvcGUAY3MyLXNlcnZlci0zAA==";

    private static string[] Argv(string base64) => ServerIdentity.ParseProcCmdline(Convert.FromBase64String(base64));

    // The same servers without the explicit scope argument.
    private static string[] ServerArgs(int port, int tvPort, string bindIp = "0.0.0.0") => new[]
    {
        "/home/cs2servermanager/server-1/game/bin/linuxsteamrt64/cs2",
        "-dedicated", "-ip", bindIp, "+map", "de_dust2",
        "-port", port.ToString(), "+tv_port", tvPort.ToString(),
        "+maxplayers", "15", "-usercon",
    };

    private static ScopeResolution Resolve(
        string[]? args = null,
        string? convarScope = null,
        string? convarIp = null,
        int? hostport = null,
        bool activated = false,
        string? machine = "cs2",
        string? installPath = null,
        int pid = 4242) =>
        ServerIdentity.Resolve(new ScopeInputs
        {
            CommandLineArgs = args,
            ConvarScope = convarScope,
            ConvarBindIp = convarIp,
            HostportConvar = hostport,
            ServerActivated = activated,
            MachineName = machine,
            InstallPath = installPath,
            ProcessId = pid,
        });

    // ---- /proc/self/cmdline parsing ------------------------------------------------------------

    [Fact]
    public void RealProcCmdlineIsSplitIntoTheGameArgv()
    {
        string[] argv = Argv(ProcCmdlineServer2);

        Assert.Equal(new[]
        {
            "/home/cs2servermanager/server-2/game/bin/linuxsteamrt64/cs2",
            "-dedicated", "-ip", "0.0.0.0", "+map", "de_dust2",
            "-port", "27025", "+tv_port", "27030", "+maxplayers", "15", "-usercon",
            "+at_config_scope", "cs2-server-2",
        }, argv);
    }

    [Fact]
    public void RealProcCmdlineYieldsTheStartArgumentScopeAndPort()
    {
        Assert.Equal("cs2-server-1", ServerIdentity.ParseScopeOverride(Argv(ProcCmdlineServer1)));
        Assert.Equal("cs2-server-2", ServerIdentity.ParseScopeOverride(Argv(ProcCmdlineServer2)));
        Assert.Equal("cs2-server-3", ServerIdentity.ParseScopeOverride(Argv(ProcCmdlineServer3)));

        Assert.Equal(27015, ServerIdentity.ParseGamePort(Argv(ProcCmdlineServer1)));
        Assert.Equal(27025, ServerIdentity.ParseGamePort(Argv(ProcCmdlineServer2)));
        Assert.Equal(27035, ServerIdentity.ParseGamePort(Argv(ProcCmdlineServer3)));
    }

    [Fact]
    public void TheThreeProductionServersResolveToThreeScopesFromStartArguments()
    {
        // Exactly the plugin's situation during Load: convar not set yet, hostport not trusted.
        var scopes = new[] { ProcCmdlineServer1, ProcCmdlineServer2, ProcCmdlineServer3 }
            .Select(b => Resolve(Argv(b), hostport: 27015))
            .ToArray();

        Assert.Equal(new[] { "cs2-server-1", "cs2-server-2", "cs2-server-3" }, scopes.Select(s => s.Scope));
        Assert.All(scopes, s => Assert.Equal(ScopeSource.StartArgument, s.Source));
        Assert.All(scopes, s => Assert.True(s.IsFinal));
        Assert.Equal("from start argument", scopes[1].Description);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0 })]
    public void EmptyProcCmdlineGivesNoArgs(byte[] raw)
    {
        Assert.Empty(ServerIdentity.ParseProcCmdline(raw));
        Assert.Empty(ServerIdentity.ParseProcCmdline(null));
    }

    [Fact]
    public void ProcCmdlineWithoutTrailingNulIsStillParsed()
    {
        byte[] raw = Encoding.UTF8.GetBytes("cs2\0-port\027025");
        Assert.Equal(27025, ServerIdentity.ParseGamePort(ServerIdentity.ParseProcCmdline(raw)));
    }

    [Fact]
    public void ReadingTheCommandLineNeverThrows()
    {
        var (args, source) = ServerIdentity.ReadProcessCommandLine();
        Assert.NotNull(args);
        Assert.False(string.IsNullOrEmpty(source));
    }

    // ---- flag forms ----------------------------------------------------------------------------

    [Theory]
    [InlineData("+at_config_scope", "eu-3")]
    [InlineData("-at_config_scope", "eu-3")]
    [InlineData("+AT_CONFIG_SCOPE", "eu-3")]
    public void ScopeIsReadFromNameValueForm(string flag, string value)
    {
        Assert.Equal("eu-3", ServerIdentity.ParseScopeOverride(new[] { "cs2", flag, value }));
    }

    [Theory]
    [InlineData("+at_config_scope=eu-3")]
    [InlineData("-at_config_scope=eu-3")]
    public void ScopeIsReadFromNameEqualsValueForm(string arg)
    {
        Assert.Equal("eu-3", ServerIdentity.ParseScopeOverride(new[] { "cs2", "-dedicated", arg }));
    }

    [Theory]
    [InlineData("-port")]
    [InlineData("+port")]
    [InlineData("-hostport")]
    [InlineData("+hostport")]
    public void GamePortIsReadFromAnyOfTheUsualFlags(string flag)
    {
        Assert.Equal(27045, ServerIdentity.ParseGamePort(new[] { "cs2", flag, "27045" }));
        Assert.Equal(27045, ServerIdentity.ParseGamePort(new[] { "cs2", flag + "=27045" }));
    }

    [Fact]
    public void TvPortIsNotMistakenForTheGamePort()
    {
        Assert.Equal(27025, ServerIdentity.ParseGamePort(ServerArgs(27025, 27030)));
        Assert.Null(ServerIdentity.ParseGamePort(new[] { "cs2", "+tv_port", "27030" }));
    }

    [Fact]
    public void MissingArgumentsParseAsNothing()
    {
        Assert.Null(ServerIdentity.ParseGamePort(null));
        Assert.Null(ServerIdentity.ParseGamePort(Array.Empty<string>()));
        Assert.Null(ServerIdentity.ParseScopeOverride(null));
        Assert.Null(ServerIdentity.ParseScopeOverride(new[] { "cs2", "-dedicated", "-port", "27015" }));
        // A trailing flag with no value after it must not throw.
        Assert.Null(ServerIdentity.ParseGamePort(new[] { "cs2", "-port" }));
        Assert.Null(ServerIdentity.ParseScopeOverride(new[] { "cs2", "+at_config_scope" }));
        Assert.Null(ServerIdentity.ParseScopeOverride(new[] { "cs2", "+at_config_scope=" }));
        Assert.Null(ServerIdentity.ParseScopeOverride(new[] { "cs2", "+at_config_scope", "" }));
    }

    [Fact]
    public void AFlagIsNeverTakenAsAValue()
    {
        Assert.Null(ServerIdentity.ParseScopeOverride(new[] { "cs2", "+at_config_scope", "+map", "de_dust2" }));
        Assert.Null(ServerIdentity.ParseGamePort(new[] { "cs2", "-port", "-usercon" }));
    }

    [Fact]
    public void TheExecutableIsNeverParsedAsAFlag()
    {
        Assert.Null(ServerIdentity.ParseGamePort(new[] { "-port", "27015" }));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-1")]
    [InlineData("+27015")]
    public void AnInvalidPortValueIsIgnored(string value)
    {
        Assert.Null(ServerIdentity.ParseGamePort(new[] { "cs2", "-port", value }));
    }

    // ---- resolution order ----------------------------------------------------------------------

    [Fact]
    public void TheStartArgumentWinsOverTheConvar()
    {
        var r = Resolve(new[] { "cs2", "+at_config_scope", "from-args" }, convarScope: "from-convar");
        Assert.Equal("from-args", r.Scope);
        Assert.Equal(ScopeSource.StartArgument, r.Source);
    }

    [Fact]
    public void TheConvarWinsOverEverythingDerived()
    {
        var r = Resolve(ServerArgs(27025, 27030), convarScope: "tournament-eu-3", hostport: 27015, activated: true);
        Assert.Equal("tournament-eu-3", r.Scope);
        Assert.Equal(ScopeSource.Convar, r.Source);
        Assert.True(r.IsFinal);
    }

    [Fact]
    public void ThreeServersWithoutAnExplicitScopeGetTheirCommandLinePort()
    {
        var scopes = new[] { (27015, 27020), (27025, 27030), (27035, 27040) }
            .Select(p => Resolve(ServerArgs(p.Item1, p.Item2), hostport: 27015))
            .ToArray();

        Assert.Equal(new[] { "cs2:27015", "cs2:27025", "cs2:27035" }, scopes.Select(s => s.Scope));
        Assert.All(scopes, s => Assert.Equal(ScopeSource.CommandLinePort, s.Source));
        Assert.All(scopes, s => Assert.True(s.IsFinal));
    }

    [Fact]
    public void TheCommandLinePortWinsOverHostport()
    {
        Assert.Equal("cs2:27025", Resolve(ServerArgs(27025, 27030), hostport: 27015, activated: true).Scope);
    }

    [Fact]
    public void HostportIsUsedOnlyAfterTheServerActivated()
    {
        string[] noPort = { "/srv/cs2-a/game/bin/linuxsteamrt64/cs2", "-dedicated" };

        var before = Resolve(noPort, hostport: 27015, activated: false, installPath: "/srv/cs2-a/game");
        Assert.NotEqual(ScopeSource.HostportConvar, before.Source);
        Assert.False(before.IsFinal);

        var after = Resolve(noPort, hostport: 27055, activated: true, installPath: "/srv/cs2-a/game");
        Assert.Equal("cs2:27055", after.Scope);
        Assert.Equal(ScopeSource.HostportConvar, after.Source);
        Assert.True(after.IsFinal);
    }

    [Fact]
    public void ARealBindAddressIsPreferredOverTheMachineName()
    {
        Assert.Equal("10.0.0.5:27025", Resolve(ServerArgs(27025, 27030, "10.0.0.5")).Scope);
        Assert.Equal("10.0.0.6:27025", Resolve(new[] { "cs2", "-port", "27025" }, convarIp: "10.0.0.6").Scope);
    }

    [Theory]
    [InlineData("192.168.50.196", "192.168.50.196:27015")]
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

    // ---- never collapse to a host-wide key -----------------------------------------------------

    [Fact]
    public void WhatBrokeIn1426NoLongerCollapsesToOneKey()
    {
        // 1.4.26 saw no game args (Environment.GetCommandLineArgs() inside the hosted runtime) and
        // the pre-activation hostport default, and resolved all three servers to cs2:27015.
        var scopes = new[] { "server-1", "server-2", "server-3" }
            .Select(dir => Resolve(
                args: new[] { $"/home/cs2servermanager/{dir}/game/bin/linuxsteamrt64/cs2" },
                hostport: 27015,
                activated: false,
                installPath: $"/home/cs2servermanager/{dir}/game"))
            .ToArray();

        Assert.Equal(3, scopes.Select(s => s.Scope).Distinct().Count());
        Assert.DoesNotContain(scopes, s => s.Scope == "cs2:27015");
        Assert.All(scopes, s => Assert.True(s.IsFallback));
        Assert.All(scopes, s => Assert.Equal(ScopeSource.InstallPathFallback, s.Source));
        Assert.All(scopes, s => Assert.StartsWith("cs2:path-", s.Scope));
        Assert.All(scopes, s => Assert.Contains("FALLBACK", s.Description));
    }

    [Fact]
    public void TheInstallPathFallbackIsStableAcrossRestarts()
    {
        var first = Resolve(new[] { "cs2" }, activated: true, installPath: "/home/cs2servermanager/server-2/game", pid: 1);
        var second = Resolve(new[] { "cs2" }, activated: true, installPath: "/home/cs2servermanager/server-2/game/", pid: 2);

        Assert.Equal(first.Scope, second.Scope);
        Assert.True(first.IsFinal);
    }

    [Fact]
    public void WithoutAnyIdentityTheProcessIdIsUsedRatherThanAHostWideKey()
    {
        var a = Resolve(null, machine: "cs2", installPath: null, pid: 100);
        var b = Resolve(null, machine: "cs2", installPath: null, pid: 101);

        Assert.NotEqual(a.Scope, b.Scope);
        Assert.Equal(ScopeSource.ProcessFallback, a.Source);
        Assert.True(a.IsFallback);
    }

    [Fact]
    public void NoResolutionEverProducesTheLegacyScope()
    {
        Assert.NotEqual(ServerIdentity.LegacyScope, Resolve(null, machine: null).Scope);
        Assert.NotEqual(ServerIdentity.LegacyScope, Resolve(new[] { "cs2", "+at_config_scope", "   " }, machine: null).Scope);
        Assert.NotEqual(ServerIdentity.LegacyScope, ServerIdentity.Sanitize("   "));
    }

    [Fact]
    public void StartArgumentsAloneIdentifyAServerOnlyWithAScopeOrPort()
    {
        Assert.True(ServerIdentity.HasCommandLineIdentity(Argv(ProcCmdlineServer2)));
        Assert.True(ServerIdentity.HasCommandLineIdentity(new[] { "cs2", "-port", "27015" }));
        Assert.True(ServerIdentity.HasCommandLineIdentity(new[] { "cs2", "+at_config_scope=x" }));
        Assert.False(ServerIdentity.HasCommandLineIdentity(new[] { "cs2", "-dedicated", "+tv_port", "27020" }));
        Assert.False(ServerIdentity.HasCommandLineIdentity(null));
    }

    // ---- normalisation -------------------------------------------------------------------------

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
    public void TwoBoxesOnTheSamePortDoNotCollide()
    {
        Assert.NotEqual(
            Resolve(ServerArgs(27015, 27020), machine: "cs2-eu").Scope,
            Resolve(ServerArgs(27015, 27020), machine: "cs2-us").Scope);
    }
}

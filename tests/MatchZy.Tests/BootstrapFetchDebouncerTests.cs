using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class BootstrapFetchDebouncerTests
{
    private const string OldUrl = "http://192.168.50.196:3069/api/servers/s_3/bootstrap";
    private const string NewUrl = "http://192.168.50.196:3069/api/servers/s_1/bootstrap";
    private const string OldToken = "old-token";
    private const string NewToken = "new-token";

    /// <summary>Manual clock and timer queue: nothing fires until the test advances time.</summary>
    private sealed class FakeScheduler : IBootstrapScheduler
    {
        private readonly List<Entry> entries = new();

        public DateTimeOffset Now { get; private set; } = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

        public int Active => entries.Count(e => !e.Cancelled);

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var entry = new Entry(Now + delay, callback);
            entries.Add(entry);
            return entry;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target = Now + by;
            while (true)
            {
                Entry? next = entries
                    .Where(e => !e.Cancelled && e.DueAt <= target)
                    .OrderBy(e => e.DueAt)
                    .FirstOrDefault();
                if (next == null) break;

                entries.Remove(next);
                Now = next.DueAt;
                next.Callback();
            }
            Now = target;
        }

        private sealed class Entry : IDisposable
        {
            public Entry(DateTimeOffset dueAt, Action callback)
            {
                DueAt = dueAt;
                Callback = callback;
            }

            public DateTimeOffset DueAt { get; }
            public Action Callback { get; }
            public bool Cancelled { get; private set; }

            public void Dispose() => Cancelled = true;
        }
    }

    /// <summary>
    /// The plugin's convar state plus its console handlers, reduced to what the debouncer sees.
    /// </summary>
    private sealed class Harness
    {
        public readonly FakeScheduler Scheduler = new();
        public readonly List<(string Url, string Token, string Reason)> Fetches = new();
        public readonly List<string> Logs = new();
        public readonly BootstrapFetchDebouncer Debouncer;

        public string Url = OldUrl;
        public string Token = OldToken;

        public Harness()
        {
            Debouncer = new BootstrapFetchDebouncer(
                Scheduler,
                () => Scheduler.Now,
                () => (Url, Token),
                (url, token, reason) => Fetches.Add((url, token, reason)),
                Logs.Add);
        }

        public void SetUrl(string value)
        {
            string previous = Url;
            Url = value;
            Debouncer.OnValueSet(previous, value);
        }

        public void SetToken(string value)
        {
            string previous = Token;
            Token = value;
            Debouncer.OnValueSet(previous, value);
        }

        public void Advance(double seconds) => Scheduler.Advance(TimeSpan.FromSeconds(seconds));

        /// <summary>Completes the running fetch and applies a payload made of setter calls.</summary>
        public void CompleteAndApply(params Action<Harness>[] payload)
        {
            Debouncer.OnFetchCompleted(applied: true);
            foreach (var command in payload) command(this);
        }
    }

    [Fact]
    public void TokenThenUrlFetchesOnceWithFinalValues()
    {
        var h = new Harness();

        h.SetToken(NewToken);
        h.Advance(0.2);
        h.SetUrl(NewUrl);

        Assert.Empty(h.Fetches);
        h.Advance(1.4);
        Assert.Empty(h.Fetches); // timer restarted by the URL change
        h.Advance(0.2);

        Assert.Equal(new[] { (NewUrl, NewToken, "console") }, h.Fetches);
        h.Advance(30);
        Assert.Single(h.Fetches);
    }

    [Fact]
    public void UrlThenTokenFetchesOnceWithFinalValues()
    {
        var h = new Harness();

        h.SetUrl(NewUrl);
        h.Advance(0.2);
        h.SetToken(NewToken);
        h.Advance(30);

        Assert.Equal(new[] { (NewUrl, NewToken, "console") }, h.Fetches);
    }

    [Fact]
    public void RapidRepeatedSetsFetchOnce()
    {
        var h = new Harness();

        for (int i = 0; i < 20; i++)
        {
            h.SetToken($"token-{i}");
            h.SetUrl($"http://mat:3069/api/servers/s_{i}/bootstrap");
            h.Advance(0.5);
        }

        Assert.Empty(h.Fetches);
        Assert.Equal(1, h.Scheduler.Active);
        h.Advance(30);

        Assert.Equal(new[] { ("http://mat:3069/api/servers/s_19/bootstrap", "token-19", "console") }, h.Fetches);
    }

    [Fact]
    public void NoFetchWithoutBothValues()
    {
        var h = new Harness { Url = "", Token = "" };

        h.SetToken(NewToken);
        h.Advance(30);

        Assert.Empty(h.Fetches);
        Assert.False(h.Debouncer.FetchInProgress);
    }

    [Fact]
    public void PayloadSettingTheSameValuesDoesNotLoop()
    {
        var h = new Harness();
        h.SetToken(NewToken);
        h.SetUrl(NewUrl);
        h.Advance(2);
        Assert.Single(h.Fetches);

        // The payload re-sends the URL and token it was fetched with.
        h.CompleteAndApply(x => x.SetToken(NewToken), x => x.SetUrl(NewUrl));
        h.Advance(60);

        Assert.Single(h.Fetches);
        Assert.False(h.Debouncer.FetchInProgress);
    }

    [Fact]
    public void PayloadRedirectingToOtherValuesIsCapped()
    {
        var h = new Harness();
        h.SetUrl(NewUrl);
        h.Advance(2);

        // Every payload points at a different URL: a redirect loop.
        for (int i = 0; i < 10; i++)
        {
            int hop = i;
            h.CompleteAndApply(x => x.SetUrl($"http://mat:3069/api/servers/hop{hop}/bootstrap"));
            h.Advance(2);
        }

        Assert.Equal(1 + BootstrapFetchDebouncer.MaxChainedFetches, h.Fetches.Count);
        Assert.Contains(h.Logs, l => l.Contains("times in a row"));
    }

    [Fact]
    public void OperatorResendingSameValuesLaterStillRefetches()
    {
        var h = new Harness();
        h.SetToken(NewToken);
        h.SetUrl(NewUrl);
        h.Advance(2);
        h.CompleteAndApply();

        // A second "configure" from the controller well after the payload was applied.
        h.Advance(20);
        h.SetToken(NewToken);
        h.SetUrl(NewUrl);
        h.Advance(2);

        Assert.Equal(2, h.Fetches.Count);
        Assert.Equal((NewUrl, NewToken, "console"), h.Fetches[1]);
    }

    [Fact]
    public void ChangeDuringFetchRefetchesWithNewValuesAfterItCompletes()
    {
        var h = new Harness();
        h.SetUrl(NewUrl);
        h.Advance(2);
        Assert.True(h.Debouncer.FetchInProgress);

        h.SetToken(NewToken);
        h.Advance(2); // timer fires while the first fetch is still running
        Assert.Single(h.Fetches);

        h.Debouncer.OnFetchCompleted(applied: false);
        h.Advance(2);

        Assert.Equal(2, h.Fetches.Count);
        Assert.Equal((NewUrl, NewToken, "console"), h.Fetches[1]);
    }

    [Fact]
    public void StartupFetchIsImmediateAndNotDebounced()
    {
        var h = new Harness();

        Assert.True(h.Debouncer.FetchNow("startup"));
        Assert.Equal(new[] { (OldUrl, OldToken, "startup") }, h.Fetches);
        Assert.False(h.Debouncer.FetchNow("startup")); // already running
    }

    [Fact]
    public void ProductionSequenceFetchesTheNewServersUrl()
    {
        // server-1 after upgrading to 1.4.27: persisted URL still pointed at s_3.
        var h = new Harness { Url = OldUrl, Token = NewToken };

        // MAT: matchzy_server_id "s_1", matchzy_bootstrap_token, matchzy_bootstrap_url (RCON round trips apart).
        h.SetToken(NewToken);
        h.Advance(0.05);
        h.SetUrl(NewUrl);
        h.Advance(5);

        Assert.Equal(new[] { (NewUrl, NewToken, "console") }, h.Fetches);
    }
}

public class BootstrapPayloadCheckTests
{
    [Theory]
    [InlineData("http://192.168.50.196:3069/api/servers/s_1/bootstrap", "s_1")]
    [InlineData("https://mat.example/prefix/api/servers/abc-123/bootstrap/", "abc-123")]
    [InlineData("http://mat/api/servers/s%201/bootstrap", "s 1")]
    [InlineData("http://mat/api/servers/s_1/heartbeat", null)]
    [InlineData("http://mat/bootstrap", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    public void ServerIdFromUrl(string url, string? expected)
    {
        Assert.Equal(expected, BootstrapPayloadCheck.ServerIdFromUrl(url));
    }

    [Fact]
    public void ServerIdFromCommandsTakesTheLastQuotedOrBareValue()
    {
        Assert.Equal("s_3", BootstrapPayloadCheck.ServerIdFromCommands(new[]
        {
            "matchzy_clear_event_queue",
            "matchzy_server_id \"s_2\"",
            "matchzy_server_idx \"nope\"",
            "  matchzy_server_id   s_3 ",
            "matchzy_remote_log_url \"http://mat/api/events?server_id=s_9\"",
        }));
        Assert.Null(BootstrapPayloadCheck.ServerIdFromCommands(new[] { "matchzy_server_id", "matchzy_server_id \"\"" }));
        Assert.Null(BootstrapPayloadCheck.ServerIdFromCommands(null));
    }

    [Fact]
    public void WarnsWhenPayloadIdDiffersFromUrl()
    {
        var warnings = BootstrapPayloadCheck.ServerIdWarnings(
            "http://mat/api/servers/s_1/bootstrap", new[] { "matchzy_server_id \"s_3\"" }, currentServerId: "s_3");

        Assert.Single(warnings);
        Assert.Contains("fetched from the bootstrap URL of server \"s_1\"", warnings[0]);
    }

    [Fact]
    public void WarnsWhenPayloadChangesTheIdTheControllerJustSet()
    {
        // The production case: MAT set s_1, the stale URL returned s_3's payload.
        var warnings = BootstrapPayloadCheck.ServerIdWarnings(
            "http://mat/api/servers/s_3/bootstrap", new[] { "matchzy_server_id \"s_3\"" }, currentServerId: "s_1");

        Assert.Single(warnings);
        Assert.Contains("from \"s_1\" to \"s_3\"", warnings[0]);
    }

    [Fact]
    public void NoWarningWhenEverythingAgrees()
    {
        Assert.Empty(BootstrapPayloadCheck.ServerIdWarnings(
            "http://mat/api/servers/s_1/bootstrap", new[] { "matchzy_server_id \"s_1\"" }, "s_1"));
        Assert.Empty(BootstrapPayloadCheck.ServerIdWarnings(
            "http://mat/api/servers/s_1/bootstrap", new[] { "matchzy_server_id \"s_1\"" }, ""));
        Assert.Empty(BootstrapPayloadCheck.ServerIdWarnings(
            "http://mat/api/servers/s_1/bootstrap", new[] { "matchzy_chat_prefix x" }, "s_2"));
    }
}

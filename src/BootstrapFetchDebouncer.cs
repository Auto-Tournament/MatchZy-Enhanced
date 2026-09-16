using System;
using System.Collections.Generic;

namespace MatchZy;

/// <summary>Runs a callback once after a delay. Disposing the handle cancels it.</summary>
public interface IBootstrapScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>
/// Decides when the plugin fetches its bootstrap payload after <c>matchzy_bootstrap_url</c> or
/// <c>matchzy_bootstrap_token</c> changes.
///
/// A controller sets both values with separate commands. Fetching as soon as either one is set
/// uses whatever the other one currently is, which is stale whenever both change: a fetch on
/// token-set hits the old URL (another server's id), a fetch on URL-set sends the old token. So
/// every change (re)starts a short timer instead, and when it fires the payload is fetched once
/// with the values current at that moment. The order of the two commands no longer matters.
///
/// Loop guard: a payload may itself set the URL or token. Setting the same value again shortly
/// after a payload was applied is treated as that payload's echo and ignored. A payload that
/// changes a value (a redirect) does trigger another fetch, but only
/// <see cref="MaxChainedFetches"/> times in a row.
///
/// All members must be called from one thread (the game thread in the plugin).
/// </summary>
public sealed class BootstrapFetchDebouncer
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>How long after a payload is applied an unchanged value counts as its echo.</summary>
    public static readonly TimeSpan DefaultEchoWindow = TimeSpan.FromSeconds(3);

    /// <summary>Fetches in a row that were triggered by a previous payload before giving up.</summary>
    public const int MaxChainedFetches = 3;

    public const string DebouncedReason = "console";

    private readonly IBootstrapScheduler scheduler;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<(string? Url, string? Token)> currentValues;
    private readonly Action<string, string, string> startFetch;
    private readonly Action<string> log;
    private readonly TimeSpan delay;
    private readonly TimeSpan echoWindow;

    private IDisposable? pendingTimer;
    private int timerGeneration;
    private bool refetchAfterCurrent;
    private DateTimeOffset? lastAppliedAt;
    private bool pendingTriggeredByPayload;
    private int chainedFetches;

    /// <param name="scheduler">Runs the debounce timer.</param>
    /// <param name="clock">Current time.</param>
    /// <param name="currentValues">Reads the current bootstrap URL and token when the timer fires.</param>
    /// <param name="startFetch">Starts a fetch with (url, token, reason). It must eventually call
    /// <see cref="OnFetchCompleted"/> on the same thread as everything else.</param>
    /// <param name="log">Diagnostic log sink.</param>
    public BootstrapFetchDebouncer(
        IBootstrapScheduler scheduler,
        Func<DateTimeOffset> clock,
        Func<(string? Url, string? Token)> currentValues,
        Action<string, string, string> startFetch,
        Action<string>? log = null,
        TimeSpan? delay = null,
        TimeSpan? echoWindow = null)
    {
        this.scheduler = scheduler;
        this.clock = clock;
        this.currentValues = currentValues;
        this.startFetch = startFetch;
        this.log = log ?? (_ => { });
        this.delay = delay ?? DefaultDelay;
        this.echoWindow = echoWindow ?? DefaultEchoWindow;
    }

    public TimeSpan Delay => delay;

    /// <summary>True while a fetch has been started and not yet completed.</summary>
    public bool FetchInProgress { get; private set; }

    /// <summary>True while the debounce timer is running.</summary>
    public bool FetchScheduled => pendingTimer != null;

    /// <summary>
    /// Call after <c>matchzy_bootstrap_url</c> or <c>matchzy_bootstrap_token</c> was set, with the
    /// value before and after. Returns true when the debounce timer was (re)started.
    /// </summary>
    public bool OnValueSet(string? previous, string? next)
    {
        bool afterPayload = lastAppliedAt.HasValue && clock() - lastAppliedAt.Value < echoWindow;
        if (afterPayload && string.Equals(Normalise(previous), Normalise(next), StringComparison.Ordinal))
        {
            log("[Bootstrap] Value re-set to the same value right after a bootstrap payload was applied; not refetching");
            return false;
        }

        pendingTriggeredByPayload |= afterPayload;
        Restart();
        return true;
    }

    /// <summary>
    /// Fetches immediately, without the debounce (startup, from persisted config). Returns false
    /// when nothing was started (a fetch is already running, or URL/token missing).
    /// </summary>
    public bool FetchNow(string reason)
    {
        if (FetchInProgress) return false;
        return Start(reason, triggeredByPayload: false);
    }

    /// <summary>
    /// Call when a fetch started through <c>startFetch</c> finished. <paramref name="applied"/> is
    /// true when a payload is about to be applied; call this before executing its commands so any
    /// URL/token commands inside it are recognised as its echo.
    /// </summary>
    public void OnFetchCompleted(bool applied)
    {
        FetchInProgress = false;
        if (applied)
        {
            lastAppliedAt = clock();
        }

        if (refetchAfterCurrent)
        {
            // Values changed while this fetch was running; fetch again with them.
            refetchAfterCurrent = false;
            Restart();
        }
    }

    private void Restart()
    {
        pendingTimer?.Dispose();
        int generation = ++timerGeneration;
        pendingTimer = scheduler.Schedule(delay, () => Fire(generation));
    }

    private void Fire(int generation)
    {
        if (generation != timerGeneration) return; // superseded by a later change
        pendingTimer = null;

        bool triggeredByPayload = pendingTriggeredByPayload;
        pendingTriggeredByPayload = false;

        if (FetchInProgress)
        {
            refetchAfterCurrent = true;
            pendingTriggeredByPayload |= triggeredByPayload;
            return;
        }

        Start(DebouncedReason, triggeredByPayload);
    }

    private bool Start(string reason, bool triggeredByPayload)
    {
        var (url, token) = currentValues();
        url = Normalise(url);
        token = Normalise(token);
        if (url.Length == 0 || token.Length == 0)
        {
            return false;
        }

        if (triggeredByPayload)
        {
            chainedFetches++;
            if (chainedFetches > MaxChainedFetches)
            {
                log($"[Bootstrap] Bootstrap payloads changed the bootstrap URL/token {MaxChainedFetches} times in a row; not fetching again");
                return false;
            }
        }
        else
        {
            chainedFetches = 0;
        }

        FetchInProgress = true;
        startFetch(url, token, reason);
        return true;
    }

    private static string Normalise(string? value) => value?.Trim() ?? "";
}

/// <summary>Consistency checks between the bootstrap URL and the payload it returned.</summary>
public static class BootstrapPayloadCheck
{
    /// <summary>
    /// The server id in a <c>.../api/servers/&lt;id&gt;/bootstrap</c> URL, or null when the URL has
    /// another shape.
    /// </summary>
    public static string? ServerIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int n = segments.Length;
        if (n < 3) return null;
        if (!segments[n - 1].Equals("bootstrap", StringComparison.OrdinalIgnoreCase)) return null;
        if (!segments[n - 3].Equals("servers", StringComparison.OrdinalIgnoreCase)) return null;

        string id = Uri.UnescapeDataString(segments[n - 2]).Trim();
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// The value of the last <c>matchzy_server_id</c> command in <paramref name="commands"/>, with
    /// surrounding quotes removed, or null when there is none.
    /// </summary>
    public static string? ServerIdFromCommands(IEnumerable<string>? commands)
    {
        if (commands == null) return null;

        const string name = "matchzy_server_id";
        string? found = null;
        foreach (string? raw in commands)
        {
            string cmd = raw?.Trim() ?? "";
            if (cmd.Length <= name.Length) continue;
            if (!cmd.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!char.IsWhiteSpace(cmd[name.Length])) continue;

            string value = cmd.Substring(name.Length).Trim().Trim('"').Trim();
            if (value.Length > 0) found = value;
        }
        return found;
    }

    /// <summary>
    /// Warnings about the server id a payload sets: when it differs from the id in the URL it was
    /// fetched from, or from the id the server already had (set by the controller just before the
    /// URL, so a difference usually means the URL is stale). Empty when everything agrees or the
    /// payload sets no id. Never blocks applying the payload.
    /// </summary>
    public static IReadOnlyList<string> ServerIdWarnings(string? url, IEnumerable<string>? commands, string? currentServerId)
    {
        var warnings = new List<string>();
        string? payloadId = ServerIdFromCommands(commands);
        if (payloadId == null) return warnings;

        string? urlId = ServerIdFromUrl(url);
        if (urlId != null && !string.Equals(urlId, payloadId, StringComparison.Ordinal))
        {
            warnings.Add($"[Bootstrap] WARNING: payload sets matchzy_server_id \"{payloadId}\" but was fetched from the bootstrap URL of server \"{urlId}\" ({url})");
        }

        string current = currentServerId?.Trim() ?? "";
        if (current.Length > 0 && !string.Equals(current, payloadId, StringComparison.Ordinal))
        {
            warnings.Add($"[Bootstrap] WARNING: payload changes matchzy_server_id from \"{current}\" to \"{payloadId}\" (fetched from {url}); if that is wrong, matchzy_bootstrap_url is stale");
        }

        return warnings;
    }
}

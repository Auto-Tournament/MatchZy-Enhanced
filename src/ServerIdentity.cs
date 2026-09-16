using System;
using System.Globalization;
using System.Linq;

namespace MatchZy;

/// <summary>
/// Derives the stable identity that scopes this server's rows in the shared database.
///
/// Several MatchZy servers on one box normally share a single MySQL database (that is the
/// point of a shared stats database), but everything in <c>matchzy_server_config</c> used to be
/// keyed by setting name alone. Per-server values - the server id, the bootstrap URL and token,
/// the remote log URL and its header, the demo upload URL - therefore collided: the last server
/// to write won, and on the next restart every server loaded that one server's values.
///
/// The identity below is deliberately derived from things the game server already knows about
/// itself, so it works before any external manager (MAT, a control panel, a deploy script) has
/// configured anything:
///
///   1. <c>matchzy_config_scope</c>, when the operator set one explicitly, either as a convar or
///      as <c>+matchzy_config_scope &lt;name&gt;</c> in the server's start arguments. This is the
///      escape hatch, and it is never persisted to the database, because a value that decides the
///      scope cannot itself be read from a scoped row. The start-argument form is the reliable
///      one: config.cfg is executed through the engine command buffer and may not have run yet.
///   2. The bind address and game port from the process command line (<c>-ip</c> / <c>-port</c>),
///      falling back to the <c>ip</c> and <c>hostport</c> convars.
///
/// The bind address is usually <c>0.0.0.0</c> (CS2 servers are nearly always started that way),
/// which cannot distinguish anything, so an unusable bind address is replaced by the machine
/// name. That gives, for three servers on a box called "cs2", the scopes <c>cs2:27015</c>,
/// <c>cs2:27025</c> and <c>cs2:27035</c> - stable across restarts, distinct per server on one
/// box, distinct across boxes sharing one database, and requiring no configuration.
///
/// Known failure modes, all recoverable and all documented in the README:
///   - Changing a server's game port changes its scope. The server then finds no scoped row and
///     falls back to the legacy (unscoped) row, and an external manager re-pushes its values on
///     the next configure. Set <c>matchzy_config_scope</c> to pin a name across port changes.
///   - Renaming the box changes the scope of every server on it that binds to 0.0.0.0. Same
///     recovery. Setting a real bind address, or <c>matchzy_config_scope</c>, avoids it.
///   - Two boxes with the same machine name and the same ports, sharing one database, still
///     collide. <c>matchzy_config_scope</c> is the fix.
/// </summary>
public static class ServerIdentity
{
    /// <summary>
    /// Scope used for rows written before scoping existed. Reads fall back to it; writes never
    /// target it.
    /// </summary>
    public const string LegacyScope = "";

    /// <summary>Port assumed when neither the command line nor the convars give one.</summary>
    public const int DefaultGamePort = 27015;

    /// <summary>Stand-in host component when neither a bind address nor a machine name is usable.</summary>
    public const string UnknownHost = "unknown-host";

    /// <summary>
    /// Maximum scope length. Kept well inside the <c>VARCHAR(190)</c> column so a scope is never
    /// silently truncated by the database into a collision with another one.
    /// </summary>
    public const int MaxScopeLength = 180;

    /// <summary>Bind addresses that identify no particular interface, and so identify no server.</summary>
    private static readonly string[] UnusableHosts =
    {
        "", "0", "0.0.0.0", "*", "::", "[::]", "::0", "127.0.0.1", "[::1]", "::1", "localhost",
    };

    /// <summary>Command line flags that carry the game port. <c>tv_port</c> is deliberately absent.</summary>
    private static readonly string[] PortFlags = { "-port", "+port", "-hostport", "+hostport" };

    /// <summary>Command line flags that carry the bind address.</summary>
    private static readonly string[] IpFlags = { "-ip", "+ip" };

    /// <summary>
    /// Command line flags that carry an explicit scope override. Reading the override from the
    /// command line as well as the convar matters because <c>execifexists MatchZy/config.cfg</c>
    /// goes through the engine command buffer and may not have run by the time the scope is first
    /// needed - <c>+matchzy_config_scope &lt;name&gt;</c> in the server's start arguments always has.
    /// </summary>
    private static readonly string[] ScopeFlags = { "+matchzy_config_scope", "-matchzy_config_scope" };

    /// <summary>
    /// Full resolution, in preference order: the convar override, the command line override, then
    /// the bind address and game port from the command line, then from the convars.
    /// </summary>
    public static string Resolve(
        string? explicitScope,
        string[]? commandLineArgs,
        string? convarBindIp,
        int? convarGamePort,
        string? machineName)
    {
        string? scopeOverride = !string.IsNullOrWhiteSpace(explicitScope)
            ? explicitScope
            : ParseScopeOverride(commandLineArgs);

        if (!string.IsNullOrWhiteSpace(scopeOverride))
        {
            return Sanitize(scopeOverride!);
        }

        string? bindIp = ParseBindIp(commandLineArgs) ?? convarBindIp;
        int? gamePort = ParseGamePort(commandLineArgs) ?? convarGamePort;

        return Derive(bindIp, gamePort, machineName);
    }

    /// <summary>
    /// Builds the <c>host:port</c> scope from an already-resolved bind address and port.
    /// </summary>
    public static string Derive(string? bindIp, int? gamePort, string? machineName)
    {
        string host;
        if (IsUsableHost(bindIp))
        {
            host = bindIp!.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(machineName))
        {
            host = machineName!.Trim();
        }
        else
        {
            host = UnknownHost;
        }

        int port = IsUsablePort(gamePort) ? gamePort!.Value : DefaultGamePort;

        return Sanitize($"{host}:{port.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// True when a bind address names one particular interface, and so can identify one server.
    /// </summary>
    public static bool IsUsableHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string trimmed = host!.Trim();
        return !UnusableHosts.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when a port is in the valid range.</summary>
    public static bool IsUsablePort(int? port) => port.HasValue && port.Value > 0 && port.Value <= 65535;

    /// <summary>
    /// Reads the game port from the process command line, e.g.
    /// <c>... -dedicated -ip 0.0.0.0 +map de_dust2 -port 27025 +tv_port 27030 ...</c> gives 27025.
    /// Returns null when no port flag is present or its value is not a valid port.
    /// </summary>
    public static int? ParseGamePort(string[]? args) =>
        int.TryParse(ValueAfterFlag(args, PortFlags), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
        && IsUsablePort(port)
            ? port
            : null;

    /// <summary>
    /// Reads the bind address from the process command line (<c>-ip &lt;addr&gt;</c>).
    /// Returns null when no address flag is present.
    /// </summary>
    public static string? ParseBindIp(string[]? args)
    {
        string? value = ValueAfterFlag(args, IpFlags);
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }

    /// <summary>
    /// Reads an explicit scope override from the process command line
    /// (<c>+matchzy_config_scope &lt;name&gt;</c>). Returns null when none is present.
    /// </summary>
    public static string? ParseScopeOverride(string[]? args)
    {
        string? value = ValueAfterFlag(args, ScopeFlags);
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }

    /// <summary>
    /// Normalises a scope so the same server always produces byte-identical rows: trimmed,
    /// lower-cased invariantly, internal whitespace collapsed to '-', and bounded in length.
    /// </summary>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UnknownHost;

        string value = raw.Trim().ToLowerInvariant();
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i])) chars[i] = '-';
        }
        value = new string(chars);

        if (value.Length > MaxScopeLength)
        {
            value = value.Substring(0, MaxScopeLength);
        }

        return value.Length == 0 ? UnknownHost : value;
    }

    /// <summary>
    /// Returns the argument following the first of <paramref name="flags"/> found in
    /// <paramref name="args"/>. Flags are matched whole, so <c>+tv_port</c> never matches
    /// <c>-port</c>.
    /// </summary>
    private static string? ValueAfterFlag(string[]? args, string[] flags)
    {
        if (args == null) return null;

        for (int i = 0; i < args.Length - 1; i++)
        {
            string arg = args[i];
            if (string.IsNullOrEmpty(arg)) continue;
            if (flags.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                string next = args[i + 1];
                if (!string.IsNullOrWhiteSpace(next)) return next;
            }
        }

        return null;
    }
}

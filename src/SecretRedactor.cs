using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutoTournamentCS2;

/// <summary>
/// Keeps secrets out of logs, console output and chat. Server logs get pasted into Discord and
/// GitHub issues when people ask for help, so anything that authenticates this server (the MAT
/// server token, remote log / demo upload header values, passwords) is shown as
/// <c>(hidden, N chars)</c> instead of its value.
/// </summary>
public static class SecretRedactor
{
    /// <summary>Config keys and convars whose values are secrets, whatever their name looks like.</summary>
    public static readonly IReadOnlySet<string> SecretKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "at_bootstrap_token",
        "at_match_token",
        "at_report_token",
        "at_remote_log_header_value",
        "get5_remote_log_header_value",
        "at_demo_upload_header_value",
        "get5_demo_upload_header_value",
        "at_remote_backup_header_value",
        "get5_remote_backup_header_value",
        "remote_log_header_value",
        "sv_password",
        "rcon_password",
        "tv_password",
        "tv_relaypassword",
    };

    // Any other config/convar name containing one of these is treated as a secret too.
    private static readonly string[] SecretKeyFragments = { "token", "password", "passwd", "secret", "header_value", "api_key", "apikey" };

    // HTTP header names that carry credentials. Custom header names (e.g. at_remote_log_header_key)
    // are passed in by the caller, since they can be anything.
    private static readonly string[] SecretHeaderFragments = { "token", "password", "secret", "auth", "cookie", "api-key", "apikey", "api_key" };

    // Query string parameters that carry credentials.
    private static readonly HashSet<string> SecretQueryParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "auth", "key", "api_key", "apikey", "secret", "password", "sig", "signature",
    };

    /// <summary>True if the value of this config key / convar must never be logged.</summary>
    public static bool IsSecretKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string trimmed = name.Trim();
        if (SecretKeys.Contains(trimmed)) return true;
        string lower = trimmed.ToLowerInvariant();
        return SecretKeyFragments.Any(lower.Contains);
    }

    /// <summary>True if this HTTP header name carries credentials.</summary>
    public static bool IsSecretHeader(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string lower = name.Trim().ToLowerInvariant();
        return SecretHeaderFragments.Any(lower.Contains);
    }

    /// <summary>Placeholder for a secret: says whether it is set and how long it is, never what it is.</summary>
    public static string Hidden(string? value)
    {
        return string.IsNullOrEmpty(value) ? "(empty)" : $"(hidden, {value.Length} chars)";
    }

    /// <summary>The value to log for a config key: hidden for secrets, otherwise the value with any embedded secrets redacted.</summary>
    public static string FormatValue(string? key, string? value)
    {
        if (IsSecretKey(key)) return Hidden(value);
        return RedactText(value ?? string.Empty);
    }

    /// <summary>
    /// Formats request headers for a log line as <c>Name: value, Name: value</c>. Values of
    /// credential-bearing headers, and of any header named in <paramref name="customSecretHeaders"/>, are hidden.
    /// </summary>
    public static string FormatHeaders(IEnumerable<KeyValuePair<string, string>> headers, IEnumerable<string?>? customSecretHeaders = null)
    {
        return string.Join(", ", RedactHeaders(headers, customSecretHeaders).Select(h => $"{h.Key}: {h.Value}"));
    }

    /// <summary>Returns a copy of the headers with secret values replaced by <see cref="Hidden"/>.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> RedactHeaders(IEnumerable<KeyValuePair<string, string>> headers, IEnumerable<string?>? customSecretHeaders = null)
    {
        var custom = new HashSet<string>(
            (customSecretHeaders ?? Enumerable.Empty<string?>()).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!.Trim()),
            StringComparer.OrdinalIgnoreCase);

        return headers
            .Select(h => new KeyValuePair<string, string>(
                h.Key,
                IsSecretHeader(h.Key) || custom.Contains(h.Key.Trim()) ? Hidden(h.Value) : h.Value))
            .ToList();
    }

    /// <summary>Formats a custom header name/value pair given on a command line (the value is always hidden).</summary>
    public static string FormatCustomHeader(string? name, string? value)
    {
        if (string.IsNullOrEmpty(name)) return "(none)";
        return $"{name}: {Hidden(value)}";
    }

    // "name": "value" in JSON.
    private static readonly Regex JsonStringProperty = new(
        "\"(?<name>[^\"\\\\]+)\"(?<sep>\\s*:\\s*)\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"",
        RegexOptions.Compiled);

    // A console command setting a convar: `name value`, `name "value"`, or `name \"value\"` inside a
    // JSON string. Only snake_case names (with an underscore) are considered, so prose such as
    // "invalid token provided" is left alone.
    private static readonly Regex CommandAssignment = new(
        "(?<name>\\b[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+\\b)(?<sep>[ \\t]+)(?<value>\\\\\"(?:[^\"\\\\]|\\\\(?!\"))*\\\\\"|\"(?:[^\"\\\\]|\\\\.)*\"|[^\\s;\"\\\\,\\]}]+)",
        RegexOptions.Compiled);

    // ?token=... / &key=... in URLs.
    private static readonly Regex QueryParam = new(
        "(?<prefix>[?&](?<name>[A-Za-z0-9_\\-]+)=)(?<value>[^&#\\s\"']*)",
        RegexOptions.Compiled);

    /// <summary>
    /// Redacts a single console command line or a list of commands separated by <c>;</c> or newlines,
    /// e.g. a bootstrap payload command <c>at_bootstrap_token "abc"</c> becomes
    /// <c>at_bootstrap_token "(hidden, 3 chars)"</c>.
    /// </summary>
    public static string RedactCommand(string? command)
    {
        if (string.IsNullOrEmpty(command)) return command ?? string.Empty;
        return CommandAssignment.Replace(command, m =>
        {
            if (!IsSecretKey(m.Groups["name"].Value)) return m.Value;
            string raw = m.Groups["value"].Value;
            string replacement;
            if (raw.StartsWith("\\\"", StringComparison.Ordinal) && raw.Length >= 4)
                replacement = $"\\\"{Hidden(raw.Substring(2, raw.Length - 4))}\\\"";
            else if (raw.StartsWith("\"", StringComparison.Ordinal) && raw.Length >= 2)
                replacement = $"\"{Hidden(raw.Substring(1, raw.Length - 2))}\"";
            else
                replacement = Hidden(raw);
            return m.Groups["name"].Value + m.Groups["sep"].Value + replacement;
        });
    }

    /// <summary>
    /// Redacts secrets inside free text about to be logged: JSON bodies (payloads, match configs,
    /// events, HTTP responses), console commands embedded in them, and credentials in URL query strings.
    /// </summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        string result = JsonStringProperty.Replace(text, m =>
            IsSecretKey(m.Groups["name"].Value)
                ? $"\"{m.Groups["name"].Value}\"{m.Groups["sep"].Value}\"{Hidden(m.Groups["value"].Value)}\""
                : m.Value);

        result = RedactCommand(result);

        result = QueryParam.Replace(result, m =>
            SecretQueryParams.Contains(m.Groups["name"].Value) || IsSecretKey(m.Groups["name"].Value)
                ? m.Groups["prefix"].Value + Hidden(m.Groups["value"].Value)
                : m.Value);

        return result;
    }
}

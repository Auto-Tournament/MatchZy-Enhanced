using System.Collections.Generic;
using MatchZy;
using Xunit;

namespace MatchZy.Tests;

public class SecretRedactorTests
{
    private const string Token = "s3cr3t-T0ken-value-1234";

    [Theory]
    [InlineData("matchzy_bootstrap_token")]
    [InlineData("MATCHZY_BOOTSTRAP_TOKEN")]
    [InlineData("matchzy_match_token")]
    [InlineData("matchzy_report_token")]
    [InlineData("matchzy_remote_log_header_value")]
    [InlineData("get5_remote_log_header_value")]
    [InlineData("matchzy_demo_upload_header_value")]
    [InlineData("matchzy_remote_backup_header_value")]
    [InlineData("remote_log_header_value")]
    [InlineData("sv_password")]
    [InlineData("rcon_password")]
    [InlineData("some_future_api_secret")]
    public void SecretKeysAreRecognised(string key)
    {
        Assert.True(SecretRedactor.IsSecretKey(key));
    }

    [Theory]
    [InlineData("matchzy_bootstrap_url")]
    [InlineData("matchzy_remote_log_url")]
    [InlineData("matchzy_remote_log_header_key")]
    [InlineData("matchzy_server_id")]
    [InlineData("matchzy_chat_prefix")]
    [InlineData("matchzy_tournament_status")]
    [InlineData("mp_maxrounds")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalKeysAreNotSecret(string? key)
    {
        Assert.False(SecretRedactor.IsSecretKey(key));
    }

    [Fact]
    public void FormatValueHidesSecretsAndShowsLength()
    {
        string formatted = SecretRedactor.FormatValue("matchzy_bootstrap_token", Token);

        Assert.DoesNotContain(Token, formatted);
        Assert.Equal($"(hidden, {Token.Length} chars)", formatted);
        Assert.Equal("(empty)", SecretRedactor.FormatValue("matchzy_bootstrap_token", ""));
    }

    [Fact]
    public void FormatValueLeavesNormalValuesUnchanged()
    {
        Assert.Equal("cs2-server-1", SecretRedactor.FormatValue("matchzy_server_id", "cs2-server-1"));
        Assert.Equal(
            "http://mat:3069/api/servers/cs2-server-1/bootstrap",
            SecretRedactor.FormatValue("matchzy_bootstrap_url", "http://mat:3069/api/servers/cs2-server-1/bootstrap"));
        Assert.Equal("X-MatchZy-Token", SecretRedactor.FormatValue("matchzy_remote_log_header_key", "X-MatchZy-Token"));
    }

    [Theory]
    [InlineData("matchzy_bootstrap_token \"" + Token + "\"", "matchzy_bootstrap_token \"(hidden, 23 chars)\"")]
    [InlineData("matchzy_bootstrap_token " + Token, "matchzy_bootstrap_token (hidden, 23 chars)")]
    [InlineData("matchzy_remote_log_header_value \"" + Token + "\"", "matchzy_remote_log_header_value \"(hidden, 23 chars)\"")]
    [InlineData("matchzy_server_id \"cs2-server-1\"", "matchzy_server_id \"cs2-server-1\"")]
    [InlineData("mp_maxrounds 24", "mp_maxrounds 24")]
    public void CommandLinesAreRedacted(string command, string expected)
    {
        Assert.Equal(expected, SecretRedactor.RedactCommand(command));
    }

    [Fact]
    public void MultipleCommandsOnOneLineAreRedacted()
    {
        string redacted = SecretRedactor.RedactCommand(
            $"matchzy_server_id cs2-server-1; matchzy_remote_log_header_key \"X-MatchZy-Token\"; matchzy_remote_log_header_value \"{Token}\"; sv_password hunter2");

        Assert.DoesNotContain(Token, redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.Contains("matchzy_server_id cs2-server-1", redacted);
        Assert.Contains("matchzy_remote_log_header_key \"X-MatchZy-Token\"", redacted);
    }

    [Fact]
    public void BootstrapPayloadJsonIsRedacted()
    {
        string payload =
            "{\"success\":true,\"serverId\":\"cs2-server-1\",\"commands\":[" +
            "\"matchzy_server_id \\\"cs2-server-1\\\"\"," +
            $"\"matchzy_bootstrap_token \\\"{Token}\\\"\"," +
            "\"matchzy_remote_log_header_key \\\"X-MatchZy-Token\\\"\"," +
            $"\"matchzy_remote_log_header_value \\\"{Token}\\\"\"," +
            $"\"matchzy_report_token {Token}\"" +
            "]}";

        string redacted = SecretRedactor.RedactText(payload);

        Assert.DoesNotContain(Token, redacted);
        Assert.Contains("\"matchzy_server_id \\\"cs2-server-1\\\"\"", redacted);
        Assert.Contains("\"matchzy_bootstrap_token \\\"(hidden, 23 chars)\\\"\"", redacted);
        Assert.Contains("\"matchzy_remote_log_header_key \\\"X-MatchZy-Token\\\"\"", redacted);
        Assert.Contains("\"matchzy_report_token (hidden, 23 chars)\"", redacted);
    }

    [Fact]
    public void JsonPropertiesWithSecretNamesAreRedacted()
    {
        string matchJson =
            "{\"matchid\":\"42\",\"remote_log_url\":\"http://mat/api/events\"," +
            $"\"remote_log_header_value\": \"{Token}\"," +
            $"\"cvars\":{{\"sv_password\":\"hunter2\",\"matchzy_bootstrap_token\":\"{Token}\",\"mp_maxrounds\":\"24\"}}}}";

        string redacted = SecretRedactor.RedactText(matchJson);

        Assert.DoesNotContain(Token, redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.Contains("\"remote_log_header_value\": \"(hidden, 23 chars)\"", redacted);
        Assert.Contains("\"matchid\":\"42\"", redacted);
        Assert.Contains("\"remote_log_url\":\"http://mat/api/events\"", redacted);
        Assert.Contains("\"mp_maxrounds\":\"24\"", redacted);
    }

    [Fact]
    public void ProseMentioningTokensIsLeftAlone()
    {
        const string message = "{\"error\":\"Invalid token provided\"}";
        Assert.Equal(message, SecretRedactor.RedactText(message));
    }

    [Fact]
    public void CredentialsInUrlQueryStringsAreRedacted()
    {
        string redacted = SecretRedactor.RedactText($"http://mat/api/events?server=cs2-server-1&token={Token}&x=1");

        Assert.DoesNotContain(Token, redacted);
        Assert.Equal("http://mat/api/events?server=cs2-server-1&token=(hidden, 23 chars)&x=1", redacted);
    }

    [Fact]
    public void HeaderDictionariesAreRedacted()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-MatchZy-Token"] = Token,
            ["Authorization"] = "Bearer " + Token,
            ["My-Custom-Auth"] = Token,
            ["Some-Custom-Header"] = Token,
            ["MatchZy-FileName"] = "demo.dem",
            ["Content-Type"] = "application/octet-stream",
        };

        var redacted = SecretRedactor.RedactHeaders(headers, customSecretHeaders: new[] { "some-custom-header" });
        string formatted = SecretRedactor.FormatHeaders(headers, customSecretHeaders: new[] { "Some-Custom-Header" });

        foreach (var header in redacted)
        {
            if (header.Key is "MatchZy-FileName" or "Content-Type")
                Assert.Equal(headers[header.Key], header.Value);
            else
                Assert.StartsWith("(hidden, ", header.Value);
        }
        Assert.DoesNotContain(Token, formatted);
        Assert.Contains("MatchZy-FileName: demo.dem", formatted);
        Assert.Contains("X-MatchZy-Token: (hidden, 23 chars)", formatted);
    }

    [Fact]
    public void CustomHeaderFromCommandLineAlwaysHidesTheValue()
    {
        Assert.Equal("X-Whatever: (hidden, 23 chars)", SecretRedactor.FormatCustomHeader("X-Whatever", Token));
        Assert.Equal("(none)", SecretRedactor.FormatCustomHeader("", ""));
    }
}

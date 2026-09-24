using System.Collections.Generic;
using AutoTournamentCS2;
using Xunit;

namespace AutoTournamentCS2.Tests;

public class SecretRedactorTests
{
    private const string Token = "s3cr3t-T0ken-value-1234";

    [Theory]
    [InlineData("at_bootstrap_token")]
    [InlineData("AT_BOOTSTRAP_TOKEN")]
    [InlineData("at_match_token")]
    [InlineData("at_report_token")]
    [InlineData("at_remote_log_header_value")]
    [InlineData("get5_remote_log_header_value")]
    [InlineData("at_demo_upload_header_value")]
    [InlineData("at_remote_backup_header_value")]
    [InlineData("remote_log_header_value")]
    [InlineData("sv_password")]
    [InlineData("rcon_password")]
    [InlineData("some_future_api_secret")]
    public void SecretKeysAreRecognised(string key)
    {
        Assert.True(SecretRedactor.IsSecretKey(key));
    }

    [Theory]
    [InlineData("at_bootstrap_url")]
    [InlineData("at_remote_log_url")]
    [InlineData("at_remote_log_header_key")]
    [InlineData("at_server_id")]
    [InlineData("at_chat_prefix")]
    [InlineData("at_tournament_status")]
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
        string formatted = SecretRedactor.FormatValue("at_bootstrap_token", Token);

        Assert.DoesNotContain(Token, formatted);
        Assert.Equal($"(hidden, {Token.Length} chars)", formatted);
        Assert.Equal("(empty)", SecretRedactor.FormatValue("at_bootstrap_token", ""));
    }

    [Fact]
    public void FormatValueLeavesNormalValuesUnchanged()
    {
        Assert.Equal("cs2-server-1", SecretRedactor.FormatValue("at_server_id", "cs2-server-1"));
        Assert.Equal(
            "http://mat:3069/api/servers/cs2-server-1/bootstrap",
            SecretRedactor.FormatValue("at_bootstrap_url", "http://mat:3069/api/servers/cs2-server-1/bootstrap"));
        Assert.Equal("X-Auto-Tournament-Token", SecretRedactor.FormatValue("at_remote_log_header_key", "X-Auto-Tournament-Token"));
    }

    [Theory]
    [InlineData("at_bootstrap_token \"" + Token + "\"", "at_bootstrap_token \"(hidden, 23 chars)\"")]
    [InlineData("at_bootstrap_token " + Token, "at_bootstrap_token (hidden, 23 chars)")]
    [InlineData("at_remote_log_header_value \"" + Token + "\"", "at_remote_log_header_value \"(hidden, 23 chars)\"")]
    [InlineData("at_server_id \"cs2-server-1\"", "at_server_id \"cs2-server-1\"")]
    [InlineData("mp_maxrounds 24", "mp_maxrounds 24")]
    public void CommandLinesAreRedacted(string command, string expected)
    {
        Assert.Equal(expected, SecretRedactor.RedactCommand(command));
    }

    [Fact]
    public void MultipleCommandsOnOneLineAreRedacted()
    {
        string redacted = SecretRedactor.RedactCommand(
            $"at_server_id cs2-server-1; at_remote_log_header_key \"X-Auto-Tournament-Token\"; at_remote_log_header_value \"{Token}\"; sv_password hunter2");

        Assert.DoesNotContain(Token, redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.Contains("at_server_id cs2-server-1", redacted);
        Assert.Contains("at_remote_log_header_key \"X-Auto-Tournament-Token\"", redacted);
    }

    [Fact]
    public void BootstrapPayloadJsonIsRedacted()
    {
        string payload =
            "{\"success\":true,\"serverId\":\"cs2-server-1\",\"commands\":[" +
            "\"at_server_id \\\"cs2-server-1\\\"\"," +
            $"\"at_bootstrap_token \\\"{Token}\\\"\"," +
            "\"at_remote_log_header_key \\\"X-Auto-Tournament-Token\\\"\"," +
            $"\"at_remote_log_header_value \\\"{Token}\\\"\"," +
            $"\"at_report_token {Token}\"" +
            "]}";

        string redacted = SecretRedactor.RedactText(payload);

        Assert.DoesNotContain(Token, redacted);
        Assert.Contains("\"at_server_id \\\"cs2-server-1\\\"\"", redacted);
        Assert.Contains("\"at_bootstrap_token \\\"(hidden, 23 chars)\\\"\"", redacted);
        Assert.Contains("\"at_remote_log_header_key \\\"X-Auto-Tournament-Token\\\"\"", redacted);
        Assert.Contains("\"at_report_token (hidden, 23 chars)\"", redacted);
    }

    [Fact]
    public void JsonPropertiesWithSecretNamesAreRedacted()
    {
        string matchJson =
            "{\"matchid\":\"42\",\"remote_log_url\":\"http://mat/api/events\"," +
            $"\"remote_log_header_value\": \"{Token}\"," +
            $"\"cvars\":{{\"sv_password\":\"hunter2\",\"at_bootstrap_token\":\"{Token}\",\"mp_maxrounds\":\"24\"}}}}";

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
            ["X-Auto-Tournament-Token"] = Token,
            ["Authorization"] = "Bearer " + Token,
            ["My-Custom-Auth"] = Token,
            ["Some-Custom-Header"] = Token,
            ["Auto-Tournament-FileName"] = "demo.dem",
            ["Content-Type"] = "application/octet-stream",
        };

        var redacted = SecretRedactor.RedactHeaders(headers, customSecretHeaders: new[] { "some-custom-header" });
        string formatted = SecretRedactor.FormatHeaders(headers, customSecretHeaders: new[] { "Some-Custom-Header" });

        foreach (var header in redacted)
        {
            if (header.Key is "Auto-Tournament-FileName" or "Content-Type")
                Assert.Equal(headers[header.Key], header.Value);
            else
                Assert.StartsWith("(hidden, ", header.Value);
        }
        Assert.DoesNotContain(Token, formatted);
        Assert.Contains("Auto-Tournament-FileName: demo.dem", formatted);
        Assert.Contains("X-Auto-Tournament-Token: (hidden, 23 chars)", formatted);
    }

    [Fact]
    public void CustomHeaderFromCommandLineAlwaysHidesTheValue()
    {
        Assert.Equal("X-Whatever: (hidden, 23 chars)", SecretRedactor.FormatCustomHeader("X-Whatever", Token));
        Assert.Equal("(none)", SecretRedactor.FormatCustomHeader("", ""));
    }
}

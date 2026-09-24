using System.Data;
using Dapper;
using AutoTournamentCS2;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AutoTournamentCS2.Tests;

/// <summary>
/// Exercises the scoped config store and its migration against a real SQLite database.
/// The MySQL paths differ only in their DDL dialect; the read preference, the write target and
/// the legacy fallback are the same SQL on both.
/// </summary>
public class PersistentConfigStoreTests : IDisposable
{
    private readonly string dbPath;
    private readonly SqliteConnection connection;

    private const string ServerOne = "cs2:27015";
    private const string ServerTwo = "cs2:27025";
    private const string ServerThree = "cs2:27035";

    public PersistentConfigStoreTests()
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"at-test-{Guid.NewGuid():N}.db");
        connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
    }

    public void Dispose()
    {
        connection.Close();
        connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>The table exactly as it was before per-server scoping existed.</summary>
    private void CreateLegacyConfigTable() => connection.Execute(@"
        CREATE TABLE at_server_config (
            config_key TEXT PRIMARY KEY,
            config_value TEXT NOT NULL,
            updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
        )");

    private void InsertLegacyRow(string key, string value) => connection.Execute(
        "INSERT INTO at_server_config (config_key, config_value) VALUES (@Key, @Value)",
        new { Key = key, Value = value });

    private void EnsureSchema() => PersistentConfigStore.EnsureConfigSchema(connection, isSqlite: true);

    private string? Load(string key, string scope) =>
        PersistentConfigStore.LoadConfigValue(connection, key, scope);

    private void Save(string key, string value, string scope) =>
        PersistentConfigStore.SaveConfigValue(connection, isSqlite: true, key, value, scope);

    // ---- schema creation -------------------------------------------------------------------

    [Fact]
    public void AFreshDatabaseGetsTheScopedSchema()
    {
        EnsureSchema();

        Assert.True(PersistentConfigStore.TableExists(connection, true, "at_server_config"));
        Assert.True(PersistentConfigStore.ColumnExists(connection, true, "at_server_config", "server_scope"));
    }

    [Fact]
    public void EnsureSchemaIsIdempotent()
    {
        EnsureSchema();
        Save("at_server_id", "s_1", ServerOne);

        EnsureSchema();
        EnsureSchema();

        Assert.Equal("s_1", Load("at_server_id", ServerOne));
    }

    // ---- migration -------------------------------------------------------------------------

    [Fact]
    public void MigrationAddsTheScopeColumnToAnExistingTable()
    {
        CreateLegacyConfigTable();
        Assert.False(PersistentConfigStore.ColumnExists(connection, true, "at_server_config", "server_scope"));

        EnsureSchema();

        Assert.True(PersistentConfigStore.ColumnExists(connection, true, "at_server_config", "server_scope"));
    }

    [Fact]
    public void MigrationPreservesExistingRowsAsLegacyRows()
    {
        CreateLegacyConfigTable();
        InsertLegacyRow("at_server_id", "s_3");
        InsertLegacyRow("at_bootstrap_url", "http://192.168.50.196:3069/api/servers/s_3/bootstrap");
        InsertLegacyRow("at_chat_prefix", "[Auto Tournament]");

        EnsureSchema();

        var rows = connection.Query<(string Scope, string Key, string Value)>(
            "SELECT server_scope, config_key, config_value FROM at_server_config ORDER BY config_key").ToList();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(ServerIdentity.LegacyScope, row.Scope));
        Assert.Equal("http://192.168.50.196:3069/api/servers/s_3/bootstrap",
            rows.Single(r => r.Key == "at_bootstrap_url").Value);
        Assert.Equal("s_3", rows.Single(r => r.Key == "at_server_id").Value);
    }

    [Fact]
    public void MigrationLeavesNoRebuildTableBehind()
    {
        CreateLegacyConfigTable();
        InsertLegacyRow("at_server_id", "s_3");

        EnsureSchema();

        Assert.False(PersistentConfigStore.TableExists(connection, true, "at_server_config_pre_scope"));
    }

    [Fact]
    public void MigrationRecoversFromAnInterruptedEarlierAttempt()
    {
        CreateLegacyConfigTable();
        InsertLegacyRow("at_server_id", "s_3");
        // A crash mid-rebuild would leave the parked table behind and block the rename.
        connection.Execute("CREATE TABLE at_server_config_pre_scope (config_key TEXT PRIMARY KEY, config_value TEXT NOT NULL, updated_at DATETIME)");

        EnsureSchema();

        Assert.Equal("s_3", Load("at_server_id", ServerOne));
        Assert.False(PersistentConfigStore.TableExists(connection, true, "at_server_config_pre_scope"));
    }

    // ---- read preference -------------------------------------------------------------------

    [Fact]
    public void ReadsFallBackToTheLegacyRowWhenThisServerHasNotWrittenYet()
    {
        CreateLegacyConfigTable();
        InsertLegacyRow("at_remote_log_url", "http://legacy/events");
        EnsureSchema();

        // Every server keeps reading what it read before the upgrade: no operator action needed
        // for a single-server install.
        Assert.Equal("http://legacy/events", Load("at_remote_log_url", ServerOne));
        Assert.Equal("http://legacy/events", Load("at_remote_log_url", ServerTwo));
    }

    [Fact]
    public void ReadsPreferTheScopedRowOverTheLegacyRow()
    {
        CreateLegacyConfigTable();
        InsertLegacyRow("at_server_id", "s_3");
        EnsureSchema();

        Save("at_server_id", "s_1", ServerOne);

        Assert.Equal("s_1", Load("at_server_id", ServerOne));
        // Untouched servers still see the legacy value until they write their own.
        Assert.Equal("s_3", Load("at_server_id", ServerTwo));
    }

    [Fact]
    public void AMissingKeyReadsAsNull()
    {
        EnsureSchema();

        Assert.Null(Load("at_server_id", ServerOne));
    }

    // ---- write target ----------------------------------------------------------------------

    [Fact]
    public void WritesNeverOverwriteTheLegacyRow()
    {
        CreateLegacyConfigTable();
        InsertLegacyRow("at_server_id", "s_3");
        EnsureSchema();

        Save("at_server_id", "s_1", ServerOne);

        string legacy = connection.QuerySingle<string>(
            "SELECT config_value FROM at_server_config WHERE config_key = 'at_server_id' AND server_scope = ''");
        Assert.Equal("s_3", legacy);
    }

    [Fact]
    public void WritingTheSameKeyTwiceUpdatesInPlace()
    {
        EnsureSchema();

        Save("at_server_id", "s_1", ServerOne);
        Save("at_server_id", "s_1-renamed", ServerOne);

        Assert.Equal("s_1-renamed", Load("at_server_id", ServerOne));
        Assert.Equal(1, connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM at_server_config WHERE config_key = 'at_server_id'"));
    }

    // ---- the reported bug ------------------------------------------------------------------

    [Fact]
    public void ThreeServersSharingOneDatabaseKeepTheirOwnServerIdAndBootstrapUrl()
    {
        EnsureSchema();

        // Exactly what happened in production, in the order it happened: each server is
        // configured over RCON, last writer previously won.
        Save("at_server_id", "s_1", ServerOne);
        Save("at_bootstrap_url", "http://192.168.50.196:3069/api/servers/s_1/bootstrap", ServerOne);
        Save("at_server_id", "s_2", ServerTwo);
        Save("at_bootstrap_url", "http://192.168.50.196:3069/api/servers/s_2/bootstrap", ServerTwo);
        Save("at_server_id", "s_3", ServerThree);
        Save("at_bootstrap_url", "http://192.168.50.196:3069/api/servers/s_3/bootstrap", ServerThree);

        // What each server loads on its next restart.
        Assert.Equal("s_1", Load("at_server_id", ServerOne));
        Assert.Equal("s_2", Load("at_server_id", ServerTwo));
        Assert.Equal("s_3", Load("at_server_id", ServerThree));

        Assert.Equal("http://192.168.50.196:3069/api/servers/s_1/bootstrap", Load("at_bootstrap_url", ServerOne));
        Assert.Equal("http://192.168.50.196:3069/api/servers/s_2/bootstrap", Load("at_bootstrap_url", ServerTwo));
        Assert.Equal("http://192.168.50.196:3069/api/servers/s_3/bootstrap", Load("at_bootstrap_url", ServerThree));
    }

    [Fact]
    public void RowsWrittenBy1426UnderTheSharedHostKeyAreNeverReadByAnotherScope()
    {
        EnsureSchema();

        // Pre-migration shared row, then 1.4.26 on three servers all writing under cs2:27015.
        connection.Execute(
            "INSERT INTO at_server_config (server_scope, config_key, config_value) VALUES ('', 'at_server_id', 'legacy')");
        Save("at_server_id", "s_1", "cs2:27015");
        Save("at_server_id", "s_2", "cs2:27015");
        Save("at_server_id", "s_3", "cs2:27015");

        // After the fix each server resolves its own scope from +at_config_scope. Before it
        // has written, it falls back to the legacy row only - never to another scope's row.
        Assert.Equal("legacy", Load("at_server_id", "cs2-server-1"));
        Assert.Equal("legacy", Load("at_server_id", "cs2-server-2"));

        Save("at_server_id", "s_2", "cs2-server-2");
        Assert.Equal("s_2", Load("at_server_id", "cs2-server-2"));
        Assert.Equal("legacy", Load("at_server_id", "cs2-server-1"));

        // The stale shared row is left in place, untouched.
        Assert.Equal("s_3", Load("at_server_id", "cs2:27015"));
    }

    [Fact]
    public void OneServerPerDatabaseStillBehavesExactlyAsBefore()
    {
        EnsureSchema();

        Save("at_server_id", "s_1", ServerOne);
        Save("at_remote_log_url", "http://mat/events", ServerOne);

        Assert.Equal("s_1", Load("at_server_id", ServerOne));
        Assert.Equal("http://mat/events", Load("at_remote_log_url", ServerOne));
    }

    // ---- event queue -----------------------------------------------------------------------

    [Fact]
    public void EventQueueMigrationAddsTheScopeColumnAndKeepsQueuedEvents()
    {
        connection.Execute(@"
            CREATE TABLE at_event_queue (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                event_data TEXT NOT NULL,
                match_id INTEGER,
                map_number INTEGER DEFAULT 0,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                retry_count INTEGER DEFAULT 0,
                last_retry DATETIME,
                next_retry DATETIME,
                status TEXT DEFAULT 'pending',
                error_message TEXT
            )");
        connection.Execute(
            "INSERT INTO at_event_queue (event_type, event_data, match_id) VALUES ('map_result', '{}', 42)");

        PersistentConfigStore.EnsureEventQueueSchema(connection, isSqlite: true);

        Assert.True(PersistentConfigStore.ColumnExists(connection, true, "at_event_queue", "server_scope"));
        Assert.Equal(ServerIdentity.LegacyScope, connection.QuerySingle<string>(
            "SELECT server_scope FROM at_event_queue WHERE match_id = 42"));
    }

    [Fact]
    public void PendingEventsAreThisServersPlusTheLegacyBacklog()
    {
        connection.Execute(@"
            CREATE TABLE at_event_queue (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                status TEXT DEFAULT 'pending',
                server_scope TEXT NOT NULL DEFAULT ''
            )");
        connection.Execute("INSERT INTO at_event_queue (event_type, server_scope) VALUES ('mine', @S)", new { S = ServerOne });
        connection.Execute("INSERT INTO at_event_queue (event_type, server_scope) VALUES ('theirs', @S)", new { S = ServerTwo });
        connection.Execute("INSERT INTO at_event_queue (event_type, server_scope) VALUES ('legacy', '')");

        var visible = connection.Query<string>(
            $"SELECT event_type FROM at_event_queue WHERE {PersistentConfigStore.PendingEventsScopeClause}",
            new { Scope = ServerOne, LegacyScope = ServerIdentity.LegacyScope }).ToList();

        Assert.Equal(new[] { "legacy", "mine" }, visible.OrderBy(e => e).ToArray());
        Assert.DoesNotContain("theirs", visible);
    }
}

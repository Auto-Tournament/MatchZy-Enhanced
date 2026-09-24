using Dapper;
using AutoTournamentCS2;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AutoTournamentCS2.Tests;

/// <summary>
/// The first-start carry-over from the pre-2.0.0 names: config folder, SQLite file, tables and
/// saved settings. Everything runs against real directories and a real SQLite database.
/// </summary>
public class LegacyCarryOverTests : IDisposable
{
    private readonly string root;
    private readonly List<string> log = new();

    public LegacyCarryOverTests()
    {
        root = Path.Combine(Path.GetTempPath(), $"at-carryover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void Log(string message) => log.Add(message);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ---- config folder ---------------------------------------------------------------------

    [Fact]
    public void ConfigFolder_IsMovedWhole_WhenTheNewOneDoesNotExist()
    {
        string cfg = Path.Combine(root, "cfg");
        Write(Path.Combine(cfg, "MatchZy", "config.cfg"), "operator config");
        Write(Path.Combine(cfg, "MatchZy", "database.json"), "{\"DatabaseType\":\"MySQL\"}");
        Write(Path.Combine(cfg, "MatchZy", "logs", "old.log"), "log");

        int moved = LegacyCarryOver.CarryOverConfigFolder(cfg, Log);

        Assert.Equal(3, moved);
        Assert.False(Directory.Exists(Path.Combine(cfg, "MatchZy")));
        Assert.Equal("operator config", File.ReadAllText(Path.Combine(cfg, "AutoTournamentCS2", "config.cfg")));
        Assert.Equal("{\"DatabaseType\":\"MySQL\"}", File.ReadAllText(Path.Combine(cfg, "AutoTournamentCS2", "database.json")));
        Assert.True(File.Exists(Path.Combine(cfg, "AutoTournamentCS2", "logs", "old.log")));
        Assert.Contains(log, l => l.Contains("Moved cfg/MatchZy/ to cfg/AutoTournamentCS2/ (3 file(s))"));
        Assert.Contains(log, l => l.Contains("cfg/MatchZy/logs/old.log -> cfg/AutoTournamentCS2/logs/old.log"));
    }

    [Fact]
    public void ConfigFolder_MovesOnlyMissingFiles_AndNeverOverwrites_WhenBothExist()
    {
        string cfg = Path.Combine(root, "cfg");
        Write(Path.Combine(cfg, "MatchZy", "config.cfg"), "operator config");
        Write(Path.Combine(cfg, "MatchZy", "database.json"), "operator database");
        Write(Path.Combine(cfg, "AutoTournamentCS2", "config.cfg"), "shipped default");

        int moved = LegacyCarryOver.CarryOverConfigFolder(cfg, Log);

        Assert.Equal(1, moved);
        // The new file wins and is untouched; the old one is left where it was.
        Assert.Equal("shipped default", File.ReadAllText(Path.Combine(cfg, "AutoTournamentCS2", "config.cfg")));
        Assert.Equal("operator config", File.ReadAllText(Path.Combine(cfg, "MatchZy", "config.cfg")));
        // The file the new folder did not have was carried over.
        Assert.Equal("operator database", File.ReadAllText(Path.Combine(cfg, "AutoTournamentCS2", "database.json")));
        Assert.False(File.Exists(Path.Combine(cfg, "MatchZy", "database.json")));
        Assert.Contains(log, l => l.Contains("WARNING") && l.Contains("config.cfg"));
    }

    [Fact]
    public void ConfigFolder_DoesNothing_WithoutAnOldFolder()
    {
        string cfg = Path.Combine(root, "cfg");
        Write(Path.Combine(cfg, "AutoTournamentCS2", "config.cfg"), "x");

        Assert.Equal(0, LegacyCarryOver.CarryOverConfigFolder(cfg, Log));
        Assert.Empty(log);
    }

    // ---- SQLite file -----------------------------------------------------------------------

    [Fact]
    public void SqliteFile_IsTakenFromTheOldPluginFolder()
    {
        string plugins = Path.Combine(root, "plugins");
        string module = Path.Combine(plugins, "AutoTournamentCS2");
        Directory.CreateDirectory(module);
        Write(Path.Combine(plugins, "MatchZy", "matchzy.db"), "history");
        Write(Path.Combine(plugins, "MatchZy", "matchzy.db-journal"), "journal");

        Assert.True(LegacyCarryOver.CarryOverSqliteFile(module, Log));

        Assert.Equal("history", File.ReadAllText(Path.Combine(module, "auto_tournament_cs2.db")));
        Assert.Equal("journal", File.ReadAllText(Path.Combine(module, "auto_tournament_cs2.db-journal")));
        Assert.False(File.Exists(Path.Combine(plugins, "MatchZy", "matchzy.db")));
        Assert.Contains(log, l => l.Contains("Renamed SQLite database"));
    }

    [Fact]
    public void SqliteFile_IsNotOverwritten_WhenTheNewOneExists()
    {
        string plugins = Path.Combine(root, "plugins");
        string module = Path.Combine(plugins, "AutoTournamentCS2");
        Write(Path.Combine(module, "auto_tournament_cs2.db"), "new");
        Write(Path.Combine(plugins, "MatchZy", "matchzy.db"), "old");

        Assert.False(LegacyCarryOver.CarryOverSqliteFile(module, Log));

        Assert.Equal("new", File.ReadAllText(Path.Combine(module, "auto_tournament_cs2.db")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(plugins, "MatchZy", "matchzy.db")));
        Assert.Contains(log, l => l.Contains("WARNING"));
    }

    // ---- tables and saved settings ---------------------------------------------------------

    /// <summary>A database as a 1.x plugin left it, with a match, a map, a player and settings.</summary>
    private SqliteConnection OpenLegacyDatabase()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "carry.db")}");
        connection.Open();
        connection.Execute(@"
            CREATE TABLE matchzy_stats_matches (matchid INTEGER PRIMARY KEY AUTOINCREMENT, team1_name TEXT NOT NULL);
            CREATE TABLE matchzy_stats_maps (
                matchid INTEGER NOT NULL, mapnumber INTEGER NOT NULL, mapname TEXT NOT NULL,
                PRIMARY KEY (matchid, mapnumber),
                FOREIGN KEY (matchid) REFERENCES matchzy_stats_matches (matchid));
            CREATE TABLE matchzy_stats_players (
                matchid INTEGER NOT NULL, mapnumber INTEGER NOT NULL, steamid64 INTEGER NOT NULL, kills INTEGER NOT NULL,
                PRIMARY KEY (matchid, mapnumber, steamid64),
                FOREIGN KEY (matchid) REFERENCES matchzy_stats_matches (matchid),
                FOREIGN KEY (matchid, mapnumber) REFERENCES matchzy_stats_maps (matchid, mapnumber));
            CREATE TABLE matchzy_server_config (
                server_scope TEXT NOT NULL DEFAULT '', config_key TEXT NOT NULL, config_value TEXT NOT NULL,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP, PRIMARY KEY (server_scope, config_key));
            CREATE TABLE matchzy_event_queue (id INTEGER PRIMARY KEY AUTOINCREMENT, event_type TEXT NOT NULL);

            INSERT INTO matchzy_stats_matches (matchid, team1_name) VALUES (7, 'Team A');
            INSERT INTO matchzy_stats_maps (matchid, mapnumber, mapname) VALUES (7, 0, 'de_dust2');
            INSERT INTO matchzy_stats_players (matchid, mapnumber, steamid64, kills) VALUES (7, 0, 76561198000000001, 23);
            INSERT INTO matchzy_server_config (server_scope, config_key, config_value) VALUES ('cs2:27015', 'matchzy_server_id', 'cs2-server-1');
            INSERT INTO matchzy_server_config (server_scope, config_key, config_value) VALUES ('', 'matchzy_bootstrap_url', 'http://mat/bootstrap');
            INSERT INTO matchzy_event_queue (event_type) VALUES ('series_end');");
        return connection;
    }

    [Fact]
    public void Tables_AreRenamed_WithTheirData_AndForeignKeysFollow()
    {
        using var connection = OpenLegacyDatabase();

        var renamed = LegacyCarryOver.CarryOverTables(connection, isSqlite: true, Log);

        Assert.Equal(5, renamed.Count);
        foreach (string suffix in LegacyCarryOver.TableSuffixes)
        {
            Assert.False(PersistentConfigStore.TableExists(connection, true, "matchzy_" + suffix));
            Assert.True(PersistentConfigStore.TableExists(connection, true, "at_" + suffix));
            Assert.Contains(log, l => l.Contains($"Renamed table matchzy_{suffix} -> at_{suffix}"));
        }

        Assert.Equal("Team A", connection.ExecuteScalar<string>("SELECT team1_name FROM at_stats_matches WHERE matchid = 7"));
        Assert.Equal(23, connection.ExecuteScalar<int>("SELECT kills FROM at_stats_players WHERE matchid = 7"));
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM at_event_queue"));

        string playersSql = connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE name = 'at_stats_players'");
        Assert.Contains("at_stats_maps", playersSql);
        Assert.DoesNotContain("matchzy_", playersSql);
        Assert.False(LegacyCarryOver.HasPendingTables(connection, isSqlite: true));
    }

    [Fact]
    public void Tables_AreLeftAlone_WhenTheNewNameExists()
    {
        using var connection = OpenLegacyDatabase();
        connection.Execute("CREATE TABLE at_stats_matches (matchid INTEGER PRIMARY KEY, team1_name TEXT NOT NULL)");

        var renamed = LegacyCarryOver.CarryOverTables(connection, isSqlite: true, Log);

        Assert.DoesNotContain(renamed, r => r.Old == "matchzy_stats_matches");
        Assert.True(PersistentConfigStore.TableExists(connection, true, "matchzy_stats_matches"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM at_stats_matches"));
        Assert.Contains(log, l => l.Contains("WARNING: both tables matchzy_stats_matches and at_stats_matches exist"));
        // The rest still moved.
        Assert.True(PersistentConfigStore.TableExists(connection, true, "at_stats_maps"));
    }

    [Fact]
    public void SavedSettings_AreRenamed_AndReadableUnderTheNewName()
    {
        using var connection = OpenLegacyDatabase();
        LegacyCarryOver.CarryOverTables(connection, isSqlite: true, Log);
        PersistentConfigStore.EnsureConfigSchema(connection, isSqlite: true);

        int renamed = LegacyCarryOver.CarryOverConfigKeys(connection, isSqlite: true, Log);

        Assert.Equal(2, renamed);
        Assert.Equal("cs2-server-1", PersistentConfigStore.LoadConfigValue(connection, "at_server_id", "cs2:27015"));
        Assert.Equal("http://mat/bootstrap", PersistentConfigStore.LoadConfigValue(connection, "at_bootstrap_url", "cs2:27015"));
        Assert.Null(PersistentConfigStore.LoadConfigValue(connection, "matchzy_server_id", "cs2:27015"));

        // A second start has nothing left to do.
        Assert.Equal(0, LegacyCarryOver.CarryOverConfigKeys(connection, isSqlite: true, Log));
    }

    [Fact]
    public void SavedSettings_NeverOverwriteANewValue()
    {
        using var connection = OpenLegacyDatabase();
        LegacyCarryOver.CarryOverTables(connection, isSqlite: true, Log);
        PersistentConfigStore.EnsureConfigSchema(connection, isSqlite: true);
        PersistentConfigStore.SaveConfigValue(connection, isSqlite: true, "at_server_id", "set-after-upgrade", "cs2:27015");

        int renamed = LegacyCarryOver.CarryOverConfigKeys(connection, isSqlite: true, Log);

        Assert.Equal(1, renamed); // only the bootstrap URL
        Assert.Equal("set-after-upgrade", PersistentConfigStore.LoadConfigValue(connection, "at_server_id", "cs2:27015"));
        Assert.Equal("cs2-server-1", connection.ExecuteScalar<string>(
            "SELECT config_value FROM at_server_config WHERE config_key = 'matchzy_server_id'"));
        Assert.Contains(log, l => l.Contains("WARNING") && l.Contains("matchzy_server_id"));
    }
}

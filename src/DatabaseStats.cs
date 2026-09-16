using System;
using System.IO;
using System.Data;
using System.Text.Json;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;
using Dapper;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CsvHelper;
using CsvHelper.Configuration;
using MySqlConnector;



namespace MatchZy
{
    public class QueuedEvent
    {
        public int id { get; set; }
        public string event_type { get; set; } = "";
        public string event_data { get; set; } = "";
        public long match_id { get; set; }
        public int map_number { get; set; }
        public int retry_count { get; set; }
    }

    public class Database
    {
        private IDbConnection connection = null!; // Initialized in ConnectDatabase()

        DatabaseConfig? config;
        public DatabaseType databaseType { get; set; }

        // Health state (best-effort; updated by CheckHealth)
        public bool LastHealthOk { get; private set; } = true;
        public string? LastHealthError { get; private set; } = null;

        /// <summary>
        /// Supplies this server's identity for scoping rows in the shared database. Invoked lazily
        /// on the first config read or write, not when it is assigned.
        /// </summary>
        public Func<ScopeResolution>? ScopeProvider { get; set; }

        private string? resolvedScope;

        /// <summary>
        /// This server's scope. A final resolution is cached for the life of the process, so the
        /// scope never changes once it is known. A provisional one (no explicit scope and no port
        /// on the command line, before the server has activated) is used but not cached, so the
        /// hostport convar can still take over after OnMapStart instead of freezing a fallback.
        ///
        /// With no provider at all (outside the game server) this is the legacy scope, matching
        /// the pre-scoping plugin. A provider error never degrades to the legacy scope, because
        /// that row is shared by every server on the database.
        /// </summary>
        public string ServerScope
        {
            get
            {
                if (resolvedScope != null) return resolvedScope;

                if (ScopeProvider == null)
                {
                    resolvedScope = ServerIdentity.LegacyScope;
                    return resolvedScope;
                }

                try
                {
                    ScopeResolution resolution = ScopeProvider.Invoke();
                    if (resolution.IsFinal) resolvedScope = resolution.Scope;
                    return resolution.Scope;
                }
                catch (Exception ex)
                {
                    Log($"[ServerScope] Error resolving server scope: {ex.Message}");
                    return ServerIdentity.Sanitize($"{Environment.MachineName}:pid-{Environment.ProcessId}");
                }
            }
        }

        private bool IsSqlite => connection is SqliteConnection;

        public void InitializeDatabase(string directory)
        {
            ConnectDatabase(directory);
            try
            {
                connection.Open();
                string dbType = (connection is SqliteConnection) ? "SQLite" : "MySQL";
                Log($"[InitializeDatabase] {dbType} Database connection successful");

                // Create the `matchzy_stats_matches`, `matchzy_stats_players` and `matchzy_stats_maps` tables if they doesn't exist
                if (connection is SqliteConnection) {
                    CreateRequiredTablesSQLite();
                } else {
                    CreateRequiredTablesSQL();
                }

                Log("[InitializeDatabase] Table matchzy_stats_matches created (or already exists)");
                Log("[InitializeDatabase] Table matchzy_stats_players created (or already exists)");
                Log("[InitializeDatabase] Table matchzy_stats_maps created (or already exists)");
                
                // Create (or migrate) the server config table for persistent configuration.
                // Both tables below hold per-server state and are scoped by server identity so
                // several servers can share one database; see PersistentConfigStore.
                PersistentConfigStore.EnsureConfigSchema(connection, IsSqlite, Log);
                Log("[InitializeDatabase] Table matchzy_server_config created (or already exists)");

                // Create event queue table for reliable event delivery
                if (connection is SqliteConnection) {
                    CreateEventQueueTableSQLite();
                } else {
                    CreateEventQueueTableSQL();
                }
                PersistentConfigStore.EnsureEventQueueSchema(connection, IsSqlite, Log);
                Log("[InitializeDatabase] Table matchzy_event_queue created (or already exists)");
            }
            catch (Exception ex)
            {
                Log($"[InitializeDatabase - FATAL] Database connection or table creation error: {ex.Message}");
                LastHealthOk = false;
                LastHealthError = ex.Message;
                if (config != null && databaseType == DatabaseType.MySQL)
                {
                    string maskedPassword = string.IsNullOrEmpty(config.MySqlPassword) ? "(empty)" : "***";
                    Log($"[InitializeDatabase - FATAL] Connection details - Host: {config.MySqlHost ?? "(null)"}, Port: {config.MySqlPort ?? 3306}, Database: {config.MySqlDatabase ?? "(null)"}, User: {config.MySqlUsername ?? "(null)"}, Password: {maskedPassword}");
                }
                else if (databaseType == DatabaseType.SQLite)
                {
                    string dbPath = Path.Join(directory, "matchzy.db");
                    Log($"[InitializeDatabase - FATAL] SQLite database path: {dbPath}");
                }
            }
        }

        /// <summary>
        /// Best-effort DB health check. Runs a trivial query and returns current status.
        /// This is used for server health reporting to external controllers.
        /// </summary>
        public (bool ok, string dbType, string? error) CheckHealth()
        {
            string dbType = (connection is SqliteConnection) ? "sqlite" : "mysql";
            try
            {
                if (connection.State == ConnectionState.Closed)
                {
                    connection.Open();
                }

                // Use a trivial query that should work on both SQLite and MySQL.
                connection.ExecuteScalar<int>("SELECT 1");

                LastHealthOk = true;
                LastHealthError = null;
                return (true, dbType, null);
            }
            catch (Exception ex)
            {
                LastHealthOk = false;
                LastHealthError = ex.Message;
                return (false, dbType, ex.Message);
            }
            finally
            {
                try
                {
                    if (connection != null && connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        public void ConnectDatabase(string directory)
        {
            try
            {
                SetDatabaseConfig(directory);

                if (databaseType == DatabaseType.SQLite)
                {
                    string dbPath = Path.Join(directory, "matchzy.db");
                    connection = new SqliteConnection($"Data Source={dbPath}");
                    Log($"[ConnectDatabase] Using SQLite database: {dbPath}");
                }
                else if (config != null && databaseType == DatabaseType.MySQL)
                {
                    // Build connection string with timeout settings
                    // ConnectionTimeout: Time to wait for initial connection (default 15s, we'll use 10s)
                    // DefaultCommandTimeout: Time to wait for commands to execute (default 30s, we'll use 15s)
                    string connectionString = $"Server={config.MySqlHost};Port={config.MySqlPort};Database={config.MySqlDatabase};User Id={config.MySqlUsername};Password={config.MySqlPassword};Connection Timeout=10;Default Command Timeout=15;";
                    connection = new MySqlConnection(connectionString);
                    
                    // Log connection details (mask password for security)
                    string maskedPassword = string.IsNullOrEmpty(config.MySqlPassword) ? "(empty)" : "***";
                    Log($"[ConnectDatabase] Attempting MySQL connection - Host: {config.MySqlHost ?? "(null)"}, Port: {config.MySqlPort ?? 3306}, Database: {config.MySqlDatabase ?? "(null)"}, User: {config.MySqlUsername ?? "(null)"}, Password: {maskedPassword}, Connection Timeout: 10s, Command Timeout: 15s");
                }
                else
                {
                    Log($"[ConnectDatabase] Invalid database specified, using SQLite.");
                    connection = new SqliteConnection($"Data Source={Path.Join(directory, "matchzy.db")}");
                    databaseType = DatabaseType.SQLite;
                }
            } 
            catch (Exception ex)
            {
                Log($"[ConnectDatabase - FATAL] Database connection error: {ex.Message}");
                if (config != null && databaseType == DatabaseType.MySQL)
                {
                    string maskedPassword = string.IsNullOrEmpty(config.MySqlPassword) ? "(empty)" : "***";
                    Log($"[ConnectDatabase - FATAL] Connection details - Host: {config.MySqlHost ?? "(null)"}, Port: {config.MySqlPort ?? 3306}, Database: {config.MySqlDatabase ?? "(null)"}, User: {config.MySqlUsername ?? "(null)"}, Password: {maskedPassword}");
                }
            }

        }

        public void CreateRequiredTablesSQLite()
        {
            connection.Execute($@"
            CREATE TABLE IF NOT EXISTS matchzy_stats_matches (
                matchid INTEGER PRIMARY KEY AUTOINCREMENT,
                start_time DATETIME NOT NULL,
                end_time DATETIME DEFAULT NULL,
                winner TEXT NOT NULL DEFAULT '',
                series_type TEXT NOT NULL DEFAULT '',
                team1_name TEXT NOT NULL DEFAULT '',
                team1_score INTEGER NOT NULL DEFAULT 0,
                team2_name TEXT NOT NULL DEFAULT '',
                team2_score INTEGER NOT NULL DEFAULT 0,
                server_ip TEXT NOT NULL DEFAULT '0'
            )");

            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS matchzy_stats_maps (
                    matchid INTEGER NOT NULL,
                    mapnumber INTEGER NOT NULL,
                    start_time DATETIME NOT NULL,
                    end_time DATETIME DEFAULT NULL,
                    winner TEXT NOT NULL DEFAULT '',
                    mapname TEXT NOT NULL DEFAULT '',
                    team1_score INTEGER NOT NULL DEFAULT 0,
                    team2_score INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (matchid, mapnumber),
                    FOREIGN KEY (matchid) REFERENCES matchzy_stats_matches (matchid)
                )");

            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS matchzy_stats_players (
                    matchid INTEGER NOT NULL,
                    mapnumber INTEGER NOT NULL,
                    steamid64 INTEGER NOT NULL,
                    team TEXT NOT NULL DEFAULT '',
                    name TEXT NOT NULL,
                    kills INTEGER NOT NULL,
                    deaths INTEGER NOT NULL,
                    damage INTEGER NOT NULL,
                    assists INTEGER NOT NULL,
                    enemy5ks INTEGER NOT NULL,
                    enemy4ks INTEGER NOT NULL,
                    enemy3ks INTEGER NOT NULL,
                    enemy2ks INTEGER NOT NULL,
                    utility_count INTEGER NOT NULL,
                    utility_damage INTEGER NOT NULL,
                    utility_successes INTEGER NOT NULL,
                    utility_enemies INTEGER NOT NULL,
                    flash_count INTEGER NOT NULL,
                    flash_successes INTEGER NOT NULL,
                    health_points_removed_total INTEGER NOT NULL,
                    health_points_dealt_total INTEGER NOT NULL,
                    shots_fired_total INTEGER NOT NULL,
                    shots_on_target_total INTEGER NOT NULL,
                    v1_count INTEGER NOT NULL,
                    v1_wins INTEGER NOT NULL,
                    v2_count INTEGER NOT NULL,
                    v2_wins INTEGER NOT NULL,
                    entry_count INTEGER NOT NULL,
                    entry_wins INTEGER NOT NULL,
                    equipment_value INTEGER NOT NULL,
                    money_saved INTEGER NOT NULL,
                    kill_reward INTEGER NOT NULL,
                    live_time INTEGER NOT NULL,
                    head_shot_kills INTEGER NOT NULL,
                    cash_earned INTEGER NOT NULL,
                    enemies_flashed INTEGER NOT NULL,
                    PRIMARY KEY (matchid, mapnumber, steamid64),
                    FOREIGN KEY (matchid) REFERENCES matchzy_stats_matches (matchid),
                    FOREIGN KEY (matchid, mapnumber) REFERENCES matchzy_stats_maps (matchid, mapnumber)
                )");
        }

        public void CreateRequiredTablesSQL()
        {
            connection.Execute($@"
                CREATE TABLE IF NOT EXISTS matchzy_stats_matches (
                    matchid INT PRIMARY KEY AUTO_INCREMENT,
                    start_time DATETIME NOT NULL,
                    end_time DATETIME DEFAULT NULL,
                    winner VARCHAR(255) NOT NULL DEFAULT '',
                    series_type VARCHAR(255) NOT NULL DEFAULT '',
                    team1_name VARCHAR(255) NOT NULL DEFAULT '',
                    team1_score INT NOT NULL DEFAULT 0,
                    team2_name VARCHAR(255) NOT NULL DEFAULT '',
                    team2_score INT NOT NULL DEFAULT 0,
                    server_ip VARCHAR(255) NOT NULL DEFAULT '0'
                )");
                
            connection.Execute($@"
            CREATE TABLE IF NOT EXISTS matchzy_stats_maps (
                matchid INT NOT NULL,
                mapnumber TINYINT(3) UNSIGNED NOT NULL,
                start_time DATETIME NOT NULL,
                end_time DATETIME DEFAULT NULL,
                winner VARCHAR(16) NOT NULL DEFAULT '',
                mapname VARCHAR(64) NOT NULL DEFAULT '',
                team1_score INT NOT NULL DEFAULT 0,
                team2_score INT NOT NULL DEFAULT 0,
                PRIMARY KEY (matchid, mapnumber),
                INDEX mapnumber_index (mapnumber),
                CONSTRAINT matchzy_stats_maps_matchid FOREIGN KEY (matchid) REFERENCES matchzy_stats_matches (matchid)
            )");

            connection.Execute($@"
            CREATE TABLE IF NOT EXISTS matchzy_stats_players (
                matchid INT NOT NULL,
                mapnumber TINYINT(3) UNSIGNED NOT NULL,
                steamid64 BIGINT NOT NULL,
                team VARCHAR(255) NOT NULL DEFAULT '',
                name VARCHAR(255) NOT NULL,
                kills INT NOT NULL,
                deaths INT NOT NULL,
                damage INT NOT NULL,
                assists INT NOT NULL,
                enemy5ks INT NOT NULL,
                enemy4ks INT NOT NULL,
                enemy3ks INT NOT NULL,
                enemy2ks INT NOT NULL,
                utility_count INT NOT NULL,
                utility_damage INT NOT NULL,
                utility_successes INT NOT NULL,
                utility_enemies INT NOT NULL,
                flash_count INT NOT NULL,
                flash_successes INT NOT NULL,
                health_points_removed_total INT NOT NULL,
                health_points_dealt_total INT NOT NULL,
                shots_fired_total INT NOT NULL,
                shots_on_target_total INT NOT NULL,
                v1_count INT NOT NULL,
                v1_wins INT NOT NULL,
                v2_count INT NOT NULL,
                v2_wins INT NOT NULL,
                entry_count INT NOT NULL,
                entry_wins INT NOT NULL,
                equipment_value INT NOT NULL,
                money_saved INT NOT NULL,
                kill_reward INT NOT NULL,
                live_time INT NOT NULL,
                head_shot_kills INT NOT NULL,
                cash_earned INT NOT NULL,
                enemies_flashed INT NOT NULL,
                PRIMARY KEY (matchid, mapnumber, steamid64),
                CONSTRAINT fk_player_map_ref FOREIGN KEY (matchid, mapnumber) 
                    REFERENCES matchzy_stats_maps (matchid, mapnumber)
            )");
        }

        public void CreateEventQueueTableSQLite()
        {
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS matchzy_event_queue (
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
                    error_message TEXT,
                    server_scope TEXT NOT NULL DEFAULT ''
                )");
            
            // Create index for efficient querying of pending events
            connection.Execute(@"
                CREATE INDEX IF NOT EXISTS idx_event_queue_status_retry 
                ON matchzy_event_queue(status, next_retry)");
        }

        public void CreateEventQueueTableSQL()
        {
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS matchzy_event_queue (
                    id INT PRIMARY KEY AUTO_INCREMENT,
                    event_type VARCHAR(100) NOT NULL,
                    event_data TEXT NOT NULL,
                    match_id INT,
                    map_number INT DEFAULT 0,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    retry_count INT DEFAULT 0,
                    last_retry TIMESTAMP NULL,
                    next_retry TIMESTAMP NULL,
                    status VARCHAR(20) DEFAULT 'pending',
                    error_message TEXT,
                    server_scope VARCHAR(190) NOT NULL DEFAULT '',
                    INDEX idx_status_retry (status, next_retry),
                    INDEX idx_event_queue_scope_status (server_scope, status, next_retry)
                )");
        }

        /// <summary>
        /// Loads a configuration value from the database.
        /// Prefers this server's scoped row and falls back to the legacy (pre-scoping) row, so a
        /// single-server install keeps reading the values it already had.
        /// </summary>
        public string? LoadConfigValue(string key)
        {
            try
            {
                if (connection.State == ConnectionState.Closed)
                {
                    connection.Open();
                }
                var result = PersistentConfigStore.LoadConfigValue(connection, key, ServerScope);
                if (connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }
                return result;
            }
            catch (Exception ex)
            {
                Log($"[LoadConfigValue] Error loading config key '{key}': {ex.Message}");
                LogConnectionDetails("LoadConfigValue");
                // Ensure connection is closed on error
                try
                {
                    if (connection != null && connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }
                catch { }
                return null;
            }
        }

        /// <summary>
        /// Queues a failed event for retry
        /// </summary>
        public void QueueEvent(string eventType, string eventData, long matchId, int mapNumber, string errorMessage = "")
        {
            try
            {
                if (connection.State == ConnectionState.Closed)
                {
                    connection.Open();
                }
                
                // Calculate next retry time using exponential backoff (start with 30 seconds)
                string nextRetryExpression = (connection is SqliteConnection) 
                    ? "datetime('now', '+30 seconds')" 
                    : "DATE_ADD(NOW(), INTERVAL 30 SECOND)";
                
                // Stamped with this server's scope so a shared database does not let one server
                // retry another server's events against the wrong remote log URL and headers.
                connection.Execute($@"
                    INSERT INTO matchzy_event_queue
                    (event_type, event_data, match_id, map_number, error_message, next_retry, server_scope)
                    VALUES (@EventType, @EventData, @MatchId, @MapNumber, @ErrorMessage, {nextRetryExpression}, @Scope)",
                    new { EventType = eventType, EventData = eventData, MatchId = matchId, MapNumber = mapNumber, ErrorMessage = errorMessage, Scope = ServerScope }
                );

                if (connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }
                Log($"[QueueEvent] Queued {eventType} event for matchId={matchId} (will retry in 30s)");
            }
            catch (Exception ex)
            {
                Log($"[QueueEvent] Error queueing event: {ex.Message}");
                LogConnectionDetails("QueueEvent");
                // Ensure connection is closed on error
                try
                {
                    if (connection != null && connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Gets pending events ready for retry
        /// </summary>
        public List<QueuedEvent> GetPendingEvents(int limit = 50)
        {
            try
            {
                if (connection.State == ConnectionState.Closed)
                {
                    connection.Open();
                }
                
                string nowExpression = (connection is SqliteConnection) ? "datetime('now')" : "NOW()";
                
                var events = connection.Query<QueuedEvent>($@"
                    SELECT id, event_type, event_data, match_id, map_number, retry_count
                    FROM matchzy_event_queue
                    WHERE status = 'pending'
                    AND {PersistentConfigStore.PendingEventsScopeClause}
                    AND (next_retry IS NULL OR next_retry <= {nowExpression})
                    AND retry_count < 20
                    ORDER BY created_at ASC
                    LIMIT {limit}
                ", new { Scope = ServerScope, LegacyScope = ServerIdentity.LegacyScope }).ToList();
                
                if (connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }
                return events;
            }
            catch (Exception ex)
            {
                Log($"[GetPendingEvents] Error: {ex.Message}");
                LogConnectionDetails("GetPendingEvents");
                // Ensure connection is closed on error
                try
                {
                    if (connection != null && connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }
                catch { }
                return new List<QueuedEvent>();
            }
        }

        /// <summary>
        /// Marks an event as successfully sent
        /// </summary>
        public void MarkEventSent(int eventId)
        {
            try
            {
                if (connection.State == ConnectionState.Closed)
                {
                    connection.Open();
                }
                connection.Execute(@"
                    UPDATE matchzy_event_queue 
                    SET status = 'sent' 
                    WHERE id = @Id",
                    new { Id = eventId }
                );
                if (connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }
                Log($"[MarkEventSent] Event {eventId} marked as sent successfully.");
            }
            catch (Exception ex)
            {
                Log($"[MarkEventSent] Error: {ex.Message}");
                LogConnectionDetails("MarkEventSent");
                // Ensure connection is closed on error
                try
                {
                    if (connection != null && connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Updates event with retry info and schedules next retry using exponential backoff
        /// </summary>
        public void MarkEventRetry(int eventId, int retryCount, string errorMessage = "")
        {
            try
            {
                if (connection.State == ConnectionState.Closed)
                {
                    connection.Open();
                }
                
                // Exponential backoff: 30s, 1m, 2m, 4m, 8m, 16m, 32m (capped at 32 minutes)
                int delaySeconds = Math.Min(30 * (1 << retryCount), 1920);
                
                string nextRetryExpression = (connection is SqliteConnection)
                    ? $"datetime('now', '+{delaySeconds} seconds')"
                    : $"DATE_ADD(NOW(), INTERVAL {delaySeconds} SECOND)";
                    
                string lastRetryExpression = (connection is SqliteConnection)
                    ? "datetime('now')"
                    : "NOW()";
                
                if (retryCount >= 20)
                {
                    // Max retries reached, mark as failed
                    connection.Execute(@"
                        UPDATE matchzy_event_queue 
                        SET retry_count = @RetryCount, 
                            last_retry = " + lastRetryExpression + @",
                            status = 'failed',
                            error_message = @ErrorMessage
                        WHERE id = @Id",
                        new { Id = eventId, RetryCount = retryCount, ErrorMessage = errorMessage }
                    );
                    Log($"[MarkEventRetry] Event {eventId} reached max retries (20), marked as failed.");
                }
                else
                {
                    connection.Execute($@"
                        UPDATE matchzy_event_queue 
                        SET retry_count = @RetryCount, 
                            last_retry = {lastRetryExpression},
                            next_retry = {nextRetryExpression},
                            error_message = @ErrorMessage
                        WHERE id = @Id",
                        new { Id = eventId, RetryCount = retryCount, ErrorMessage = errorMessage }
                    );
                    Log($"[MarkEventRetry] Event {eventId} retry scheduled in {delaySeconds}s (attempt {retryCount + 1})");
                }
                
                if (connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }
            }
            catch (Exception ex)
            {
                Log($"[MarkEventRetry] Error: {ex.Message}");
                LogConnectionDetails("MarkEventRetry");
                // Ensure connection is closed on error
                try
                {
                    if (connection != null && connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Cleans up old successfully sent events (older than 7 days)
        /// </summary>
        public void CleanupOldEvents()
        {
            try
            {
                if (connection.State == ConnectionState.Closed)
                {
                    connection.Open();
                }
                
                string dateExpression = (connection is SqliteConnection)
                    ? "datetime('now', '-7 days')"
                    : "DATE_SUB(NOW(), INTERVAL 7 DAY)";
                
                // Deliberately unscoped: these events have already been delivered, so removing
                // them is pure housekeeping. Keeping it global also stops a decommissioned
                // server's rows accumulating forever in a shared database.
                int deleted = connection.Execute($@"
                    DELETE FROM matchzy_event_queue
                    WHERE status = 'sent'
                    AND created_at < {dateExpression}
                ");
                
                if (connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }
                
                if (deleted > 0)
                {
                    Log($"[CleanupOldEvents] Cleaned up {deleted} old sent events.");
                }
            }
            catch (Exception ex)
            {
                Log($"[CleanupOldEvents] Error: {ex.Message}");
                LogConnectionDetails("CleanupOldEvents");
                // Ensure connection is closed on error
                try
                {
                    if (connection != null && connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Clears all pending/failed events from the retry queue
        /// </summary>
        public int ClearEventQueue()
        {
            try
            {
                if (connection.State == ConnectionState.Closed)
                {
                    connection.Open();
                }
                
                // Only this server's (and pre-scoping legacy) events. Clearing the queue happens
                // when this server's remote log URL changes, which says nothing about the events
                // another server on the same database still needs to send.
                int deleted = connection.Execute($@"
                    DELETE FROM matchzy_event_queue
                    WHERE status IN ('pending', 'failed')
                    AND {PersistentConfigStore.PendingEventsScopeClause}
                ", new { Scope = ServerScope, LegacyScope = ServerIdentity.LegacyScope });

                if (connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }

                Log($"[ClearEventQueue] Cleared {deleted} pending/failed events from queue.");
                return deleted;
            }
            catch (Exception ex)
            {
                Log($"[ClearEventQueue] Error: {ex.Message}");
                LogConnectionDetails("ClearEventQueue");
                // Ensure connection is closed on error
                try
                {
                    if (connection != null && connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }
                catch { }
                return 0;
            }
        }

        /// <summary>
        /// Saves a configuration value to the database (insert or update).
        /// Always writes this server's scoped row; the legacy row is never overwritten, so one
        /// server writing can no longer change what another server loads.
        /// </summary>
        public void SaveConfigValue(string key, string value)
        {
            try
            {
                if (connection.State == ConnectionState.Closed)
                {
                    connection.Open();
                }

                PersistentConfigStore.SaveConfigValue(connection, IsSqlite, key, value, ServerScope);

                if (connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }
                Log($"[SaveConfigValue] Saved config for server '{ServerScope}': {key} = {value}");
            }
            catch (Exception ex)
            {
                Log($"[SaveConfigValue] Error saving config key '{key}': {ex.Message}");
                LogConnectionDetails("SaveConfigValue");
                // Ensure connection is closed on error
                try
                {
                    if (connection != null && connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Gets complete match statistics from database for API pull requests
        /// </summary>
        public string? GetMatchStatsJson(long matchId)
        {
            try
            {
                if (connection.State == ConnectionState.Closed)
                {
                    connection.Open();
                }
                
                // Get match info
                var match = connection.QueryFirstOrDefault<dynamic>(@"
                    SELECT * FROM matchzy_stats_matches 
                    WHERE matchid = @MatchId",
                    new { MatchId = matchId }
                );
                
                if (match == null)
                {
                    if (connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                    return null;
                }
                
                // Get map info
                var maps = connection.Query<dynamic>(@"
                    SELECT * FROM matchzy_stats_maps 
                    WHERE matchid = @MatchId
                    ORDER BY mapnumber",
                    new { MatchId = matchId }
                ).ToList();
                
                // Get player stats
                var players = connection.Query<dynamic>(@"
                    SELECT * FROM matchzy_stats_players 
                    WHERE matchid = @MatchId
                    ORDER BY mapnumber, team, name",
                    new { MatchId = matchId }
                ).ToList();
                
                if (connection.State == ConnectionState.Open)
                {
                    connection.Close();
                }
                
                // Build JSON structure
                var result = new
                {
                    match = match,
                    maps = maps,
                    players = players
                };
                
                return JsonSerializer.Serialize(result);
            }
            catch (Exception ex)
            {
                Log($"[GetMatchStatsJson] Error: {ex.Message}");
                LogConnectionDetails("GetMatchStatsJson");
                // Ensure connection is closed on error
                try
                {
                    if (connection != null && connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }
                catch { }
                return null;
            }
        }

        public long InitMatch(string team1name, string team2name, string serverIp, bool isMatchSetup, long liveMatchId, int mapNumber, string seriesType, MatchConfig matchConfig)
        {
            try
            {
                string mapName = isMatchSetup ? matchConfig.Maplist[mapNumber] : Server.MapName;
                string dateTimeExpression = (connection is SqliteConnection) ? "datetime('now')" : "NOW()";

                if (mapNumber == 0) {
                    if (isMatchSetup && liveMatchId != -1) {
                        connection.Execute(@"
                            INSERT INTO matchzy_stats_matches (matchid, start_time, team1_name, team2_name, series_type, server_ip)
                            VALUES (@liveMatchId, " + dateTimeExpression + ", @team1name, @team2name, @seriesType, @serverIp)",
                            new { liveMatchId, team1name, team2name, seriesType, serverIp });
                    } else {
                        connection.Execute(@"
                            INSERT INTO matchzy_stats_matches (start_time, team1_name, team2_name, series_type, server_ip)
                            VALUES (" + dateTimeExpression + ", @team1name, @team2name, @seriesType, @serverIp)",
                            new { team1name, team2name, seriesType, serverIp });
                    }
                }

                if (isMatchSetup && liveMatchId != -1) {
                    connection.Execute(@"
                        INSERT INTO matchzy_stats_maps (matchid, start_time, mapnumber, mapname)
                        VALUES (@liveMatchId, " + dateTimeExpression + ", @mapNumber, @mapName)",
                        new { liveMatchId, mapNumber, mapName });
                    return liveMatchId;
                }

                // Retrieve the last inserted match_id
                long matchId = -1;
                if (connection is SqliteConnection)
                {
                    matchId = connection.ExecuteScalar<long>("SELECT last_insert_rowid()");
                }
                else if (connection is MySqlConnection)
                {
                    matchId = connection.ExecuteScalar<long>("SELECT LAST_INSERT_ID()");
                }

                connection.Execute(@"
                    INSERT INTO matchzy_stats_maps (matchid, start_time, mapnumber, mapname)
                    VALUES (@matchId, " + dateTimeExpression + ", @mapNumber, @mapName)",
                    new { matchId, mapNumber, mapName });

                Log($"[InsertMatchData] Data inserted into matchzy_stats_matches with match_id: {matchId}");
                return matchId;
            }
            catch (Exception ex)
            {
                Log($"[InsertMatchData - FATAL] Error inserting data: {ex.Message}");
                return liveMatchId;
            }
        }

        public void UpdateTeamData(int matchId, string team1name, string team2name) {
            try
            {
                connection.Execute(@"
                    UPDATE matchzy_stats_matches
                    SET team1_name = @team1name, team2_name = @team2name
                    WHERE matchid = @matchId",
                    new { matchId, team1name, team2name });

                Log($"[UpdateTeamData] Data updated for matchId: {matchId} team1name: {team1name} team2name: {team2name}");
            }
            catch (Exception ex)
            {
                Log($"[UpdateTeamData - FATAL] Error updating data of matchId: {matchId} [ERROR]: {ex.Message}");
            }
        }

        public async Task SetMapEndData(long matchId, int mapNumber, string winnerName, int t1score, int t2score, int team1SeriesScore, int team2SeriesScore)
        {
            try
            {
                string dateTimeExpression = (connection is SqliteConnection) ? "datetime('now')" : "NOW()";

                string sqlQuery = $@"
                    UPDATE matchzy_stats_maps
                    SET winner = @winnerName, end_time = {dateTimeExpression}, team1_score = @t1score, team2_score = @t2score
                    WHERE matchid = @matchId AND mapNumber = @mapNumber";

                await connection.ExecuteAsync(sqlQuery, new { matchId, winnerName, t1score, t2score, mapNumber });

                sqlQuery = $@"
                    UPDATE matchzy_stats_matches
                    SET team1_score = @team1SeriesScore, team2_score = @team2SeriesScore
                    WHERE matchid = @matchId";

                await connection.ExecuteAsync(sqlQuery, new { matchId, team1SeriesScore, team2SeriesScore });

                Log($"[SetMapEndData] Data updated for matchId: {matchId} mapNumber: {mapNumber} winnerName: {winnerName}");
            }
            catch (Exception ex)
            {
                Log($"[SetMapEndData - FATAL] Error updating data of matchId: {matchId} mapNumber: {mapNumber} [ERROR]: {ex.Message}");
            } 
        }

        public async Task SetMatchEndData(long matchId, string winnerName, int t1score, int t2score)
        {
            try
            {
                string dateTimeExpression = (connection is SqliteConnection) ? "datetime('now')" : "NOW()";

                string sqlQuery = $@"
                    UPDATE matchzy_stats_matches
                    SET winner = @winnerName, end_time = {dateTimeExpression}, team1_score = @t1score, team2_score = @t2score
                    WHERE matchid = @matchId";

                await connection.ExecuteAsync(sqlQuery, new { matchId, winnerName, t1score, t2score });

                Log($"[SetMatchEndData] Data updated for matchId: {matchId} winnerName: {winnerName}");
            }
            catch (Exception ex)
            {
                Log($"[SetMatchEndData - FATAL] Error updating data of matchId: {matchId} [ERROR]: {ex.Message}");
            }
        }

        public async Task UpdateMapStatsAsync(long matchId, int mapNumber, int t1score, int t2score)
        {
            try
            {
                string sqlQuery = $@"
                    UPDATE matchzy_stats_maps
                    SET team1_score = @t1score, team2_score = @t2score
                    WHERE matchid = @matchId AND mapnumber = @mapNumber";

                await connection.ExecuteAsync(sqlQuery, new { matchId, mapNumber, t1score, t2score });
            }
            catch (Exception ex)
            {
                Log($"[UpdatePlayerStats - FATAL] Error updating data of matchId: {matchId} [ERROR]: {ex.Message}");
            }
        }

        public async Task UpdatePlayerStatsAsync(long matchId, int mapNumber, Dictionary<ulong, Dictionary<string, object>> playerStatsDictionary)
        {
            try
            {
                foreach (ulong steamid64 in playerStatsDictionary.Keys)
                {
                    Log($"[UpdatePlayerStats] Going to update data for Match: {matchId}, MapNumber: {mapNumber}, Player: {steamid64}");

                    var playerStats = playerStatsDictionary[steamid64];

                    string sqlQuery = $@"
                    INSERT INTO matchzy_stats_players (
                        matchid, mapnumber, steamid64, team, name, kills, deaths, damage, assists,
                        enemy5ks, enemy4ks, enemy3ks, enemy2ks, utility_count, utility_damage,
                        utility_successes, utility_enemies, flash_count, flash_successes,
                        health_points_removed_total, health_points_dealt_total, shots_fired_total,
                        shots_on_target_total, v1_count, v1_wins, v2_count, v2_wins, entry_count, entry_wins,
                        equipment_value, money_saved, kill_reward, live_time, head_shot_kills,
                        cash_earned, enemies_flashed)
                    VALUES (
                        @matchId, @mapNumber, @steamid64, @team, @name, @kills, @deaths, @damage, @assists,
                        @enemy5ks, @enemy4ks, @enemy3ks, @enemy2ks, @utility_count, @utility_damage,
                        @utility_successes, @utility_enemies, @flash_count, @flash_successes,
                        @health_points_removed_total, @health_points_dealt_total, @shots_fired_total,
                        @shots_on_target_total, @v1_count, @v1_wins, @v2_count, @v2_wins, @entry_count,
                        @entry_wins, @equipment_value, @money_saved, @kill_reward, @live_time,
                        @head_shot_kills, @cash_earned, @enemies_flashed)
                    ON DUPLICATE KEY UPDATE
                        team = @team, name = @name, kills = @kills, deaths = @deaths, damage = @damage,
                        assists = @assists, enemy5ks = @enemy5ks, enemy4ks = @enemy4ks, enemy3ks = @enemy3ks,
                        enemy2ks = @enemy2ks, utility_count = @utility_count, utility_damage = @utility_damage,
                        utility_successes = @utility_successes, utility_enemies = @utility_enemies,
                        flash_count = @flash_count, flash_successes = @flash_successes,
                        health_points_removed_total = @health_points_removed_total,
                        health_points_dealt_total = @health_points_dealt_total,
                        shots_fired_total = @shots_fired_total, shots_on_target_total = @shots_on_target_total,
                        v1_count = @v1_count, v1_wins = @v1_wins, v2_count = @v2_count, v2_wins = @v2_wins,
                        entry_count = @entry_count, entry_wins = @entry_wins,
                        equipment_value = @equipment_value, money_saved = @money_saved,
                        kill_reward = @kill_reward, live_time = @live_time, head_shot_kills = @head_shot_kills,
                        cash_earned = @cash_earned, enemies_flashed = @enemies_flashed";

                    if (connection is SqliteConnection) {
                        sqlQuery = @"
                        INSERT OR REPLACE INTO matchzy_stats_players (
                            matchid, mapnumber, steamid64, team, name, kills, deaths, damage, assists,
                            enemy5ks, enemy4ks, enemy3ks, enemy2ks, utility_count, utility_damage,
                            utility_successes, utility_enemies, flash_count, flash_successes,
                            health_points_removed_total, health_points_dealt_total, shots_fired_total,
                            shots_on_target_total, v1_count, v1_wins, v2_count, v2_wins, entry_count, entry_wins,
                            equipment_value, money_saved, kill_reward, live_time, head_shot_kills,
                            cash_earned, enemies_flashed)
                        VALUES (
                            @matchId, @mapNumber, @steamid64, @team, @name, @kills, @deaths, @damage, @assists,
                            @enemy5ks, @enemy4ks, @enemy3ks, @enemy2ks, @utility_count, @utility_damage,
                            @utility_successes, @utility_enemies, @flash_count, @flash_successes,
                            @health_points_removed_total, @health_points_dealt_total, @shots_fired_total,
                            @shots_on_target_total, @v1_count, @v1_wins, @v2_count, @v2_wins, @entry_count,
                            @entry_wins, @equipment_value, @money_saved, @kill_reward, @live_time,
                            @head_shot_kills, @cash_earned, @enemies_flashed)";
                    }

                    await connection.ExecuteAsync(sqlQuery,
                        new
                        {
                            matchId,
                            mapNumber,
                            steamid64,
                            team = playerStats["TeamName"],
                            name = playerStats["PlayerName"],
                            kills = playerStats["Kills"],
                            deaths = playerStats["Deaths"],
                            damage = playerStats["Damage"],
                            assists = playerStats["Assists"],
                            enemy5ks = playerStats["Enemy5Ks"],
                            enemy4ks = playerStats["Enemy4Ks"],
                            enemy3ks = playerStats["Enemy3Ks"],
                            enemy2ks = playerStats["Enemy2Ks"],
                            utility_count = playerStats["UtilityCount"],
                            utility_damage = playerStats["UtilityDamage"],
                            utility_successes = playerStats["UtilitySuccess"],
                            utility_enemies = playerStats["UtilityEnemies"],
                            flash_count = playerStats["FlashCount"],
                            flash_successes = playerStats["FlashSuccess"],
                            health_points_removed_total = playerStats["HealthPointsRemovedTotal"],
                            health_points_dealt_total = playerStats["HealthPointsDealtTotal"],
                            shots_fired_total = playerStats["ShotsFiredTotal"],
                            shots_on_target_total = playerStats["ShotsOnTargetTotal"],
                            v1_count = playerStats["1v1Count"],
                            v1_wins = playerStats["1v1Wins"],
                            v2_count = playerStats["1v2Count"],
                            v2_wins = playerStats["1v2Wins"],
                            entry_count = playerStats["EntryCount"],
                            entry_wins = playerStats["EntryWins"],
                            equipment_value = playerStats["EquipmentValue"],
                            money_saved = playerStats["MoneySaved"],
                            kill_reward = playerStats["KillReward"],
                            live_time = playerStats["LiveTime"],
                            head_shot_kills = playerStats["HeadShotKills"],
                            cash_earned = playerStats["CashEarned"],
                            enemies_flashed = playerStats["EnemiesFlashed"]
                        });

                    Log($"[UpdatePlayerStats] Data inserted/updated for player {steamid64} in match {matchId}");
                }
            }
            catch (Exception ex)
            {
                Log($"[UpdatePlayerStats - FATAL] Error inserting/updating data: {ex.Message}");
            }
        }

        public async Task WritePlayerStatsToCsv(string filePath, long matchId, int mapNumber)
        {
            try {
                string csvFilePath = $"{filePath}/match_data_map{mapNumber}_{matchId}.csv";
                string? directoryPath = Path.GetDirectoryName(csvFilePath);
                if (directoryPath != null)
                {
                    if (!Directory.Exists(directoryPath))
                    {
                        Directory.CreateDirectory(directoryPath);
                    }
                }

                using (var writer = new StreamWriter(csvFilePath))
                using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)))
                {
                    IEnumerable<dynamic> playerStatsData = await connection.QueryAsync(
                        "SELECT * FROM matchzy_stats_players WHERE matchid = @MatchId AND mapnumber = @MapNumber ORDER BY team, kills DESC", new { MatchId = matchId, MapNumber = mapNumber });

                    // Use the first data row to get the column names
                    dynamic? firstDataRow = playerStatsData.FirstOrDefault();
                    if (firstDataRow != null)
                    {
                        foreach (var propertyName in ((IDictionary<string, object>)firstDataRow).Keys)
                        {
                            csv.WriteField(propertyName);
                        }
                        csv.NextRecord(); // End of the column names row

                        // Write data to the CSV file
                        foreach (var playerStats in playerStatsData)
                        {
                            foreach (var propertyValue in ((IDictionary<string, object>)playerStats).Values)
                            {
                                csv.WriteField(propertyValue);
                            }
                            csv.NextRecord();
                        }
                    }
                }
                Log($"[WritePlayerStatsToCsv] Match stats for ID: {matchId} written successfully at: {csvFilePath}");
            }
            catch (Exception ex)
            {
                Log($"[WritePlayerStatsToCsv - FATAL] Error writing data: {ex.Message}");
            }

        }

        private void CreateDefaultConfigFile(string configFile)
        {
            // Create a default configuration
            DatabaseConfig defaultConfig = new DatabaseConfig
            {
                DatabaseType = "SQLite",
                MySqlHost = "your_mysql_host",
                MySqlDatabase = "your_mysql_database",
                MySqlUsername = "your_mysql_username",
                MySqlPassword = "your_mysql_password",
                MySqlPort = 3306
            };

            // Serialize and save the default configuration to the file
            string defaultConfigJson = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configFile, defaultConfigJson);

            Log($"[InitializeDatabase] Default configuration file created at: {configFile}");
        }

        private void SetDatabaseConfig(string directory)
        {
            string fileName = "database.json";
            string configFile = Path.Combine(Server.GameDirectory + "/csgo/cfg/MatchZy", fileName);
            if (!File.Exists(configFile))
            {
                // Create a default configuration if the file doesn't exist
                Log($"[InitializeDatabase] database.json doesn't exist, creating default!");
                CreateDefaultConfigFile(configFile);
            }

            try
            {
                string jsonContent = File.ReadAllText(configFile);
                config = JsonSerializer.Deserialize<DatabaseConfig>(jsonContent);
                // Set the database type
                if (config != null && config.DatabaseType?.Trim().ToLower() == "mysql") {
                    databaseType = DatabaseType.MySQL;
                } else {
                    databaseType = DatabaseType.SQLite;
                }
                
            }
            catch (JsonException ex)
            {
                Log($"[TryDeserializeConfig - ERROR] Error deserializing database.json: {ex.Message}. Using SQLite DB");
                databaseType = DatabaseType.SQLite;
            }
        }

        private void Log(string message)
        {
            Console.WriteLine("[MatchZy] " + message);
        }

        /// <summary>
        /// Logs connection details for debugging (masks password)
        /// </summary>
        private void LogConnectionDetails(string context)
        {
            if (config != null && databaseType == DatabaseType.MySQL)
            {
                string maskedPassword = string.IsNullOrEmpty(config.MySqlPassword) ? "(empty)" : "***";
                string connectionState = connection != null ? connection.State.ToString() : "null";
                Log($"[{context}] Connection details - Host: {config.MySqlHost ?? "(null)"}, Port: {config.MySqlPort ?? 3306}, Database: {config.MySqlDatabase ?? "(null)"}, User: {config.MySqlUsername ?? "(null)"}, Password: {maskedPassword}, Connection State: {connectionState}");
            }
            else if (databaseType == DatabaseType.SQLite && connection != null)
            {
                Log($"[{context}] SQLite connection state: {connection.State}");
            }
        }

        public enum DatabaseType
        {
            SQLite,
            MySQL
        }
    }

    public class DatabaseConfig
    {
        public string? DatabaseType { get; set; }
        public string? MySqlHost { get; set; }
        public string? MySqlDatabase { get; set; }
        public string? MySqlUsername { get; set; }
        public string? MySqlPassword { get; set; }
        public int? MySqlPort { get; set; }
    }

}

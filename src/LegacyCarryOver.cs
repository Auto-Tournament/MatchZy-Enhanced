using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;

namespace AutoTournamentCS2;

/// <summary>
/// Carries an install of the plugin from before the 2.0.0 rename over to the new names, on the
/// first start of 2.0.0. The old names are never read as settings; this only moves data so an
/// operator's config files, stats and persisted settings survive the upgrade.
///
/// Rules, applied by every step here:
///   - Only moves or renames. Nothing is ever deleted.
///   - Never overwrites: when the new-named file, table or setting already exists, both are left
///     as they are, a warning is logged, and the new one is used.
///   - Every move is logged, so an operator can see exactly what happened.
///   - Safe to run on every start: once the old names are gone there is nothing left to do.
///
/// This file has no CounterStrikeSharp dependency so it can be unit tested.
/// </summary>
public static class LegacyCarryOver
{
    // The pre-2.0.0 names. They appear here, and only here, because this is the code that finds
    // them on disk and in the database.
    public const string OldConfigFolder = "MatchZy";
    public const string OldPluginFolder = "MatchZy";
    public const string OldSqliteFile = "matchzy.db";
    public const string OldTablePrefix = "matchzy_";
    public const string OldConfigKeyPrefix = "matchzy_";

    public const string NewConfigFolder = "AutoTournamentCS2";
    public const string NewSqliteFile = "auto_tournament_cs2.db";
    public const string NewTablePrefix = "at_";
    public const string NewConfigKeyPrefix = "at_";

    /// <summary>The plugin's tables, without prefix.</summary>
    public static readonly string[] TableSuffixes =
    {
        "stats_matches",
        "stats_maps",
        "stats_players",
        "server_config",
        "event_queue",
    };

    /// <summary>SQLite files that belong to a database file and must travel with it.</summary>
    private static readonly string[] SqliteSidecarSuffixes = { "-wal", "-shm", "-journal" };

    private const string LogPrefix = "[CarryOver] ";

    /// <summary>
    /// Moves <c>cfg/MatchZy/</c> to <c>cfg/AutoTournamentCS2/</c>.
    ///
    /// When the new folder does not exist the whole folder is moved. When it does (the release
    /// zip ships <c>cfg/AutoTournamentCS2/</c>, so extracting it before the first start creates
    /// it) each file is moved on its own when the new folder has no file of that name, and left
    /// in place with a warning when it does.
    /// </summary>
    /// <param name="cfgDirectory">The game's <c>csgo/cfg</c> directory.</param>
    /// <returns>The number of files moved.</returns>
    public static int CarryOverConfigFolder(string cfgDirectory, Action<string> log)
    {
        string oldDir = Path.Combine(cfgDirectory, OldConfigFolder);
        string newDir = Path.Combine(cfgDirectory, NewConfigFolder);
        string oldLabel = $"cfg/{OldConfigFolder}/";
        string newLabel = $"cfg/{NewConfigFolder}/";

        if (!Directory.Exists(oldDir)) return 0;

        List<string> files = Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(oldDir, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (!Directory.Exists(newDir))
        {
            Directory.Move(oldDir, newDir);
            log($"{LogPrefix}Moved {oldLabel} to {newLabel} ({files.Count} file(s)).");
            foreach (string file in files)
            {
                log($"{LogPrefix}  moved {oldLabel}{ToLabel(file)} -> {newLabel}{ToLabel(file)}");
            }
            return files.Count;
        }

        int moved = 0;
        var kept = new List<string>();
        foreach (string file in files)
        {
            string source = Path.Combine(oldDir, file);
            string target = Path.Combine(newDir, file);
            if (File.Exists(target) || Directory.Exists(target))
            {
                kept.Add(file);
                continue;
            }

            string? targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
            File.Move(source, target);
            moved++;
            log($"{LogPrefix}Moved {oldLabel}{ToLabel(file)} -> {newLabel}{ToLabel(file)}");
        }

        if (kept.Count > 0)
        {
            log($"{LogPrefix}WARNING: both {oldLabel} and {newLabel} have {string.Join(", ", kept.Select(ToLabel))}. " +
                $"Using the files in {newLabel}; the ones in {oldLabel} were left untouched. " +
                $"Copy over any changes you want to keep (renaming {OldConfigKeyPrefix}* settings to {NewConfigKeyPrefix}*), then remove {oldLabel}.");
        }

        return moved;
    }

    /// <summary>
    /// Renames the SQLite database <c>matchzy.db</c> to <c>auto_tournament_cs2.db</c> in the
    /// plugin's directory.
    ///
    /// The old file is looked for in the new plugin directory first (an operator who copied it
    /// over) and then in the old plugin directory next to it (<c>plugins/MatchZy/</c>), where
    /// every pre-2.0.0 install kept it.
    /// </summary>
    /// <param name="moduleDirectory">The plugin's own directory (<c>plugins/AutoTournamentCS2</c>).</param>
    /// <returns>True when a file was moved.</returns>
    public static bool CarryOverSqliteFile(string moduleDirectory, Action<string> log)
    {
        string moduleDir = moduleDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string newPath = Path.Combine(moduleDir, NewSqliteFile);

        var candidates = new List<string> { Path.Combine(moduleDir, OldSqliteFile) };
        string? pluginsDir = Path.GetDirectoryName(moduleDir);
        if (!string.IsNullOrEmpty(pluginsDir))
        {
            candidates.Add(Path.Combine(pluginsDir, OldPluginFolder, OldSqliteFile));
        }

        List<string> existing = candidates.Where(File.Exists).ToList();
        if (existing.Count == 0) return false;

        if (File.Exists(newPath))
        {
            foreach (string old in existing)
            {
                log($"{LogPrefix}WARNING: both {old} and {newPath} exist. Using {newPath}; {old} was left untouched.");
            }
            return false;
        }

        string source = existing[0];
        File.Move(source, newPath);
        log($"{LogPrefix}Renamed SQLite database {source} -> {newPath}");

        foreach (string suffix in SqliteSidecarSuffixes)
        {
            string sidecar = source + suffix;
            string sidecarTarget = newPath + suffix;
            if (!File.Exists(sidecar)) continue;
            if (File.Exists(sidecarTarget))
            {
                log($"{LogPrefix}WARNING: both {sidecar} and {sidecarTarget} exist. Left {sidecar} untouched.");
                continue;
            }
            File.Move(sidecar, sidecarTarget);
            log($"{LogPrefix}Renamed {sidecar} -> {sidecarTarget}");
        }

        foreach (string other in existing.Skip(1))
        {
            log($"{LogPrefix}WARNING: {other} was not carried over because {source} was used. It was left untouched.");
        }

        return true;
    }

    /// <summary>
    /// Renames the <c>matchzy_*</c> tables to <c>at_*</c>. A table is renamed only when its old
    /// name exists and its new name does not.
    ///
    /// On MySQL every rename happens in a single <c>RENAME TABLE</c> statement, which is atomic.
    /// On SQLite they run in one transaction. Foreign keys between the tables follow the rename
    /// on both.
    /// </summary>
    /// <returns>The (old, new) pairs that were renamed.</returns>
    public static IReadOnlyList<(string Old, string New)> CarryOverTables(IDbConnection connection, bool isSqlite, Action<string> log)
    {
        var renames = new List<(string Old, string New)>();
        foreach (string suffix in TableSuffixes)
        {
            string oldName = OldTablePrefix + suffix;
            string newName = NewTablePrefix + suffix;
            if (!PersistentConfigStore.TableExists(connection, isSqlite, oldName)) continue;

            if (PersistentConfigStore.TableExists(connection, isSqlite, newName))
            {
                log($"{LogPrefix}WARNING: both tables {oldName} and {newName} exist. Using {newName}; {oldName} was left untouched.");
                continue;
            }

            renames.Add((oldName, newName));
        }

        if (renames.Count == 0) return renames;

        if (isSqlite)
        {
            using IDbTransaction transaction = connection.BeginTransaction();
            try
            {
                foreach (var (oldName, newName) in renames)
                {
                    connection.Execute($"ALTER TABLE {oldName} RENAME TO {newName}", transaction: transaction);
                }
                transaction.Commit();
            }
            catch
            {
                try { transaction.Rollback(); } catch { /* nothing was renamed */ }
                throw;
            }
        }
        else
        {
            // One statement: MySQL renames all of them or none.
            string pairs = string.Join(", ", renames.Select(r => $"`{r.Old}` TO `{r.New}`"));
            connection.Execute($"RENAME TABLE {pairs}");
        }

        foreach (var (oldName, newName) in renames)
        {
            log($"{LogPrefix}Renamed table {oldName} -> {newName}");
        }

        return renames;
    }

    /// <summary>True when an old table exists whose new name does not, i.e. a rename is still due.</summary>
    public static bool HasPendingTables(IDbConnection connection, bool isSqlite) =>
        TableSuffixes.Any(suffix =>
            PersistentConfigStore.TableExists(connection, isSqlite, OldTablePrefix + suffix) &&
            !PersistentConfigStore.TableExists(connection, isSqlite, NewTablePrefix + suffix));

    /// <summary>
    /// Renames persisted settings in <c>at_server_config</c> from <c>matchzy_*</c> keys to the
    /// matching <c>at_*</c> keys, so values the platform set (server id, bootstrap URL and token,
    /// remote log settings, ...) survive the rename. A row is renamed only when that server has
    /// no row under the new key. Run it after the table has its current schema.
    /// </summary>
    /// <returns>The number of rows renamed.</returns>
    public static int CarryOverConfigKeys(IDbConnection connection, bool isSqlite, Action<string> log)
    {
        string table = PersistentConfigStore.ConfigTable;
        string scope = PersistentConfigStore.ScopeColumn;
        if (!PersistentConfigStore.TableExists(connection, isSqlite, table)) return 0;

        // Filtered in memory rather than with LIKE, whose '_' wildcard and escape syntax differ
        // between the two databases. The table holds a few dozen rows per server.
        var rows = connection.Query<(string Scope, string Key)>(
                $"SELECT {scope}, config_key FROM {table}")
            .ToList();

        var existing = new HashSet<(string, string)>(rows);
        int renamed = 0;
        var conflicts = new List<string>();

        foreach (var (rowScope, key) in rows.Where(r => r.Key.StartsWith(OldConfigKeyPrefix, StringComparison.Ordinal)))
        {
            string newKey = NewConfigKeyPrefix + key.Substring(OldConfigKeyPrefix.Length);
            string scopeLabel = rowScope.Length == 0 ? "(shared)" : rowScope;

            if (existing.Contains((rowScope, newKey)))
            {
                conflicts.Add($"{key} for server '{scopeLabel}'");
                continue;
            }

            connection.Execute(
                $"UPDATE {table} SET config_key = @NewKey WHERE {scope} = @Scope AND config_key = @OldKey",
                new { NewKey = newKey, OldKey = key, Scope = rowScope });
            existing.Add((rowScope, newKey));
            renamed++;
            log($"{LogPrefix}Renamed saved setting {key} -> {newKey} for server '{scopeLabel}'");
        }

        if (conflicts.Count > 0)
        {
            log($"{LogPrefix}WARNING: saved settings exist under both the old and the new name: {string.Join(", ", conflicts)}. " +
                $"Using the {NewConfigKeyPrefix}* value; the old rows were left untouched.");
        }

        return renamed;
    }

    private static string ToLabel(string relativePath) => relativePath.Replace(Path.DirectorySeparatorChar, '/');
}

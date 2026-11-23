using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Camzivo.Data.Sqlite;

/// <summary>
/// Applies schema migrations in order, inside a transaction each, after backing up an existing database.
///
/// Two rules that matter operationally:
/// 1. A backup is written <b>only</b> when there is something to migrate. The first version of this class
///    backed up on every launch, which grew %APPDATA% by a copy of the database per start.
/// 2. A database from a newer build is refused instead of being opened and silently mis-read.
/// </summary>
public static class MigrationRunner
{
    public const int CurrentVersion = 6;

    /// <summary>How many automatic backups to keep next to the database.</summary>
    public const int BackupsToKeep = 5;

    public static IReadOnlyList<Migration> All { get; } =
    [
        new Migration(1, "Initial schema: devices, cameras, capabilities, layouts, events, exports, audit", Migrations.V001_InitialSchema.Apply),
        new Migration(2, "Store discovered main and sub stream URIs on the device", Migrations.V002_StreamUris.Apply),
        new Migration(3, "Recorders: device kind and imported channels", Migrations.V003_NvrChannels.Apply),
        new Migration(4, "Allow kept channels to share their recorder's endpoint", Migrations.V004_ChannelDevices.Apply),
        new Migration(5, "Saved camera layouts, local event history and export records", Migrations.V005_OperatorState.Apply),
        new Migration(6, "Recorder identity, diagnostics and Profile G source tokens", Migrations.V006_NvrCompatibility.Apply),
    ];

    public static int ReadUserVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static MigrationResult Migrate(string databasePath, IReadOnlyList<Migration>? migrations = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        migrations ??= All;

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existedBefore = File.Exists(databasePath);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        var currentVersion = ReadUserVersion(connection);
        var newestKnown = migrations.Count == 0 ? 0 : migrations.Max(migration => migration.Version);

        if (currentVersion > newestKnown)
        {
            throw new InvalidOperationException(
                $"The database schema is v{currentVersion}, but this build supports at most v{newestKnown}. " +
                "Upgrade Camzivo, or restore a backup taken before the newer version was installed. " +
                "The database has not been modified.");
        }

        var pending = migrations
            .OrderBy(migration => migration.Version)
            .Where(migration => migration.Version > currentVersion)
            .ToList();

        string? backupPath = null;
        if (existedBefore && pending.Count > 0)
        {
            backupPath = $"{databasePath}.bak-v{currentVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            using var backup = new SqliteConnection($"Data Source={backupPath}");
            backup.Open();
            connection.BackupDatabase(backup);
        }

        var applied = new List<int>();
        var version = currentVersion;

        foreach (var migration in pending)
        {
            using var transaction = connection.BeginTransaction();

            try
            {
                migration.Apply(connection);

                using (var stamp = connection.CreateCommand())
                {
                    stamp.Transaction = transaction;
                    // Version is an int from our own migration table, never user input.
                    stamp.CommandText = $"PRAGMA user_version = {migration.Version};";
                    stamp.ExecuteNonQuery();
                }

                transaction.Commit();
                applied.Add(migration.Version);
                version = migration.Version;
            }
            catch (SqliteException)
            {
                transaction.Rollback();
                throw;
            }
        }

        // Prune on every open, not only when this run wrote a backup: a build that used to back up on every
        // launch leaves a pile behind, and growth must be corrected by the first fixed launch.
        PruneBackups(databasePath, BackupsToKeep);

        return new MigrationResult(applied, version, backupPath);
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> backups. Returns the number removed.</summary>
    public static int PruneBackups(string databasePath, int keep = BackupsToKeep)
    {
        if (keep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "Keep count cannot be negative.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var fileName = Path.GetFileName(databasePath);

        // Match every backup of *this* database family, including copies made before the product was renamed
        // (camzivo.db.bak-*, corvus.db.bak-*), so an upgraded profile cannot keep orphans forever.
        var extension = Path.GetExtension(fileName);
        var backups = Directory
            .EnumerateFiles(directory, "*" + extension + ".bak-*")
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .ThenByDescending(path => path, StringComparer.Ordinal)
            .ToList();

        var removed = 0;
        foreach (var stale in backups.Skip(keep))
        {
            try
            {
                File.Delete(stale);
                removed++;
            }
            catch (IOException)
            {
                // A locked backup is left for the next run rather than failing startup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}

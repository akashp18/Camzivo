namespace Camzivo.Data.Storage;

/// <summary>
/// Per-user file locations. Deliberately never <c>Program Files</c>: the incumbent's choice to keep user state
/// inside its install directory forces elevation and breaks multi-user machines.
///
/// Constructing with explicit roots makes the whole data layer testable against a temporary directory — and,
/// because of <see cref="MigrateLegacyProfile"/>, upgrading from the pre-rename product keeps the operator's data.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Folder used by builds before the product was renamed. Read once, on upgrade.</summary>
    public const string LegacyFolderName = "Corvus";

    private AppPaths(string configRoot, string cacheRoot, string logRoot, string exportRoot, string databaseFileName)
    {
        ConfigRoot = configRoot;
        CacheRoot = cacheRoot;
        LogRoot = logRoot;
        ExportRoot = exportRoot;
        DatabaseFileName = databaseFileName;
    }

    public AppPaths(string? configRoot = null, string? cacheRoot = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var config = configRoot ?? Path.Combine(appData, "Camzivo");
        var cache = cacheRoot ?? Path.Combine(localAppData, "Camzivo", "cache");

        ConfigRoot = config;
        CacheRoot = cache;

        // Injected roots must be honoured for every derived path, otherwise tests and portable installs silently
        // write into the real profile.
        LogRoot = configRoot is not null
            ? Path.Combine(configRoot, "logs")
            : Path.Combine(localAppData, "Camzivo", "logs");

        ExportRoot = configRoot is not null
            ? Path.Combine(configRoot, "exports")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Camzivo");

        DatabaseFileName = "camzivo.db";
    }

    public string ConfigRoot { get; }

    public string CacheRoot { get; }

    public string LogRoot { get; }

    public string ExportRoot { get; }

    public string DatabaseFileName { get; }

    public string DatabasePath => Path.Combine(ConfigRoot, DatabaseFileName);

    public string SecretsRoot => Path.Combine(ConfigRoot, "secrets");

    public string ConfigFilePath => Path.Combine(ConfigRoot, "config.json");

    /// <summary>Where the pre-rename build kept its data; only used by the one-time upgrade.</summary>
    public static string LegacyConfigRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        LegacyFolderName);

    public static AppPaths Default { get; } = new();

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(SecretsRoot);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(LogRoot);
    }

    /// <summary>
    /// Copies a legacy profile into <paramref name="target"/> when the target has no data yet, then removes the
    /// legacy folder. Copy-then-delete (never move) so an interrupted upgrade leaves the original intact.
    /// Returns the number of files copied; 0 when there was nothing to do.
    /// </summary>
    public static int MigrateLegacyProfile(AppPaths target, string legacyRoot)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(legacyRoot) || !Directory.Exists(legacyRoot))
        {
            return 0;
        }

        if (string.Equals(Path.GetFullPath(legacyRoot), Path.GetFullPath(target.ConfigRoot), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        // A target that already holds a database is an established install: never overwrite it.
        if (Directory.EnumerateFiles(target.ConfigRoot, "*.db").Any())
        {
            return 0;
        }

        var copied = 0;
        foreach (var source in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(legacyRoot, source);
            var destination = Path.Combine(target.ConfigRoot, relative);

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Historical backups are not migrated: they are copies of an older schema and the new profile prunes
            // its own. Copying them only relocates clutter.
            if (relative.Contains(".bak-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The database file name changed with the rename; keep it recognisable in the new profile.
            if (relative.EndsWith("corvus.db", StringComparison.OrdinalIgnoreCase))
            {
                destination = Path.Combine(target.ConfigRoot, target.DatabaseFileName);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var legacySuffix = Path.Combine(legacyRoot, relative + suffix);
                    if (File.Exists(legacySuffix))
                    {
                        File.Copy(legacySuffix, Path.Combine(target.ConfigRoot, target.DatabaseFileName + suffix), overwrite: true);
                    }
                }
            }

            File.Copy(source, destination, overwrite: false);
            copied++;
        }

        if (copied > 0)
        {
            try
            {
                Directory.Delete(legacyRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Leaving a stale folder behind is harmless; the target now owns the data.
            }
        }

        return copied;
    }
}

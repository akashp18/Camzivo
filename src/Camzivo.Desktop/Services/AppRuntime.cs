using System.IO;
using Camzivo.Core.Devices;
using Camzivo.Core.Security;
using Camzivo.Data.Devices;
using Camzivo.Data.Net;
using Camzivo.Data.Repositories;
using Camzivo.Data.Sqlite;
using Camzivo.Data.Storage;

namespace Camzivo.Desktop.Services;

/// <summary>
/// Composition root for the shell. If persistence cannot start (locked or corrupt database) the app still opens
/// and reports the reason instead of failing to launch.
/// </summary>
public sealed class AppRuntime : IAsyncDisposable
{
    private AppRuntime(
        AppPaths paths,
        DeviceRepository? devices,
        AuditRepository? audit,
        PreferencesRepository? preferences,
        PreferenceWriter? writer,
        string? persistenceError,
        ISecretStore secrets,
        DeviceOnboarding? onboarding,
        CameraRepository? cameras = null,
        NvrOnboarding? nvr = null,
        NvrDiscovery? discovery = null,
        DeviceDiagnosticsService? diagnostics = null,
        WallLayoutRepository? wallLayouts = null,
        EventRepository? events = null,
        ExportRepository? exportLog = null)
    {
        Paths = paths;
        Devices = devices;
        Audit = audit;
        Preferences = preferences;
        Writer = writer;
        PersistenceError = persistenceError;
        Secrets = secrets;
        Onboarding = onboarding;
        Cameras = cameras;
        Nvr = nvr;
        Discovery = discovery;
        Diagnostics = diagnostics;
        WallLayouts = wallLayouts;
        Events = events;
        ExportLog = exportLog;
    }

    /// <summary>Named camera layouts. Null when persistence is unavailable.</summary>
    public WallLayoutRepository? WallLayouts { get; }

    /// <summary>Local event history. Null when persistence is unavailable.</summary>
    public EventRepository? Events { get; }

    /// <summary>Records of completed evidence exports. Null when persistence is unavailable.</summary>
    public ExportRepository? ExportLog { get; }

    /// <summary>DPAPI-protected device passwords; available even when the database could not be opened.</summary>
    public ISecretStore Secrets { get; }

    /// <summary>Add/rename/remove service. Null when persistence is unavailable.</summary>
    public DeviceOnboarding? Onboarding { get; }

    /// <summary>Channels of recorders plus standalone cameras. Null when persistence is unavailable.</summary>
    public CameraRepository? Cameras { get; }

    /// <summary>Recorder import and channel lifecycle. Null when persistence is unavailable.</summary>
    public NvrOnboarding? Nvr { get; }

    /// <summary>Reads a recorder's channels over ONVIF. Null when persistence is unavailable.</summary>
    public NvrDiscovery? Discovery { get; }

    /// <summary>Read-only compatibility report used by the Devices diagnostics dialog.</summary>
    public DeviceDiagnosticsService? Diagnostics { get; }

    public AppPaths Paths { get; }

    public DeviceRepository? Devices { get; }

    public AuditRepository? Audit { get; }

    public PreferencesRepository? Preferences { get; }

    public PreferenceWriter? Writer { get; }

    public string? PersistenceError { get; }

    public bool HasPersistence => Devices is not null;

    public static AppRuntime Create() => Create(null);

    /// <summary>
    /// Opens the local store. Passing <paramref name="paths"/> is how an acceptance run gets a clean profile instead
    /// of writing test cameras into the operator's real one; the shell always uses the default profile.
    /// </summary>
    public static AppRuntime Create(AppPaths? paths)
    {
        paths ??= AppPaths.Default;

        try
        {
            paths.EnsureCreated();

            // One-time upgrade from the pre-rename profile: the operator must not lose their wall or settings.
            if (ReferenceEquals(paths, AppPaths.Default))
            {
                var migrated = AppPaths.MigrateLegacyProfile(paths, AppPaths.LegacyConfigRoot);
                if (migrated > 0)
                {
                    AppLog.Info($"Profile upgraded: {migrated} file(s) copied from the pre-rename folder.");
                }
            }

            MigrationRunner.Migrate(paths.DatabasePath);

            var factory = new SqliteConnectionFactory(paths.DatabasePath);
            var preferences = new PreferencesRepository(factory);

            var secrets = new DpapiSecretStore(paths.SecretsRoot);
            var devices = new DeviceRepository(factory);
            var cameras = new CameraRepository(factory);
            var transport = new HttpOnvifTransport();
            var rtspProbe = new TcpRtspEndpointProbe();
            var onboarding = new DeviceOnboarding(devices, secrets, new ConnectionTester(transport, rtspProbe));
            var discovery = new NvrDiscovery(transport, rtspProbe);

            return new AppRuntime(
                paths,
                devices,
                new AuditRepository(factory),
                preferences,
                new PreferenceWriter(preferences, exception => AppLog.Error("Preference write failed", exception)),
                null,
                secrets,
                onboarding,
                cameras,
                new NvrOnboarding(devices, cameras, secrets),
                discovery,
                new DeviceDiagnosticsService(discovery, devices),
                new WallLayoutRepository(factory),
                new EventRepository(factory),
                new ExportRepository(factory));
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidOperationException
                                              or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLog.Error("Local store unavailable; running without persistence", exception);
            return new AppRuntime(
                paths,
                null,
                null,
                null,
                null,
                exception.Message,
                new DpapiSecretStore(paths.SecretsRoot),
                null);
        }
    }

    /// <summary>Last-value-wins persistence; never blocks the caller.</summary>
    public void Persist(string key, string value) => Writer?.Enqueue(key, value);

    public async ValueTask DisposeAsync()
    {
        if (Writer is not null)
        {
            await Writer.DisposeAsync().ConfigureAwait(false);
        }
    }
}

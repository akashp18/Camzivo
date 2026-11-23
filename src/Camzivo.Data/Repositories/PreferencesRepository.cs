using System.Globalization;
using Camzivo.Data.Sqlite;

namespace Camzivo.Data.Repositories;

/// <summary>
/// Small key/value store for shell preferences (theme, layout, section, wall). Kept separate from device data so a
/// settings reset can never touch the asset inventory.
/// </summary>
public sealed class PreferencesRepository(SqliteConnectionFactory factory)
{
    public const string ThemeKey = "ui.theme";
    public const string LayoutKey = "ui.layout";
    public const string SectionKey = "ui.section";
    public const string WallKey = "ui.wall";
    public const string ActiveLayoutKey = "ui.layout.active";
    public const string OpenLastSectionKey = "ui.startup.last-section";
    public const string EventRetentionKey = "events.retention-days";
    public const string WindowKey = "ui.window.main";
    public const string MonitorWindowKey = "ui.window.monitor";

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM preference WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public async Task SetAsync(string key, string value, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO preference (key, value, updated_utc)
            VALUES ($key, $value, $updated)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updated", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Startup-only synchronous read. The shell needs the saved theme before the first frame is drawn, and the
    /// window does not exist yet, so there is no UI thread to block. Not for use once the app is running.
    /// </summary>
    public string? GetSync(string key)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM preference WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM preference;";

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }
}

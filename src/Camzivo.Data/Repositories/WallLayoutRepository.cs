using System.Globalization;
using System.Text.Json;
using Camzivo.Data.Sqlite;

namespace Camzivo.Data.Repositories;

/// <summary>A saved camera wall. Slots are camera identities in tile order; null is an intentionally empty tile.</summary>
public sealed record WallLayoutRecord(
    Guid Id,
    string Name,
    int GridSlots,
    IReadOnlyList<Guid?> Slots,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// Named camera layouts ("Main Entrance", "Warehouse"). Local only: no organisations, no sharing, no accounts.
///
/// Names are unique ignoring case, and every statement is parameterized. A layout is allowed to reference cameras that
/// have since been deleted: the shell restores what still exists and reports how many tiles were skipped, rather than
/// deleting the operator's layout behind their back.
/// </summary>
public sealed class WallLayoutRepository(SqliteConnectionFactory factory)
{
    public const int MaxNameLength = 60;
    public const int MaxLayouts = 40;

    public async Task<IReadOnlyList<WallLayoutRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, grid_slots, slots_json, created_utc, updated_utc
            FROM wall_layout
            ORDER BY name COLLATE NOCASE;
            """;

        var results = new List<WallLayoutRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    /// <summary>
    /// Creates the layout, or replaces the slots of an existing layout with the same name. Returns the stored record.
    /// </summary>
    public async Task<WallLayoutRecord> SaveAsync(
        string name,
        int gridSlots,
        IReadOnlyList<Guid?> slots,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var clean = ValidateName(name);
        ArgumentNullException.ThrowIfNull(slots);

        if (gridSlots < 1 || gridSlots > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(gridSlots), gridSlots, "A layout holds 1 to 64 tiles.");
        }

        if (slots.Count > 64)
        {
            throw new ArgumentException("A layout holds at most 64 tiles.", nameof(slots));
        }

        await using var connection = factory.Open();

        var existing = await FindByNameAsync(connection, clean, cancellationToken);

        if (existing is null)
        {
            var count = await CountAsync(connection, cancellationToken);
            if (count >= MaxLayouts)
            {
                throw new InvalidOperationException($"Camzivo keeps at most {MaxLayouts} saved layouts. Delete one first.");
            }

            existing = new WallLayoutRecord(Guid.NewGuid(), clean, gridSlots, [], now, now);
        }

        var record = existing with
        {
            Name = clean,
            GridSlots = gridSlots,
            Slots = slots.ToArray(),
            UpdatedUtc = now,
        };

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO wall_layout (id, name, grid_slots, slots_json, created_utc, updated_utc)
            VALUES ($id, $name, $slots, $json, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
              name = excluded.name,
              grid_slots = excluded.grid_slots,
              slots_json = excluded.slots_json,
              updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$name", record.Name);
        command.Parameters.AddWithValue("$slots", record.GridSlots);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record.Slots));
        command.Parameters.AddWithValue("$created", record.CreatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", record.UpdatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
        return record;
    }

    public async Task<bool> RenameAsync(string name, string newName, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var clean = ValidateName(newName);
        var original = ValidateName(name);

        await using var connection = factory.Open();

        var target = await FindByNameAsync(connection, clean, cancellationToken);
        if (target is not null && !string.Equals(target.Name, original, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"A layout named \"{clean}\" already exists.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE wall_layout SET name = $new, updated_utc = $updated
            WHERE name = $name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$new", clean);
        command.Parameters.AddWithValue("$name", original);
        command.Parameters.AddWithValue("$updated", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Copies a layout under a new name. The copy keeps its own identity so edits do not affect the original.</summary>
    public async Task<WallLayoutRecord> DuplicateAsync(string name, string copyName, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var clean = ValidateName(copyName);
        var original = ValidateName(name);

        await using var connection = factory.Open();

        var source = await FindByNameAsync(connection, original, cancellationToken)
                     ?? throw new InvalidOperationException($"There is no layout named \"{original}\".");

        if (await FindByNameAsync(connection, clean, cancellationToken) is not null)
        {
            throw new InvalidOperationException($"A layout named \"{clean}\" already exists.");
        }

        var count = await CountAsync(connection, cancellationToken);
        if (count >= MaxLayouts)
        {
            throw new InvalidOperationException($"Camzivo keeps at most {MaxLayouts} saved layouts. Delete one first.");
        }

        var copy = new WallLayoutRecord(Guid.NewGuid(), clean, source.GridSlots, source.Slots, now, now);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO wall_layout (id, name, grid_slots, slots_json, created_utc, updated_utc)
            VALUES ($id, $name, $slots, $json, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", copy.Id.ToString());
        command.Parameters.AddWithValue("$name", copy.Name);
        command.Parameters.AddWithValue("$slots", copy.GridSlots);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(copy.Slots));
        command.Parameters.AddWithValue("$created", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
        return copy;
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var clean = ValidateName(name);

        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM wall_layout WHERE name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", clean);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public static string ValidateName(string name)
    {
        var clean = (name ?? string.Empty).Trim();

        if (clean.Length == 0)
        {
            throw new ArgumentException("A layout needs a name.", nameof(name));
        }

        if (clean.Length > MaxNameLength)
        {
            throw new ArgumentException($"A layout name can be at most {MaxNameLength} characters.", nameof(name));
        }

        return clean;
    }

    private static async Task<WallLayoutRecord?> FindByNameAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, grid_slots, slots_json, created_utc, updated_utc
            FROM wall_layout WHERE name = $name COLLATE NOCASE LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", name);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static async Task<int> CountAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM wall_layout;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static WallLayoutRecord Map(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var slots = new List<Guid?>();

        try
        {
            foreach (var element in JsonSerializer.Deserialize<List<string?>>(reader.GetString(3)) ?? [])
            {
                slots.Add(Guid.TryParse(element, out var id) ? id : null);
            }
        }
        catch (JsonException)
        {
            // A hand-edited row restores as an empty wall instead of crashing the Live View.
        }

        return new WallLayoutRecord(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt32(2),
            slots,
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }
}

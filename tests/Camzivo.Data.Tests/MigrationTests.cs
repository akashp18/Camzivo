using Camzivo.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Camzivo.Data.Tests;

public class MigrationTests
{
    [Fact]
    public void MigrationCreatesTheFullSchemaAtTheCurrentVersion()
    {
        using var fixture = new SqliteFixture();

        Assert.Equal(MigrationRunner.CurrentVersion, fixture.Migrations.ResultingVersion);

        using var connection = fixture.Factory.Open();
        var tables = ReadTableNames(connection);

        foreach (var expected in new[]
                 {
                     "camera_group", "device", "camera", "device_capability", "workspace_layout", "layout_slot",
                     "preference", "event", "export_job", "connection_history", "audit_entry", "app_log",
                 })
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        using var fixture = new SqliteFixture();

        var second = MigrationRunner.Migrate(fixture.DatabasePath);

        Assert.Empty(second.AppliedVersions);
        Assert.Equal(MigrationRunner.CurrentVersion, second.ResultingVersion);
    }

    [Fact]
    public void ExistingDatabaseIsBackedUpBeforeMigration()
    {
        using var fixture = new SqliteFixture();
        var nextVersion = MigrationRunner.CurrentVersion + 1;
        var withExtraStep = MigrationRunner.All
            .Concat([new Migration(nextVersion, "no-op upgrade used to prove the backup path", _ => { })])
            .ToList();

        var second = MigrationRunner.Migrate(fixture.DatabasePath, withExtraStep);

        Assert.NotNull(second.BackupPath);
        Assert.True(File.Exists(second.BackupPath));
        Assert.Equal(nextVersion, second.ResultingVersion);
    }

    [Fact]
    public void ForeignKeysAreEnforced()
    {
        using var fixture = new SqliteFixture();
        using var connection = fixture.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO camera (id, device_id, channel, name, created_utc, updated_utc)
            VALUES ('c1', 'missing-device', 1, 'Orphan', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
            """;

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void SecretsAreNotStoredAsPlaintextColumns()
    {
        using var fixture = new SqliteFixture();
        using var connection = fixture.Factory.Open();

        var deviceColumns = ReadColumnNames(connection, "device");
        Assert.Contains("secret_ref", deviceColumns);
        Assert.DoesNotContain("password", deviceColumns);
        Assert.DoesNotContain("secret", deviceColumns);
    }

    private static List<string> ReadTableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static List<string> ReadColumnNames(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }
}

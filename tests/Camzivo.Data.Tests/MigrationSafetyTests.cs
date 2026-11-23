using Camzivo.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Camzivo.Data.Tests;

/// <summary>Regressions for operational bugs found in review: backup spam, newer-schema handling, backup pruning.</summary>
public class MigrationSafetyTests
{
    [Fact]
    public void FreshDatabaseHasNothingToBackUp()
    {
        using var fixture = new SqliteFixture();

        Assert.Null(fixture.Migrations.BackupPath);
        Assert.Empty(Directory.GetFiles(fixture.Root, "camzivo.db.bak-*"));
    }

    [Fact]
    public void RelaunchWithNoPendingMigrationsWritesNoBackup()
    {
        using var fixture = new SqliteFixture();

        for (var i = 0; i < 3; i++)
        {
            var result = MigrationRunner.Migrate(fixture.DatabasePath);
            Assert.Empty(result.AppliedVersions);
            Assert.Null(result.BackupPath);
        }

        Assert.Empty(Directory.GetFiles(fixture.Root, "camzivo.db.bak-*"));
    }

    [Fact]
    public void DatabaseFromANewerBuildIsRefusedWithoutBeingModified()
    {
        using var fixture = new SqliteFixture();

        using (var connection = fixture.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(() => MigrationRunner.Migrate(fixture.DatabasePath));

        Assert.Contains("v99", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not been modified", exception.Message, StringComparison.Ordinal);

        using var verify = fixture.Factory.Open();
        Assert.Equal(99, MigrationRunner.ReadUserVersion(verify));
    }

    [Fact]
    public void BackupsArePrunedToTheRetentionLimit()
    {
        using var fixture = new SqliteFixture();

        for (var i = 0; i < 9; i++)
        {
            var fake = Path.Combine(fixture.Root, $"camzivo.db.bak-v1-2026010100000{i}");
            File.WriteAllText(fake, "backup");
            File.SetLastWriteTimeUtc(fake, DateTime.UtcNow.AddMinutes(-i));
        }

        var removed = MigrationRunner.PruneBackups(fixture.DatabasePath, keep: 5);

        Assert.Equal(4, removed);
        Assert.Equal(5, Directory.GetFiles(fixture.Root, "camzivo.db.bak-*").Length);
    }

    [Fact]
    public void BackupsFromThePreRenameFileNameArePrunedToo()
    {
        using var fixture = new SqliteFixture();

        for (var i = 0; i < 8; i++)
        {
            var legacy = Path.Combine(fixture.Root, $"corvus.db.bak-v1-old{i}");
            File.WriteAllText(legacy, "legacy");
            File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddHours(-i));
        }

        MigrationRunner.Migrate(fixture.DatabasePath);

        Assert.Equal(MigrationRunner.BackupsToKeep, Directory.GetFiles(fixture.Root, "*.bak-*").Length);
    }

    [Fact]
    public void StaleBackupsArePrunedEvenWhenTheSchemaIsAlreadyCurrent()
    {
        using var fixture = new SqliteFixture();

        for (var i = 0; i < 8; i++)
        {
            var fake = Path.Combine(fixture.Root, $"camzivo.db.bak-v1-legacy{i}");
            File.WriteAllText(fake, "legacy");
            File.SetLastWriteTimeUtc(fake, DateTime.UtcNow.AddHours(-i));
        }

        var result = MigrationRunner.Migrate(fixture.DatabasePath);

        Assert.Empty(result.AppliedVersions);
        Assert.Equal(MigrationRunner.BackupsToKeep, Directory.GetFiles(fixture.Root, "camzivo.db.bak-*").Length);
    }

    [Fact]
    public void PruneToleratesAMissingDirectoryAndNegativeKeep()
    {
        Assert.Equal(0, MigrationRunner.PruneBackups(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid(), "x.db")));
        Assert.Throws<ArgumentOutOfRangeException>(() => MigrationRunner.PruneBackups("x.db", -1));
    }
}

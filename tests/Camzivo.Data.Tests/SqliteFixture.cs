using Camzivo.Data.Sqlite;

namespace Camzivo.Data.Tests;

/// <summary>Creates an isolated, migrated database per test and deletes it afterwards.</summary>
public sealed class SqliteFixture : IDisposable
{
    public SqliteFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "camzivo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        DatabasePath = Path.Combine(Root, "camzivo.db");
        Migrations = MigrationRunner.Migrate(DatabasePath);
        Factory = new SqliteConnectionFactory(DatabasePath);
    }

    public string Root { get; }

    public string DatabasePath { get; }

    public MigrationResult Migrations { get; }

    public SqliteConnectionFactory Factory { get; }

    public DeviceRepositoryFixture Devices => new(Factory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A test process holding a WAL handle must not fail the test run.
        }
    }
}

/// <summary>Thin accessor so tests read naturally without duplicating factory plumbing.</summary>
public sealed class DeviceRepositoryFixture(SqliteConnectionFactory factory)
{
    public Repositories.DeviceRepository Repository { get; } = new(factory);
}

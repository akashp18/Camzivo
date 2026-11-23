namespace Camzivo.Data.Sqlite;

/// <summary>One forward-only schema step. Never edited after release; a mistake is fixed by a new migration.</summary>
public sealed record Migration(int Version, string Description, Action<Microsoft.Data.Sqlite.SqliteConnection> Apply);

public sealed record MigrationResult(IReadOnlyList<int> AppliedVersions, int ResultingVersion, string? BackupPath)
{
    public bool ChangedAnything => AppliedVersions.Count > 0;
}

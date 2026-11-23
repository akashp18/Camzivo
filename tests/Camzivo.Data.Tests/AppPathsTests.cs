using Camzivo.Data.Storage;
using Xunit;

namespace Camzivo.Data.Tests;

public class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "camzivo-paths-" + Guid.NewGuid().ToString("N"));

    public AppPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void InjectedRootsAreHonouredForEveryDerivedPath()
    {
        var config = Path.Combine(_root, "config");
        var cache = Path.Combine(_root, "cache");
        var paths = new AppPaths(config, cache);

        Assert.Equal(config, paths.ConfigRoot);
        Assert.Equal(cache, paths.CacheRoot);
        Assert.StartsWith(config, paths.LogRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(config, paths.ExportRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(config, "camzivo.db"), paths.DatabasePath);
    }

    [Fact]
    public void EnsureCreatedMakesEveryDirectoryItReports()
    {
        var paths = new AppPaths(Path.Combine(_root, "config"), Path.Combine(_root, "cache"));
        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.ConfigRoot));
        Assert.True(Directory.Exists(paths.SecretsRoot));
        Assert.True(Directory.Exists(paths.CacheRoot));
        Assert.True(Directory.Exists(paths.LogRoot));
    }

    [Fact]
    public void LegacyProfileIsCopiedAndRemoved()
    {
        var legacy = Path.Combine(_root, "Corvus");
        Directory.CreateDirectory(Path.Combine(legacy, "secrets"));
        File.WriteAllText(Path.Combine(legacy, "corvus.db"), "database-bytes");
        File.WriteAllText(Path.Combine(legacy, "corvus.db-wal"), "wal-bytes");
        File.WriteAllText(Path.Combine(legacy, "secrets", "device.bin"), "secret-bytes");

        var target = new AppPaths(Path.Combine(_root, "Camzivo"), Path.Combine(_root, "cache"));
        target.EnsureCreated();

        var copied = AppPaths.MigrateLegacyProfile(target, legacy);

        Assert.Equal(3, copied);
        Assert.Equal("database-bytes", File.ReadAllText(target.DatabasePath));
        Assert.True(File.Exists(target.DatabasePath + "-wal"));
        Assert.True(File.Exists(Path.Combine(target.SecretsRoot, "device.bin")));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void AnEstablishedProfileIsNeverOverwritten()
    {
        var legacy = Path.Combine(_root, "Corvus");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "corvus.db"), "old");

        var target = new AppPaths(Path.Combine(_root, "Camzivo"), Path.Combine(_root, "cache"));
        target.EnsureCreated();
        File.WriteAllText(target.DatabasePath, "current");

        Assert.Equal(0, AppPaths.MigrateLegacyProfile(target, legacy));
        Assert.Equal("current", File.ReadAllText(target.DatabasePath));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void AMissingLegacyProfileIsANoOp()
    {
        var target = new AppPaths(Path.Combine(_root, "Camzivo"), Path.Combine(_root, "cache"));
        target.EnsureCreated();

        Assert.Equal(0, AppPaths.MigrateLegacyProfile(target, Path.Combine(_root, "does-not-exist")));
    }
}

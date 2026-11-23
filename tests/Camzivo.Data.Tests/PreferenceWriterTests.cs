using Camzivo.Data.Repositories;
using Camzivo.Data.Sqlite;
using Xunit;

namespace Camzivo.Data.Tests;

/// <summary>
/// Ordering is the whole point: the previous implementation could persist an intermediate layout after a later one
/// because each change wrote on its own task.
/// </summary>
public class PreferenceWriterTests
{
    [Fact]
    public async Task TheLastValueEnqueuedIsTheValueStored()
    {
        using var fixture = new SqliteFixture();
        MigrationRunner.Migrate(fixture.DatabasePath);

        var repository = new PreferencesRepository(new SqliteConnectionFactory(fixture.DatabasePath));
        await using var writer = new PreferenceWriter(repository);

        for (var index = 1; index <= 100; index++)
        {
            writer.Enqueue(PreferencesRepository.LayoutKey, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("100", await repository.GetAsync(PreferencesRepository.LayoutKey));
    }

    [Fact]
    public async Task IndependentKeysDoNotOverwriteEachOther()
    {
        using var fixture = new SqliteFixture();
        MigrationRunner.Migrate(fixture.DatabasePath);

        var repository = new PreferencesRepository(new SqliteConnectionFactory(fixture.DatabasePath));
        await using var writer = new PreferenceWriter(repository);

        writer.Enqueue(PreferencesRepository.ThemeKey, "light");
        writer.Enqueue(PreferencesRepository.LayoutKey, "16");
        writer.Enqueue(PreferencesRepository.SectionKey, "Health");

        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("light", await repository.GetAsync(PreferencesRepository.ThemeKey));
        Assert.Equal("16", await repository.GetAsync(PreferencesRepository.LayoutKey));
        Assert.Equal("Health", await repository.GetAsync(PreferencesRepository.SectionKey));
    }

    [Fact]
    public async Task AWriteFailureIsReportedAndNeverThrowsIntoTheCaller()
    {
        var errors = new List<Exception>();
        var unreachableDirectory = Path.Combine(Path.GetTempPath(), "camzivo-missing-" + Guid.NewGuid().ToString("N"), "nested");
        var unreachable = new PreferencesRepository(
            new SqliteConnectionFactory(Path.Combine(unreachableDirectory, "db.sqlite")));

        await using var writer = new PreferenceWriter(unreachable, errors.Add);
        writer.Enqueue(PreferencesRepository.ThemeKey, "dark");

        Assert.True(await writer.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task FlushingAnEmptyWriterSucceedsImmediately()
    {
        using var fixture = new SqliteFixture();
        MigrationRunner.Migrate(fixture.DatabasePath);

        await using var writer = new PreferenceWriter(new PreferencesRepository(new SqliteConnectionFactory(fixture.DatabasePath)));

        Assert.True(await writer.FlushAsync(TimeSpan.FromMilliseconds(250)));
    }
}

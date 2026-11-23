using Camzivo.Data.Repositories;
using Xunit;

namespace Camzivo.Data.Tests;

public class PreferencesRepositoryTests
{
    [Fact]
    public async Task MissingKeyReturnsNull()
    {
        using var fixture = new SqliteFixture();
        var preferences = new PreferencesRepository(fixture.Factory);

        Assert.Null(await preferences.GetAsync(PreferencesRepository.ThemeKey));
    }

    [Fact]
    public async Task ValueRoundTripsAndIsOverwritten()
    {
        using var fixture = new SqliteFixture();
        var preferences = new PreferencesRepository(fixture.Factory);
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        await preferences.SetAsync(PreferencesRepository.ThemeKey, "light", now);
        Assert.Equal("light", await preferences.GetAsync(PreferencesRepository.ThemeKey));

        await preferences.SetAsync(PreferencesRepository.ThemeKey, "dark", now.AddMinutes(1));
        Assert.Equal("dark", await preferences.GetAsync(PreferencesRepository.ThemeKey));
        Assert.Single(await preferences.GetAllAsync());
    }

    [Fact]
    public async Task KeysAreIndependent()
    {
        using var fixture = new SqliteFixture();
        var preferences = new PreferencesRepository(fixture.Factory);
        var now = DateTimeOffset.UtcNow;

        await preferences.SetAsync(PreferencesRepository.ThemeKey, "dark", now);
        await preferences.SetAsync(PreferencesRepository.LayoutKey, "16", now);

        var all = await preferences.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal("16", all[PreferencesRepository.LayoutKey]);
    }
}

using Camzivo.Data.Repositories;
using Xunit;

namespace Camzivo.Data.Tests;

public class WallLayoutRepositoryTests
{
    private static WallLayoutRepository Repository(SqliteFixture fixture) => new(fixture.Factory);

    [Fact]
    public async Task SavingALayoutRoundTripsEverySlotIncludingEmptyTiles()
    {
        using var fixture = new SqliteFixture();
        var repository = Repository(fixture);

        var camera = Guid.NewGuid();
        var second = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var saved = await repository.SaveAsync("Main Entrance", 9, [camera, null, second], now);
        var layouts = await repository.ListAsync();

        Assert.Single(layouts);
        Assert.Equal("Main Entrance", layouts[0].Name);
        Assert.Equal(9, layouts[0].GridSlots);
        Assert.Equal(new Guid?[] { camera, null, second }, layouts[0].Slots);
        Assert.Equal(saved.Id, layouts[0].Id);
    }

    [Fact]
    public async Task SavingTheSameNameAgainUpdatesInsteadOfDuplicating()
    {
        using var fixture = new SqliteFixture();
        var repository = Repository(fixture);
        var now = DateTimeOffset.UtcNow;

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var created = await repository.SaveAsync("Warehouse", 16, [first], now);
        var updated = await repository.SaveAsync("warehouse", 16, [second], now.AddMinutes(1));

        var layouts = await repository.ListAsync();

        Assert.Single(layouts);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(second, layouts[0].Slots[0]);
    }

    [Fact]
    public async Task RenameAndDuplicateKeepIndependentContent()
    {
        using var fixture = new SqliteFixture();
        var repository = Repository(fixture);
        var now = DateTimeOffset.UtcNow;
        var camera = Guid.NewGuid();

        await repository.SaveAsync("Receiving", 4, [camera], now);

        Assert.True(await repository.RenameAsync("Receiving", "Shipping", now));
        var copy = await repository.DuplicateAsync("Shipping", "Shipping copy", now);

        Assert.NotEqual(Guid.Empty, copy.Id);
        Assert.Equal(camera, copy.Slots[0]);

        await repository.SaveAsync("Shipping copy", 4, [Guid.NewGuid()], now.AddMinutes(1));

        var shipping = (await repository.ListAsync()).Single(layout => layout.Name == "Shipping");
        Assert.Equal(camera, shipping.Slots[0]);
    }

    [Fact]
    public async Task NamesAreRequiredUniqueAndBounded()
    {
        using var fixture = new SqliteFixture();
        var repository = Repository(fixture);
        var now = DateTimeOffset.UtcNow;

        await repository.SaveAsync("Perimeter", 4, [], now);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync("   ", 4, [], now));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(new string('x', 61), 4, [], now));

        // Changing only the casing of a name is a rename, not a collision.
        Assert.True(await repository.RenameAsync("Perimeter", "PERIMETER", now));

        await repository.DuplicateAsync("PERIMETER", "Perimeter 2", now);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DuplicateAsync("PERIMETER", "perimeter 2", now));
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheNamedLayout()
    {
        using var fixture = new SqliteFixture();
        var repository = Repository(fixture);
        var now = DateTimeOffset.UtcNow;

        await repository.SaveAsync("A", 4, [], now);
        await repository.SaveAsync("B", 4, [], now);

        Assert.True(await repository.DeleteAsync("a"));
        Assert.False(await repository.DeleteAsync("a"));

        var remaining = await repository.ListAsync();
        Assert.Single(remaining);
        Assert.Equal("B", remaining[0].Name);
    }

    [Fact]
    public async Task CorruptSlotJsonRestoresAsAnEmptyWallInsteadOfThrowing()
    {
        using var fixture = new SqliteFixture();
        var repository = Repository(fixture);
        var now = DateTimeOffset.UtcNow;

        await repository.SaveAsync("Damaged", 4, [], now);

        await using (var connection = fixture.Factory.Open())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE wall_layout SET slots_json = 'not json' WHERE name = 'Damaged';";
            await command.ExecuteNonQueryAsync();
        }

        var layout = Assert.Single(await repository.ListAsync());
        Assert.Empty(layout.Slots);
    }
}

public class EventRepositoryTests
{
    [Fact]
    public async Task EventsRoundTripNewestFirst()
    {
        using var fixture = new SqliteFixture();
        var repository = new EventRepository(fixture.Factory);
        var camera = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await repository.AppendAsync(new EventRecord(0, now.AddMinutes(-5), camera, "Gate", "stream", EventSeverity.Warning, "Reconnecting", "attempt 1"));
        await repository.AppendAsync(new EventRecord(0, now, camera, "Gate", "stream", EventSeverity.Critical, "Stream failed", "401"));

        var events = await repository.QueryAsync(new EventQuery());

        Assert.Equal(2, events.Count);
        Assert.Equal("Stream failed", events[0].Message);
        Assert.Equal(EventSeverity.Critical, events[0].Severity);
        Assert.Equal("Reconnecting", events[1].Message);
        Assert.Equal(camera, events[0].CameraId);
    }

    [Fact]
    public async Task QueryFiltersByCameraSeverityTextAndWindow()
    {
        using var fixture = new SqliteFixture();
        var repository = new EventRepository(fixture.Factory);
        var gate = Guid.NewGuid();
        var dock = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await repository.AppendAsync(new EventRecord(0, now.AddHours(-3), gate, "Gate", "stream", EventSeverity.Information, "Online", ""));
        await repository.AppendAsync(new EventRecord(0, now.AddHours(-2), gate, "Gate", "stream", EventSeverity.Warning, "Reconnecting", "timeout"));
        await repository.AppendAsync(new EventRecord(0, now.AddHours(-1), dock, "Dock", "device", EventSeverity.Critical, "Authentication failed", "401"));

        var byCamera = await repository.QueryAsync(new EventQuery(CameraId: gate));
        Assert.Equal(2, byCamera.Count);

        var bySeverity = await repository.QueryAsync(new EventQuery(MinimumSeverity: EventSeverity.Warning));
        Assert.Equal(2, bySeverity.Count);

        var byText = await repository.QueryAsync(new EventQuery(Search: "auth"));
        Assert.Single(byText);
        Assert.Equal("Dock", byText[0].CameraName);

        var byWindow = await repository.QueryAsync(new EventQuery(FromUtc: now.AddMinutes(-90)));
        Assert.Single(byWindow);
    }

    [Fact]
    public async Task PruneDeletesOnlyEntriesOlderThanTheCutoff()
    {
        using var fixture = new SqliteFixture();
        var repository = new EventRepository(fixture.Factory);
        var now = DateTimeOffset.UtcNow;

        await repository.AppendAsync(new EventRecord(0, now.AddDays(-40), null, "Gate", "stream", EventSeverity.Information, "Old", ""));
        await repository.AppendAsync(new EventRecord(0, now.AddDays(-1), null, "Gate", "stream", EventSeverity.Information, "Recent", ""));

        var removed = await repository.PruneAsync(now.AddDays(-30));

        Assert.Equal(1, removed);
        Assert.Equal(1, await repository.CountAsync());
    }
}

public class ExportRepositoryTests
{
    [Fact]
    public async Task ExportsAreListedNewestFirstAndCanBeDeleted()
    {
        using var fixture = new SqliteFixture();
        var repository = new ExportRepository(fixture.Factory);
        var now = DateTimeOffset.UtcNow;

        var older = new ExportRecord(Guid.NewGuid(), "Gate", @"C:\evidence\a.mp4", 1024, 12, now.AddMinutes(-10), now.AddMinutes(-9), now.AddMinutes(-8));
        var newer = new ExportRecord(Guid.NewGuid(), "Dock", @"C:\evidence\b.mp4", 2048, 24, now.AddMinutes(-5), now.AddMinutes(-4), now);

        await repository.RecordAsync(older);
        await repository.RecordAsync(newer);

        var listed = await repository.ListAsync();

        Assert.Equal(2, listed.Count);
        Assert.Equal("Dock", listed[0].CameraName);
        Assert.Equal(2048, listed[0].Bytes);

        Assert.True(await repository.DeleteAsync(older.Id));
        Assert.Single(await repository.ListAsync());
    }
}

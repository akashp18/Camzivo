using Camzivo.Data.Repositories;
using Xunit;

namespace Camzivo.Data.Tests;

public class AuditRepositoryTests
{
    [Fact]
    public async Task AuditEntriesAreReadableNewestFirst()
    {
        using var fixture = new SqliteFixture();
        var audit = new AuditRepository(fixture.Factory);
        var now = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

        await audit.AddAsync("device.add", "device-1", "Added camera at 10.0.0.5", "operator", now);
        await audit.AddAsync("export.create", "export-1", "Range 10:00-10:10", "operator", now.AddMinutes(1));

        var entries = await audit.RecentAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal("export.create", entries[0].Action);
        Assert.Equal("device.add", entries[1].Action);
    }

    [Fact]
    public async Task SecretsInDetailAreRedactedOnWrite()
    {
        using var fixture = new SqliteFixture();
        var audit = new AuditRepository(fixture.Factory);

        await audit.AddAsync(
            "device.credential.update",
            "device-1",
            "Probe of rtsp://admin:hunter2@10.0.0.5:554/live failed with password=abc123",
            "operator",
            DateTimeOffset.UtcNow);

        var entry = Assert.Single(await audit.RecentAsync());

        Assert.DoesNotContain("hunter2", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("***", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecentLimitIsClampedAndApplied()
    {
        using var fixture = new SqliteFixture();
        var audit = new AuditRepository(fixture.Factory);

        for (var i = 0; i < 5; i++)
        {
            await audit.AddAsync("device.add", $"device-{i}", string.Empty, "operator", DateTimeOffset.UtcNow);
        }

        Assert.Equal(3, (await audit.RecentAsync(3)).Count);
        Assert.Single(await audit.RecentAsync(0));
    }
}

using Camzivo.Core.Devices;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Camzivo.Data.Tests;

public class DeviceRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeviceRoundTripsWithAllFields()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.Devices.Repository;

        var device = Device.CreateNew("Warehouse North", "192.168.18.11", Now) with
        {
            OnvifPort = 8000,
            RtspPort = 554,
            HttpPort = 80,
            UseTls = true,
            Username = "operator",
            SecretRef = "secret-1",
            Vendor = "dahua",
            Model = "IPC-HDBW2231E",
        };

        await repository.UpsertAsync(device);
        var loaded = await repository.GetAsync(device.Id);

        Assert.NotNull(loaded);
        Assert.Equal(device.DisplayName, loaded!.DisplayName);
        Assert.Equal(8000, loaded.OnvifPort);
        Assert.True(loaded.UseTls);
        Assert.Equal("operator", loaded.Username);
        Assert.Equal("secret-1", loaded.SecretRef);
        Assert.Equal("dahua", loaded.Vendor);
        Assert.Equal(Now, loaded.CreatedUtc);
    }

    [Fact]
    public async Task UpsertReplacesAnExistingRow()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.Devices.Repository;
        var device = Device.CreateNew("Camera 1", "192.168.18.20", Now);

        await repository.UpsertAsync(device);
        await repository.UpsertAsync(device with { DisplayName = "Dock Gate", UpdatedUtc = Now.AddMinutes(5) });

        var all = await repository.GetAllAsync();
        var single = Assert.Single(all);
        Assert.Equal("Dock Gate", single.DisplayName);
    }

    [Fact]
    public async Task ChannelsKeptFromARecorderMayShareItsEndpoint()
    {
        // Removing a recorder while keeping its channels turns each channel into its own device; those devices share
        // the recorder's address by definition, so the schema must allow it. Duplicate detection lives in the import
        // service, which knows that eight channels of one recorder are not eight duplicates.
        using var fixture = new SqliteFixture();
        var repository = fixture.Devices.Repository;

        await repository.UpsertAsync(Device.CreateNew("Channel 1", "192.168.18.20", Now));
        await repository.UpsertAsync(Device.CreateNew("Channel 2", "192.168.18.20", Now));

        var all = await repository.GetAllAsync();
        Assert.Equal(2, all.Count);

        var found = await repository.FindByEndpointAsync("192.168.18.20", 80, 554);
        Assert.NotNull(found);
    }

    [Fact]
    public async Task RecorderKindRoundTripsThroughTheDatabase()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.Devices.Repository;
        var device = Device.CreateNew("Warehouse NVR", "192.168.18.40", Now) with { Kind = DeviceKind.Nvr };

        await repository.UpsertAsync(device);

        var loaded = await repository.GetAsync(device.Id);
        Assert.Equal(DeviceKind.Nvr, loaded!.Kind);
    }

    [Fact]
    public async Task DiagnosticIdentityAndSourceTokenRoundTrip()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.Devices.Repository;
        var device = Device.CreateNew("Warehouse NVR", "192.168.18.40", Now) with
        {
            Kind = DeviceKind.Nvr,
            Firmware = "4.000.0000000.0",
            SerialNumber = "NVR-SERIAL-1",
            DeviceTimeZone = "UTC+08:00",
            LastConnectionError = "RTSP endpoint did not answer",
            LastDiagnosticUtc = Now,
            RecordingSourceToken = "channel-7",
        };

        await repository.UpsertAsync(device);
        var loaded = await repository.GetAsync(device.Id);

        Assert.Equal("4.000.0000000.0", loaded!.Firmware);
        Assert.Equal("NVR-SERIAL-1", loaded.SerialNumber);
        Assert.Equal("UTC+08:00", loaded.DeviceTimeZone);
        Assert.Equal("RTSP endpoint did not answer", loaded.LastConnectionError);
        Assert.Equal(Now, loaded.LastDiagnosticUtc);
        Assert.Equal("channel-7", loaded.RecordingSourceToken);
    }

    [Fact]
    public async Task DeleteRemovesTheDevice()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.Devices.Repository;
        var device = Device.CreateNew("Camera 1", "192.168.18.20", Now);

        await repository.UpsertAsync(device);

        Assert.True(await repository.DeleteAsync(device.Id));
        Assert.Null(await repository.GetAsync(device.Id));
        Assert.False(await repository.DeleteAsync(device.Id));
    }

    [Fact]
    public async Task GetAllIsOrderedByDisplayName()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.Devices.Repository;

        await repository.UpsertAsync(Device.CreateNew("Zebra gate", "10.0.0.9", Now));
        await repository.UpsertAsync(Device.CreateNew("Loading bay", "10.0.0.8", Now));
        await repository.UpsertAsync(Device.CreateNew("Entrance", "10.0.0.7", Now));

        var names = (await repository.GetAllAsync()).Select(device => device.DisplayName).ToList();

        Assert.Equal(["Entrance", "Loading bay", "Zebra gate"], names);
    }

    [Fact]
    public async Task HostileDisplayNameIsStoredAsDataNotSql()
    {
        using var fixture = new SqliteFixture();
        var repository = fixture.Devices.Repository;
        const string hostile = "x'; DROP TABLE device; DROP TABLE camera; --";

        await repository.UpsertAsync(Device.CreateNew(hostile, "10.0.0.6", Now));

        var all = await repository.GetAllAsync();
        Assert.Equal(hostile, Assert.Single(all).DisplayName);

        using var connection = fixture.Factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='device';";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }
}

using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Security;
using Camzivo.Data.Devices;
using Camzivo.Data.Repositories;
using Xunit;

namespace Camzivo.Data.Tests.Devices;

public sealed class DeviceOnboardingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T09:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("", "192.168.1.10", "A name is required.")]
    [InlineData("Cam", "", "Enter an IP address, a host name, or an rtsp:// URL.")]
    [InlineData("Cam", "http://192.168.1.10", "Only rtsp:// URLs are accepted here; for ONVIF enter just the address.")]
    public void Validate_RejectsIncompleteOrWrongScheme(string name, string address, string expected)
    {
        var error = DeviceOnboarding.Validate(new CameraRegistrationRequest(name, address));

        Assert.Equal(expected, error);
    }

    [Fact]
    public void Validate_RejectsCredentialsEmbeddedInRtspUrl()
    {
        var error = DeviceOnboarding.Validate(new CameraRegistrationRequest(
            "Front",
            "rtsp://admin:admin123@192.168.1.10:554/cam/realmonitor?channel=1&subtype=0"));

        Assert.Equal(
            "Remove the username and password from the URL; enter them in the fields below so they can be stored securely.",
            error);
    }

    [Fact]
    public void Validate_AcceptsHostAndRtspUrl()
    {
        Assert.Null(DeviceOnboarding.Validate(new CameraRegistrationRequest("Front", "192.168.1.10")));
        Assert.Null(DeviceOnboarding.Validate(new CameraRegistrationRequest(
            "Front",
            "rtsp://192.168.1.10:8554/live/ch0",
            "admin",
            "secret")));
    }

    [Fact]
    public void Validate_RejectsOutOfRangePort()
    {
        Assert.Equal(
            "The ONVIF port must be between 1 and 65535.",
            DeviceOnboarding.Validate(new CameraRegistrationRequest("Front", "192.168.1.10", OnvifPort: 70000)));
    }

    [Fact]
    public async Task Register_PersistsDeviceWithVendorAndDpapiReference()
    {
        var devices = new MemoryDeviceStore();
        var secrets = new MemorySecretStore();
        var onboarding = new DeviceOnboarding(devices, secrets, new StubTester(Success()));

        var outcome = await onboarding.RegisterAsync(
            new CameraRegistrationRequest("Front door", "192.168.1.108", "admin", "Sup3rSecret!"),
            Now);

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Device);
        Assert.Equal("Front door", outcome.Device!.DisplayName);
        Assert.Equal("Dahua", outcome.Device.Vendor);
        Assert.Equal("IPC-HDW2431T", outcome.Device.Model);
        Assert.Equal("device-" + outcome.Device.Id.ToString("N"), outcome.Device.SecretRef);
        Assert.Equal("Sup3rSecret!", secrets.Load(outcome.Device.SecretRef!));
        Assert.Single(devices.Items);
        Assert.DoesNotContain("Sup3rSecret!", outcome.Device.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_PersistsNothingWhenTheConnectionTestFails()
    {
        var devices = new MemoryDeviceStore();
        var secrets = new MemorySecretStore();
        var onboarding = new DeviceOnboarding(devices, secrets, new StubTester(Failure()));

        var outcome = await onboarding.RegisterAsync(
            new CameraRegistrationRequest("Front door", "192.168.1.108", "admin", "Sup3rSecret!"),
            Now);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Device);
        Assert.Empty(devices.Items);
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public async Task Register_ManualRtspUrlKeepsTheSuppliedPortAndStream()
    {
        var devices = new MemoryDeviceStore();
        var secrets = new MemorySecretStore();
        var onboarding = new DeviceOnboarding(devices, secrets, new StubTester(Success()));

        var outcome = await onboarding.RegisterAsync(
            new CameraRegistrationRequest("Gate", "rtsp://192.168.1.50:8554/live/ch0"),
            Now);

        Assert.True(outcome.Success);
        Assert.Equal("192.168.1.50", outcome.Device!.Host);
        Assert.Equal(8554, outcome.Device.RtspPort);
        Assert.Equal("rtsp://192.168.1.50:8554/live/ch0", outcome.Stages.Count == 4
            ? new Uri("rtsp://192.168.1.50:8554/live/ch0").AbsoluteUri
            : string.Empty);
        Assert.Null(outcome.Device.SecretRef);
    }

    [Fact]
    public async Task Remove_DeletesTheStoredSecretWithTheDevice()
    {
        var devices = new MemoryDeviceStore();
        var secrets = new MemorySecretStore();
        var onboarding = new DeviceOnboarding(devices, secrets, new StubTester(Success()));

        var outcome = await onboarding.RegisterAsync(
            new CameraRegistrationRequest("Front door", "192.168.1.108", "admin", "Sup3rSecret!"),
            Now);

        var removed = await onboarding.RemoveAsync(outcome.Device!.Id);

        Assert.True(removed);
        Assert.Empty(devices.Items);
        Assert.Empty(secrets.Items);
    }

    [Fact]
    public async Task Rename_ChangesOnlyTheName()
    {
        var devices = new MemoryDeviceStore();
        var secrets = new MemorySecretStore();
        var onboarding = new DeviceOnboarding(devices, secrets, new StubTester(Success()));

        var outcome = await onboarding.RegisterAsync(
            new CameraRegistrationRequest("Front door", "192.168.1.108", "admin", "Sup3rSecret!"),
            Now);

        var renamed = await onboarding.RenameAsync(outcome.Device!.Id, "  Reception  ", Now.AddMinutes(1));

        Assert.NotNull(renamed);
        Assert.Equal("Reception", renamed!.DisplayName);
        Assert.Equal(outcome.Device.SecretRef, renamed.SecretRef);
        Assert.Equal(Now.AddMinutes(1), renamed.UpdatedUtc);
    }

    private static ConnectionTestResult Success()
    {
        var stages = new List<StageResult>
        {
            new(ConnectionStage.Reach, true, StreamFailure.None, "Device answered", TimeSpan.FromMilliseconds(20)),
            new(ConnectionStage.Authenticate, true, StreamFailure.None, "Signed in", TimeSpan.Zero),
            new(ConnectionStage.Profiles, true, StreamFailure.None, "2 profiles", TimeSpan.Zero),
            new(ConnectionStage.Stream, true, StreamFailure.None, "RTSP reachable", TimeSpan.Zero),
        };

        return new ConnectionTestResult(
            true,
            stages,
            new OnvifDeviceInformation("Dahua", "IPC-HDW2431T", "2.820", "SN1"),
            [new OnvifVideoProfile("MainToken", "mainStream", "H264", 1920, 1080, 25)],
            new Uri("rtsp://192.168.1.108:554/cam/realmonitor?channel=1&subtype=0"),
            null,
            null);
    }

    private static ConnectionTestResult Failure() => new(
        false,
        [new StageResult(ConnectionStage.Reach, false, StreamFailure.ConnectionRefused, "refused", TimeSpan.Zero)],
        null,
        [],
        null,
        null,
        null);

    private sealed class StubTester(ConnectionTestResult result) : IConnectionTester
    {
        public Task<ConnectionTestResult> TestAsync(
            DeviceEndpoint endpoint,
            string? username,
            string? password,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class MemoryDeviceStore : IDeviceStore
    {
        public List<Device> Items { get; } = [];

        public Task<IReadOnlyList<Device>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Device>>(Items);

        public Task<Device?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(device => device.Id == id));

        public Task<Device?> FindByEndpointAsync(string host, int onvifPort, int rtspPort, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(device =>
                string.Equals(device.Host, host, StringComparison.OrdinalIgnoreCase) &&
                device.OnvifPort == onvifPort &&
                device.RtspPort == rtspPort));

        public Task<Device> UpsertAsync(Device device, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(existing => existing.Id == device.Id);
            Items.Add(device);
            return Task.FromResult(device);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.RemoveAll(device => device.Id == id) > 0);
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _items = [];

        public IReadOnlyDictionary<string, string> Items => _items;

        public string Save(Guid deviceId, string secret)
        {
            var reference = ISecretStore.ReferenceFor(deviceId);
            _items[reference] = secret;
            return reference;
        }

        public string? Load(string secretReference) => _items.GetValueOrDefault(secretReference);

        public bool Delete(string secretReference) => _items.Remove(secretReference);
    }
}

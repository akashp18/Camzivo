using Camzivo.Core.Devices;
using Camzivo.Core.Security;
using Camzivo.Data.Devices;
using Camzivo.Data.Repositories;
using Xunit;

namespace Camzivo.Data.Tests.Devices;

/// <summary>
/// The NVR slice end to end, against a simulated eight-channel recorder: discover → import → rename → remove.
/// The device answers real ONVIF XML through the transport seam, so these tests cover protocol handling and the
/// persistence rules, not Dahua hardware.
/// </summary>
public sealed class NvrOnboardingTests : IClassFixture<SqliteFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private readonly SqliteFixture _fixture;
    private readonly DeviceRepository _devices;
    private readonly CameraRepository _cameras;
    private readonly MemorySecretStore _secrets = new();
    private readonly NvrOnboarding _onboarding;
    private readonly NvrDiscovery _discovery;

    public NvrOnboardingTests(SqliteFixture fixture)
    {
        _fixture = fixture;
        _devices = new DeviceRepository(fixture.Factory);
        _cameras = new CameraRepository(fixture.Factory);
        _onboarding = new NvrOnboarding(_devices, _cameras, _secrets);
        _discovery = new NvrDiscovery(new SimulatedNvr(), new AlwaysReachable());
    }

    private static NvrRegistrationRequest Request() =>
        new("Warehouse NVR", "192.168.1.200", "admin", "Sup3rSecret!");

    private async Task<(ChannelImportOutcome Outcome, NvrDiscoveryResult Discovery)> ImportAllAsync()
    {
        var discovery = await _discovery.DiscoverAsync(
            new DeviceEndpoint("192.168.1.200", 80, 554, false),
            "admin",
            "Sup3rSecret!");

        var outcome = await _onboarding.ImportAsync(Request(), discovery, [], Now);
        return (outcome, discovery);
    }

    [Fact]
    public async Task DiscoveryListsEveryChannelWithMainAndSubStreams()
    {
        var result = await _discovery.DiscoverAsync(
            new DeviceEndpoint("192.168.1.200", 80, 554, false),
            "admin",
            "Sup3rSecret!");

        Assert.True(result.Success, result.Failure?.Explanation);
        Assert.Equal(8, result.Channels.Count);
        Assert.All(result.Channels, channel =>
        {
            Assert.NotNull(channel.MainUri);
            Assert.NotNull(channel.SubUri);
        });
        Assert.NotNull(result.Services.Recording);
        Assert.Equal("channel-1", result.Channels[0].SourceToken);
        Assert.Equal("Nightwatch NVR", result.Information!.Model);
        Assert.True(result.Services.HasPtz);
    }

    [Fact]
    public async Task ImportCreatesOneRecorderWithAllChannels()
    {
        var (outcome, _) = await ImportAllAsync();

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(8, outcome.Imported);
        Assert.Empty(outcome.Skipped);
        Assert.Equal(DeviceKind.Nvr, outcome.Device!.Kind);
        Assert.NotNull(outcome.Device.SecretRef);

        var channels = await _cameras.GetForDeviceAsync(outcome.Device.Id);
        Assert.Equal(8, channels.Count);
        Assert.Equal(Enumerable.Range(1, 8), channels.Select(channel => channel.Channel));
        Assert.All(channels, channel => Assert.StartsWith("rtsp://192.168.1.200", channel.MainUri!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePasswordIsStoredOnceBehindADpapiReferenceAndNeverInTheDeviceRow()
    {
        var (outcome, _) = await ImportAllAsync();

        Assert.False(string.IsNullOrEmpty(outcome.Device!.SecretRef));
        Assert.Equal("Sup3rSecret!", _secrets.Load(outcome.Device.SecretRef!));
        Assert.DoesNotContain("Sup3rSecret", outcome.Device.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportingTheSameRecorderAgainReportsDuplicatesInsteadOfDuplicatingChannels()
    {
        var (first, discovery) = await ImportAllAsync();

        var second = await _onboarding.ImportAsync(Request(), discovery, [], Now);

        Assert.True(second.Success, second.Error);
        Assert.Equal(0, second.Imported);
        Assert.Equal(first.Device!.Id, second.Device!.Id);
        Assert.Equal(8, second.Skipped.Count);

        var channels = await _cameras.GetForDeviceAsync(first.Device.Id);
        Assert.Equal(8, channels.Count);
    }

    [Fact]
    public async Task ImportingASelectedSubsetCreatesOnlyThoseChannels()
    {
        var discovery = await _discovery.DiscoverAsync(new DeviceEndpoint("192.168.1.200", 80, 554, false), "admin", "Sup3rSecret!");

        var outcome = await _onboarding.ImportAsync(Request(), discovery, [2, 5], Now);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(2, outcome.Imported);

        var channels = await _cameras.GetForDeviceAsync(outcome.Device!.Id);
        Assert.Equal([2, 5], channels.Select(channel => channel.Channel));
    }

    [Fact]
    public async Task RenamingAChannelKeepsItsStreamAddresses()
    {
        var (outcome, _) = await ImportAllAsync();
        var channel = (await _cameras.GetForDeviceAsync(outcome.Device!.Id))[0];

        var renamed = await _onboarding.RenameChannelAsync(channel.Id, "Dock gate", Now.AddMinutes(1));

        Assert.NotNull(renamed);
        Assert.Equal("Dock gate", renamed!.Name);
        Assert.Equal(channel.MainUri, renamed.MainUri);
    }

    [Fact]
    public async Task RemovingOneChannelLeavesTheRecorderAndTheRest()
    {
        var (outcome, _) = await ImportAllAsync();
        var channel = (await _cameras.GetForDeviceAsync(outcome.Device!.Id))[2];

        Assert.True(await _onboarding.RemoveChannelAsync(channel.Id));

        var remaining = await _cameras.GetForDeviceAsync(outcome.Device.Id);
        Assert.Equal(7, remaining.Count);
        Assert.DoesNotContain(remaining, candidate => candidate.Id == channel.Id);
        Assert.NotNull(await _devices.GetAsync(outcome.Device.Id));
    }

    [Fact]
    public async Task RemovingARecorderWithItsChannelsLeavesNothingBehind()
    {
        var (outcome, _) = await ImportAllAsync();

        var removal = await _onboarding.RemoveNvrAsync(outcome.Device!.Id, NvrRemovalMode.RemoveEverything, Now);

        Assert.True(removal.Success);
        Assert.Equal(8, removal.ChannelsRemoved);
        Assert.Null(await _devices.GetAsync(outcome.Device.Id));
        Assert.Empty(await _cameras.GetForDeviceAsync(outcome.Device.Id));
        Assert.Null(_secrets.Load(outcome.Device.SecretRef!));
    }

    [Fact]
    public async Task RemovingARecorderWhileKeepingChannelsPromotesEachOneToACamera()
    {
        var (outcome, _) = await ImportAllAsync();
        var recorderId = outcome.Device!.Id;
        var channels = await _cameras.GetForDeviceAsync(recorderId);

        var removal = await _onboarding.RemoveNvrAsync(recorderId, NvrRemovalMode.KeepChannelsAsCameras, Now);

        Assert.True(removal.Success);
        Assert.Equal(8, removal.ChannelsPromoted);
        Assert.Null(await _devices.GetAsync(recorderId));
        Assert.Empty(await _cameras.GetForDeviceAsync(recorderId));

        var promoted = (await _devices.GetAllAsync()).Where(device => device.Kind == DeviceKind.Camera).ToList();
        Assert.Equal(8, promoted.Count);
        Assert.All(promoted, device => Assert.Equal("Sup3rSecret!", _secrets.Load(device.SecretRef!)));
        Assert.All(promoted, device => Assert.False(string.IsNullOrEmpty(device.StreamUriMain)));

        // Each promoted camera owns its own secret, so deleting one cannot lock the others out.
        _secrets.Delete(promoted[0].SecretRef!);
        Assert.Equal("Sup3rSecret!", _secrets.Load(promoted[1].SecretRef!));
    }

    [Fact]
    public async Task RemovingARecorderThatIsAlreadyGoneIsReportedNotThrown()
    {
        var removal = await _onboarding.RemoveNvrAsync(Guid.NewGuid(), NvrRemovalMode.RemoveEverything, Now);

        Assert.False(removal.Success);
        Assert.False(string.IsNullOrWhiteSpace(removal.Error));
    }

    [Fact]
    public async Task DiagnosticsReportIdentityAndAdvertisedProfileGCapabilities()
    {
        var (outcome, _) = await ImportAllAsync();
        var service = new DeviceDiagnosticsService(_discovery, _devices);

        var report = await service.InspectAsync(outcome.Device!, "Sup3rSecret!");

        Assert.Equal("Dahua", report.Manufacturer);
        Assert.Equal(8, report.ChannelCount);
        Assert.Contains(report.Rows, row => row.Name == "Recording search" && row.State == CapabilityState.Supported);
        Assert.Contains(report.Rows, row => row.Name == "Playback" && row.State == CapabilityState.Supported);
        Assert.Contains(report.Rows, row => row.Name == "Playback speed" && row.State == CapabilityState.Unverified);
    }

    [Fact]
    public async Task ImportingWithoutSelectingAnythingIsRefused()
    {
        var discovery = await _discovery.DiscoverAsync(new DeviceEndpoint("192.168.1.200", 80, 554, false), "admin", "Sup3rSecret!");

        var outcome = await _onboarding.ImportAsync(Request(), discovery, [99], Now);

        Assert.False(outcome.Success);
        Assert.Contains("at least one channel", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportingAFailedDiscoveryIsRefusedWithTheDeviceReason()
    {
        var failed = new NvrDiscoveryResult(
            false,
            null,
            Camzivo.Core.Devices.Onvif.OnvifServices.None,
            [],
            [],
            Camzivo.Core.Diagnostics.ConnectionDiagnostics.Explain(Camzivo.Core.Diagnostics.StreamFailure.Unauthorized, null));

        var outcome = await _onboarding.ImportAsync(Request(), failed, [], Now);

        Assert.False(outcome.Success);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
    }

    // ---- simulated recorder ---------------------------------------------------------------------------------------

    private sealed class SimulatedNvr : IOnvifTransport
    {
        public Task<OnvifHttpResponse> PostAsync(Uri uri, string body, string? authorization, CancellationToken cancellationToken)
        {
            _ = uri;
            _ = authorization;

            if (body.Contains("GetDeviceInformation", StringComparison.Ordinal))
            {
                return Task.FromResult(new OnvifHttpResponse(200, """
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
                      <tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                        <tds:Manufacturer>Dahua</tds:Manufacturer>
                        <tds:Model>Nightwatch NVR</tds:Model>
                      </tds:GetDeviceInformationResponse>
                    </s:Body></s:Envelope>
                    """));
            }

            if (body.Contains("GetCapabilities", StringComparison.Ordinal))
            {
                return Task.FromResult(new OnvifHttpResponse(200, """
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
                      <tds:GetCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                        <tds:Capabilities>
                          <tt:Media><tt:XAddr>http://192.168.1.200/onvif/media</tt:XAddr></tt:Media>
                          <tt:PTZ><tt:XAddr>http://192.168.1.200/onvif/ptz</tt:XAddr></tt:PTZ>
                          <tt:Replay><tt:XAddr>http://192.168.1.200/onvif/replay</tt:XAddr></tt:Replay>
                        </tds:Capabilities>
                      </tds:GetCapabilitiesResponse>
                    </s:Body></s:Envelope>
                    """));
            }

            if (body.Contains("GetServices", StringComparison.Ordinal))
            {
                return Task.FromResult(new OnvifHttpResponse(200, """
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
                      <tds:GetServicesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                        <tds:Service><tds:Namespace>http://www.onvif.org/ver10/media/wsdl</tds:Namespace><tds:XAddr>http://192.168.1.200/onvif/media</tds:XAddr></tds:Service>
                        <tds:Service><tds:Namespace>http://www.onvif.org/ver10/recording/wsdl</tds:Namespace><tds:XAddr>http://192.168.1.200/onvif/recording</tds:XAddr></tds:Service>
                        <tds:Service><tds:Namespace>http://www.onvif.org/ver10/search/wsdl</tds:Namespace><tds:XAddr>http://192.168.1.200/onvif/search</tds:XAddr></tds:Service>
                        <tds:Service><tds:Namespace>http://www.onvif.org/ver10/replay/wsdl</tds:Namespace><tds:XAddr>http://192.168.1.200/onvif/replay</tds:XAddr></tds:Service>
                      </tds:GetServicesResponse>
                    </s:Body></s:Envelope>
                    """));
            }

            if (body.Contains("GetRecordings", StringComparison.Ordinal))
            {
                return Task.FromResult(new OnvifHttpResponse(200, """
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
                      <trc:GetRecordingsResponse xmlns:trc="http://www.onvif.org/ver10/recording/wsdl">
                        <trc:RecordingItem><trc:RecordingToken>rec-1</trc:RecordingToken></trc:RecordingItem>
                        <trc:RecordingItem><trc:RecordingToken>rec-2</trc:RecordingToken></trc:RecordingItem>
                      </trc:GetRecordingsResponse>
                    </s:Body></s:Envelope>
                    """));
            }

            if (body.Contains("GetRecordingInformation", StringComparison.Ordinal))
            {
                var token = Between(body, "<RecordingToken>", "</RecordingToken>");
                var channel = token.EndsWith("2", StringComparison.Ordinal) ? 2 : 1;
                return Task.FromResult(new OnvifHttpResponse(200, $"""
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
                      <trc:GetRecordingInformationResponse xmlns:trc="http://www.onvif.org/ver10/recording/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                        <tt:RecordingInformation token="{token}">
                          <tt:RecordingToken>{token}</tt:RecordingToken>
                          <tt:Source><tt:SourceId>channel-{channel}</tt:SourceId><tt:Name>Channel {channel}</tt:Name></tt:Source>
                        </tt:RecordingInformation>
                      </trc:GetRecordingInformationResponse>
                    </s:Body></s:Envelope>
                    """));
            }

            if (body.Contains("GetProfiles", StringComparison.Ordinal))
            {
                return Task.FromResult(new OnvifHttpResponse(200, ProfilesXml()));
            }

            if (body.Contains("GetStreamUri", StringComparison.Ordinal))
            {
                var token = Between(body, "<ProfileToken>", "</ProfileToken>");
                var subtype = token.Contains("-sub", StringComparison.Ordinal) ? 1 : 0;
                var channel = token.Split('-')[0];

                return Task.FromResult(new OnvifHttpResponse(200, $"""
                    <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
                      <trt:GetStreamUriResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                        <trt:MediaUri>
                          <tt:Uri>rtsp://192.168.1.200:554/cam/realmonitor?channel={channel}&amp;subtype={subtype}</tt:Uri>
                        </trt:MediaUri>
                      </trt:GetStreamUriResponse>
                    </s:Body></s:Envelope>
                    """));
            }

            return Task.FromResult(new OnvifHttpResponse(500, string.Empty));
        }

        private static string ProfilesXml()
        {
            var builder = new System.Text.StringBuilder();
            builder.Append("""<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">""");

            for (var channel = 1; channel <= 8; channel++)
            {
                Append(builder, $"{channel}-main", $"Channel {channel} Main", 1920, 1080);
                Append(builder, $"{channel}-sub", $"Channel {channel} Sub", 640, 360);
            }

            builder.Append("</trt:GetProfilesResponse></s:Body></s:Envelope>");
            return builder.ToString();

            static void Append(System.Text.StringBuilder builder, string token, string name, int width, int height)
            {
                builder.Append($"""
                    <trt:Profiles token="{token}">
                      <tt:Name>{name}</tt:Name>
                      <tt:VideoEncoderConfiguration>
                        <tt:Encoding>H264</tt:Encoding>
                        <tt:Resolution><tt:Width>{width}</tt:Width><tt:Height>{height}</tt:Height></tt:Resolution>
                        <tt:RateControl><tt:FrameRateLimit>25</tt:FrameRateLimit></tt:RateControl>
                      </tt:VideoEncoderConfiguration>
                    </trt:Profiles>
                    """);
            }
        }

        private static string Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
            var to = text.IndexOf(end, from, StringComparison.Ordinal);
            return text[from..to];
        }
    }

    private sealed class AlwaysReachable : IRtspEndpointProbe
    {
        public Task<Camzivo.Core.Diagnostics.StreamFailure?> ProbeAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromResult<Camzivo.Core.Diagnostics.StreamFailure?>(null);
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _items = [];

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

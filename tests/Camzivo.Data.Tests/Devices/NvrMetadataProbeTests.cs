using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Camzivo.Data.Devices;
using Xunit;

namespace Camzivo.Data.Tests.Devices;

public sealed class NvrMetadataProbeTests
{
    private const string DeviceInfo = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
            <tds:Manufacturer>Dahua</tds:Manufacturer><tds:Model>NVR5216</tds:Model>
            <tds:FirmwareVersion>4.0</tds:FirmwareVersion><tds:SerialNumber>SN-123</tds:SerialNumber>
          </tds:GetDeviceInformationResponse>
        </s:Body></s:Envelope>
        """;

    private static string Capabilities(string media) => $"""
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <tds:GetCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
            <tds:Capabilities><tt:Media><tt:XAddr>{media}</tt:XAddr></tt:Media></tds:Capabilities>
          </tds:GetCapabilitiesResponse>
        </s:Body></s:Envelope>
        """;

    private static string Services(string media) => $"""
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <tds:GetServicesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
            <tds:Service><tds:Namespace>http://www.onvif.org/ver10/media/wsdl</tds:Namespace>
              <tds:XAddr>{media}</tds:XAddr></tds:Service>
            <tds:Service><tds:Namespace>http://www.onvif.org/ver20/ptz/wsdl</tds:Namespace>
              <tds:XAddr>http://192.168.1.108:80/onvif/ptz</tds:XAddr></tds:Service>
          </tds:GetServicesResponse>
        </s:Body></s:Envelope>
        """;

    private const string SystemTime = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <tds:GetSystemDateAndTimeResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
            <tds:SystemDateAndTime>
              <tt:UTCDateTime><tt:Time><tt:Hour>10</tt:Hour><tt:Minute>30</tt:Minute><tt:Second>5</tt:Second></tt:Time>
                <tt:Date><tt:Year>2026</tt:Year><tt:Month>11</tt:Month><tt:Day>23</tt:Day></tt:Date></tt:UTCDateTime>
              <tt:LocalDateTime><tt:Time><tt:Hour>12</tt:Hour><tt:Minute>30</tt:Minute><tt:Second>5</tt:Second></tt:Time>
                <tt:Date><tt:Year>2026</tt:Year><tt:Month>11</tt:Month><tt:Day>23</tt:Day></tt:Date></tt:LocalDateTime>
              <tt:TZ>CET-1CEST,M3.5.0,M10.5.0/3</tt:TZ>
            </tds:SystemDateAndTime>
          </tds:GetSystemDateAndTimeResponse>
        </s:Body></s:Envelope>
        """;

    private const string Profiles = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
            <trt:Profiles token="main">
              <tt:Name>Channel 1 main</tt:Name>
              <tt:VideoSourceConfiguration token="vsc1"><tt:SourceToken>src1</tt:SourceToken></tt:VideoSourceConfiguration>
              <tt:VideoEncoderConfiguration token="enc-main"><tt:Encoding>H264</tt:Encoding>
                <tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
                <tt:RateControl><tt:FrameRateLimit>25</tt:FrameRateLimit></tt:RateControl>
              </tt:VideoEncoderConfiguration>
            </trt:Profiles>
            <trt:Profiles token="sub">
              <tt:Name>Channel 1 sub</tt:Name>
              <tt:VideoSourceConfiguration token="vsc2"><tt:SourceToken>src1</tt:SourceToken></tt:VideoSourceConfiguration>
              <tt:VideoEncoderConfiguration token="enc-sub"><tt:Encoding>H264</tt:Encoding>
                <tt:Resolution><tt:Width>640</tt:Width><tt:Height>360</tt:Height></tt:Resolution>
                <tt:RateControl><tt:FrameRateLimit>15</tt:FrameRateLimit></tt:RateControl>
              </tt:VideoEncoderConfiguration>
            </trt:Profiles>
          </trt:GetProfilesResponse>
        </s:Body></s:Envelope>
        """;

    private const string VideoSources = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <trt:GetVideoSourcesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
            <trt:VideoSources token="src1"><tt:Name>Channel 1</tt:Name></trt:VideoSources>
          </trt:GetVideoSourcesResponse>
        </s:Body></s:Envelope>
        """;

    private const string VideoSourceConfigurations = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <trt:GetVideoSourceConfigurationsResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
            <trt:Configurations token="vsc1"><tt:Name>Channel 1</tt:Name></trt:Configurations>
          </trt:GetVideoSourceConfigurationsResponse>
        </s:Body></s:Envelope>
        """;

    private const string VideoEncoderConfigurations = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <trt:GetVideoEncoderConfigurationsResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
            <trt:Configurations token="enc-main"><tt:Name>Main</tt:Name><tt:Encoding>H264</tt:Encoding>
              <tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
              <tt:RateControl><tt:FrameRateLimit>25</tt:FrameRateLimit></tt:RateControl>
            </trt:Configurations>
          </trt:GetVideoEncoderConfigurationsResponse>
        </s:Body></s:Envelope>
        """;

    private const string Empty = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body /></s:Envelope>
        """;

    private static string Respond(string operation, string media = "http://192.168.1.108:80/onvif/media") => operation switch
    {
        "GetDeviceInformation" => DeviceInfo,
        "GetCapabilities" => Capabilities(media),
        "GetServices" => Services(media),
        "GetServiceCapabilities" => Empty,
        "GetSystemDateAndTime" => SystemTime,
        "GetProfiles" => Profiles,
        "GetVideoSources" => VideoSources,
        "GetVideoSourceConfigurations" => VideoSourceConfigurations,
        "GetVideoEncoderConfigurations" => VideoEncoderConfigurations,
        _ => Empty,
    };

    private static Task<NvrMetadataResult> RunAsync(IOnvifTransport transport, int maxAttempts = 20, TimeSpan? timeout = null) =>
        new NvrMetadataProbe(transport, maxAttempts, timeout ?? TimeSpan.FromSeconds(60))
            .RunAsync("192.168.1.108", 80, useTls: false, "admin", "password");

    [Fact]
    public async Task Probe_sends_only_the_metadata_operations_and_stops_after_them()
    {
        var transport = new ScriptedTransport(body => Respond(NvrReadOnlyOperations.FindOperation(body)!));

        var result = await RunAsync(transport);

        Assert.True(result.Success, result.Error);
        var expected = new[]
        {
            "GetDeviceInformation",
            "GetCapabilities",
            "GetServices",
            "GetServiceCapabilities",
            "GetSystemDateAndTime",
            "GetProfiles",
            "GetVideoSources",
            "GetVideoSourceConfigurations",
            "GetVideoEncoderConfigurations",
        };
        Assert.Equal(expected, result.Requests.Select(request => request.Operation));

        // The probe has no stream, recording, PTZ or configuration operation to send, and it never asked for one.
        Assert.DoesNotContain(result.Requests, request => request.Operation is
            "GetStreamUri" or "GetSnapshotUri" or "GetReplayUri" or "FindRecordings" or
            "GetRecordingSearchResults" or "GetRecordings" or "ContinuousMove" or "Stop");
        Assert.All(result.Requests, request => Assert.True(NvrMetadataOperations.IsAllowed(request.Operation)));
        Assert.All(transport.Calls, uri => Assert.Equal("192.168.1.108", uri.Host));
    }

    [Fact]
    public async Task Probe_reports_identity_sources_and_main_sub_relationships()
    {
        var transport = new ScriptedTransport(body => Respond(NvrReadOnlyOperations.FindOperation(body)!));

        var result = await RunAsync(transport);

        Assert.Equal("Dahua", result.Information?.Manufacturer);
        Assert.Equal("NVR5216", result.Information?.Model);
        Assert.Equal(2, result.Profiles.Count);
        Assert.Single(result.Sources);
        Assert.Single(result.Encoders);
        Assert.NotNull(result.SystemTime?.Utc);

        var main = Assert.Single(result.Relationships, relationship => relationship.Role == "main");
        var sub = Assert.Single(result.Relationships, relationship => relationship.Role == "sub");

        Assert.Equal("main", main.ProfileToken);
        Assert.Equal("src1", main.SourceToken);
        Assert.Equal("enc-main", main.EncoderToken);
        Assert.Equal(1920, main.Width);
        Assert.Equal("sub", sub.ProfileToken);
        Assert.Equal(640, sub.Width);
    }

    [Fact]
    public async Task Cross_host_media_service_is_reported_and_never_contacted()
    {
        var transport = new ScriptedTransport(body =>
        {
            var operation = NvrReadOnlyOperations.FindOperation(body)!;
            return Respond(operation, "http://10.0.0.9/onvif/media");
        });

        var result = await RunAsync(transport);

        Assert.False(result.Success);
        Assert.Contains("outside", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.BlockedEndpoints, entry => entry.Contains("Media", StringComparison.Ordinal));
        Assert.All(transport.Calls, uri => Assert.Equal("192.168.1.108", uri.Host));
        Assert.DoesNotContain(result.Requests, request => request.Operation == "GetProfiles");
        Assert.Empty(result.Profiles);
    }

    [Fact]
    public async Task Cross_host_service_is_reported_without_revealing_the_full_address()
    {
        var transport = new ScriptedTransport(body =>
        {
            var operation = NvrReadOnlyOperations.FindOperation(body)!;
            return operation is "GetServices" or "GetCapabilities"
                ? Capabilities("http://10.0.0.9/onvif/media")
                : Respond(operation);
        });

        var result = await RunAsync(transport);

        var blocked = Assert.Single(result.BlockedEndpoints);
        Assert.DoesNotContain("10.0.0.9", blocked, StringComparison.Ordinal);
        Assert.Contains("10.0.0.***", blocked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_budget_bounds_a_hostile_device()
    {
        var transport = new ChallengeAfterIdentityTransport();

        var result = await RunAsync(transport, maxAttempts: 4);

        Assert.False(result.Success);
        Assert.Equal(4, result.HttpAttempts);
        Assert.Equal(4, transport.Calls.Count);
        Assert.Contains(result.Requests, request => request.State == "BLOCKED");
    }

    [Fact]
    public async Task Rejected_credentials_stop_after_one_digest_retry()
    {
        var transport = new ScriptedTransport(
            _ => Empty,
            statusCode: 401,
            challenge: "Digest realm=\"nvr\", nonce=\"abc123\", qop=\"auth\"");

        var result = await RunAsync(transport);

        Assert.False(result.Success);
        Assert.Equal(2, result.HttpAttempts);
        Assert.Contains("401", result.Error, StringComparison.Ordinal);
        Assert.Contains(result.Requests, request => request.State == "DENIED");
    }

    [Fact]
    public async Task Overall_time_limit_stops_the_session()
    {
        var transport = new ScriptedTransport(_ => DeviceInfo, delay: TimeSpan.FromMinutes(5));
        var started = DateTime.UtcNow;

        var result = await RunAsync(transport, timeout: TimeSpan.FromMilliseconds(150));

        Assert.False(result.Success);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Contains("time limit", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Requests, request => request.State == "TIMEOUT");
    }

    [Fact]
    public async Task Report_never_contains_the_password_or_an_authorization_value()
    {
        const string secret = "Sup3rSecret!";
        var transport = new ScriptedTransport(body => Respond(NvrReadOnlyOperations.FindOperation(body)!));

        var result = await new NvrMetadataProbe(transport)
            .RunAsync("192.168.1.108", 80, useTls: false, "admin", secret);

        Assert.DoesNotContain(secret, string.Join("|", result.Requests.Select(request => request.Detail + request.Target)));
        Assert.DoesNotContain(secret, result.Error ?? string.Empty);
        Assert.DoesNotContain(result.Requests, request =>
            request.Detail.Contains("Authorization", StringComparison.OrdinalIgnoreCase) ||
            request.Target.Contains('@', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsupported_media_calls_are_reported_not_fatal()
    {
        var transport = new ScriptedTransport(body =>
        {
            var operation = NvrReadOnlyOperations.FindOperation(body)!;
            return operation is "GetVideoSources" or "GetVideoSourceConfigurations" or "GetVideoEncoderConfigurations"
                ? "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body /></s:Envelope>"
                : Respond(operation);
        }, statusCode: 200);

        var result = await new NvrMetadataProbe(new FailingOptionalTransport(transport))
            .RunAsync("192.168.1.108", 80, useTls: false, "admin", "password");

        Assert.True(result.Success, result.Error);
        Assert.Contains("GetVideoSources", result.UnsupportedOperations);
        Assert.Contains("GetVideoEncoderConfigurations", result.UnsupportedOperations);
        Assert.Equal(2, result.Profiles.Count);
    }

    /// <summary>Answers identity once, then challenges every other call forever, to prove the budget is finite.</summary>
    private sealed class ChallengeAfterIdentityTransport : IOnvifTransport
    {
        public List<Uri> Calls { get; } = [];

        public Task<OnvifHttpResponse> PostAsync(
            Uri uri,
            string body,
            string? authorization,
            CancellationToken cancellationToken)
        {
            Calls.Add(uri);

            return Task.FromResult(
                NvrReadOnlyOperations.FindOperation(body) == "GetDeviceInformation"
                    ? new OnvifHttpResponse(200, DeviceInfo)
                    : new OnvifHttpResponse(401, string.Empty, "Digest realm=\"nvr\", nonce=\"abc123\", qop=\"auth\""));
        }
    }

    private sealed class ScriptedTransport(
        Func<string, string> respond,
        int statusCode = 200,
        string? challenge = null,
        TimeSpan? delay = null) : IOnvifTransport
    {
        public List<Uri> Calls { get; } = [];

        public async Task<OnvifHttpResponse> PostAsync(
            Uri uri,
            string body,
            string? authorization,
            CancellationToken cancellationToken)
        {
            Calls.Add(uri);

            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            return statusCode is 200 or 202
                ? new OnvifHttpResponse(statusCode, respond(body))
                : new OnvifHttpResponse(statusCode, string.Empty, challenge);
        }
    }

    /// <summary>Returns HTTP 400 for the optional source/encoder metadata calls, like a device that does not implement them.</summary>
    private sealed class FailingOptionalTransport(IOnvifTransport inner) : IOnvifTransport
    {
        public Task<OnvifHttpResponse> PostAsync(
            Uri uri,
            string body,
            string? authorization,
            CancellationToken cancellationToken)
        {
            var operation = NvrReadOnlyOperations.FindOperation(body);

            return operation is "GetVideoSources" or "GetVideoSourceConfigurations" or "GetVideoEncoderConfigurations"
                ? Task.FromResult(new OnvifHttpResponse(400, string.Empty))
                : inner.PostAsync(uri, body, authorization, cancellationToken);
        }
    }
}

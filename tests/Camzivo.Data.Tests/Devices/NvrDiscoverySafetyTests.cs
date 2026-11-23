using Camzivo.Core.Devices;
using Camzivo.Core.Diagnostics;
using Camzivo.Core.Net;
using Camzivo.Data.Devices;
using Xunit;

namespace Camzivo.Data.Tests.Devices;

public sealed class NvrDiscoverySafetyTests
{
    private const string DeviceInfo = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
            <tds:Manufacturer>Dahua</tds:Manufacturer><tds:Model>NVR</tds:Model>
            <tds:FirmwareVersion>1.0</tds:FirmwareVersion><tds:SerialNumber>S1</tds:SerialNumber>
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

    private const string Profiles = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
            <trt:Profiles token="p1"><tt:Name>Channel 1</tt:Name>
              <tt:VideoEncoderConfiguration><tt:Encoding>H264</tt:Encoding>
                <tt:Resolution><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:Resolution>
              </tt:VideoEncoderConfiguration>
            </trt:Profiles>
          </trt:GetProfilesResponse>
        </s:Body></s:Envelope>
        """;

    private static string Stream(string uri) => $"""
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <trt:GetStreamUriResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
            <trt:MediaUri><tt:Uri>{uri}</tt:Uri></trt:MediaUri>
          </trt:GetStreamUriResponse>
        </s:Body></s:Envelope>
        """;

    [Fact]
    public async Task Cross_host_media_service_is_not_contacted()
    {
        var transport = new ScriptedTransport(body =>
            body.Contains("GetDeviceInformation", StringComparison.Ordinal) ? DeviceInfo
            : body.Contains("GetCapabilities", StringComparison.Ordinal) ? Capabilities("http://10.0.0.9/onvif/media")
            : body.Contains("GetServices", StringComparison.Ordinal) ? Capabilities("http://192.168.1.108/onvif/media")
            : body.Contains("GetProfiles", StringComparison.Ordinal) ? Profiles
            : body.Contains("GetStreamUri", StringComparison.Ordinal) ? Stream("rtsp://192.168.1.108:554/live")
            : "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body /></s:Envelope>");
        var probe = new RecordingProbe();
        var discovery = new NvrDiscovery(transport, probe);

        var result = await discovery.DiscoverAsync(
            new DeviceEndpoint("192.168.1.108", 80, 554, false), "admin", "password");

        Assert.False(result.Success);
        Assert.Contains("outside", result.ValidationError, StringComparison.OrdinalIgnoreCase);
        Assert.All(transport.Calls, uri => Assert.Equal("192.168.1.108", uri.Host));
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public async Task Cross_host_stream_is_not_opened()
    {
        var transport = new ScriptedTransport(body =>
            body.Contains("GetDeviceInformation", StringComparison.Ordinal) ? DeviceInfo
            : body.Contains("GetCapabilities", StringComparison.Ordinal) ? Capabilities("http://192.168.1.108/onvif/media")
            : body.Contains("GetServices", StringComparison.Ordinal) ? Capabilities("http://192.168.1.108/onvif/media")
            : body.Contains("GetProfiles", StringComparison.Ordinal) ? Profiles
            : body.Contains("GetStreamUri", StringComparison.Ordinal) ? Stream("rtsp://10.0.0.7:554/live")
            : "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body /></s:Envelope>");
        var probe = new RecordingProbe();
        var discovery = new NvrDiscovery(transport, probe);

        var result = await discovery.DiscoverAsync(
            new DeviceEndpoint("192.168.1.108", 80, 554, false), "admin", "password");

        Assert.False(result.Success);
        Assert.Contains("approved", result.ValidationError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(probe.Calls);
    }

    private sealed class ScriptedTransport(Func<string, string> respond) : IOnvifTransport
    {
        public List<Uri> Calls { get; } = [];

        public Task<OnvifHttpResponse> PostAsync(Uri uri, string body, string? authorization, CancellationToken cancellationToken)
        {
            Calls.Add(uri);
            return Task.FromResult(new OnvifHttpResponse(200, respond(body)));
        }
    }

    private sealed class RecordingProbe : IRtspEndpointProbe
    {
        public List<Uri> Calls { get; } = [];

        public Task<StreamFailure?> ProbeAsync(Uri uri, CancellationToken cancellationToken)
        {
            Calls.Add(uri);
            return Task.FromResult<StreamFailure?>(null);
        }
    }
}

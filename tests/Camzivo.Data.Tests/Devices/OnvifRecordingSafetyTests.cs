using Camzivo.Core.Devices;
using Camzivo.Data.Recording;
using Xunit;

namespace Camzivo.Data.Tests.Devices;

public sealed class OnvifRecordingSafetyTests
{
    private static string Services(string searchHost, string replayHost) => $"""
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
          <tds:GetServicesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
            <tds:Service><tds:Namespace>http://www.onvif.org/ver10/search/wsdl</tds:Namespace><tds:XAddr>http://{searchHost}/onvif/search</tds:XAddr></tds:Service>
            <tds:Service><tds:Namespace>http://www.onvif.org/ver10/replay/wsdl</tds:Namespace><tds:XAddr>http://{replayHost}/onvif/replay</tds:XAddr></tds:Service>
          </tds:GetServicesResponse>
        </s:Body></s:Envelope>
        """;

    [Fact]
    public async Task Cross_host_search_service_is_reported_without_contacting_it()
    {
        var transport = new ScriptedTransport(_ => Services("10.0.0.9", "10.0.0.9"));
        var catalog = new OnvifRecordingCatalog(transport);

        var result = await catalog.SearchAsync(
            Guid.NewGuid(),
            new Uri("http://192.168.1.108:80/onvif/device_service"),
            "admin",
            "password",
            DateOnly.FromDateTime(DateTime.Today),
            TimeZoneInfo.Local,
            "channel-1",
            CancellationToken.None,
            554);

        Assert.True(result.IsSupported == false || result.Failed);
        Assert.Contains("unapproved", result.UnsupportedReason ?? result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.All(transport.Calls, uri => Assert.Equal("192.168.1.108", uri.Host));
    }

    [Fact]
    public async Task Cross_host_replay_address_is_rejected()
    {
        var transport = new ScriptedTransport(body =>
            body.Contains("GetReplayUri", StringComparison.Ordinal)
                ? """
                  <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
                    <trp:GetReplayUriResponse xmlns:trp="http://www.onvif.org/ver10/replay/wsdl">
                      <trp:Uri>rtsp://10.0.0.7:554/replay</trp:Uri>
                    </trp:GetReplayUriResponse>
                  </s:Body></s:Envelope>
                  """
                : Services("192.168.1.108", "192.168.1.108"));
        var catalog = new OnvifRecordingCatalog(transport);

        var result = await catalog.GetReplayAsync(
            new Uri("http://192.168.1.108:80/onvif/device_service"),
            "admin",
            "password",
            "rec-1",
            CancellationToken.None,
            554);

        Assert.Null(result.ReplayUri);
        Assert.Contains("outside", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.All(transport.Calls, uri => Assert.Equal("192.168.1.108", uri.Host));
    }

    private sealed class ScriptedTransport(Func<string, string> respond) : Camzivo.Core.Devices.IOnvifTransport
    {
        public List<Uri> Calls { get; } = [];

        public Task<Camzivo.Core.Devices.OnvifHttpResponse> PostAsync(
            Uri uri,
            string body,
            string? authorization,
            CancellationToken cancellationToken)
        {
            Calls.Add(uri);
            return Task.FromResult(new Camzivo.Core.Devices.OnvifHttpResponse(200, respond(body)));
        }
    }
}

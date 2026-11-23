using Camzivo.Core.Devices.Onvif;
using Xunit;

namespace Camzivo.Core.Tests.Devices;

/// <summary>PTZ and service-discovery messages, checked against the shapes real devices return.</summary>
public sealed class OnvifPtzSoapTests
{
    private static readonly Uri Device = new("http://192.168.1.108/onvif/device_service");

    [Fact]
    public void CapabilitiesProvideAbsoluteServiceAddresses()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>
                <tds:GetCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tds:Capabilities>
                    <tt:Media><tt:XAddr>http://192.168.1.108/onvif/media_service</tt:XAddr></tt:Media>
                    <tt:PTZ><tt:XAddr>http://192.168.1.108/onvif/ptz_service</tt:XAddr></tt:PTZ>
                    <tt:Replay><tt:XAddr>http://192.168.1.108/onvif/replay_service</tt:XAddr></tt:Replay>
                  </tds:Capabilities>
                </tds:GetCapabilitiesResponse>
              </s:Body>
            </s:Envelope>
            """;

        var services = OnvifPtzSoap.ParseServices(xml, Device);

        Assert.True(services.HasPtz);
        Assert.True(services.HasReplay);
        Assert.Equal("http://192.168.1.108/onvif/ptz_service", services.Ptz!.AbsoluteUri);
        Assert.Equal("http://192.168.1.108/onvif/media_service", services.Media!.AbsoluteUri);
    }

    [Fact]
    public void RelativeServiceAddressIsResolvedAgainstTheDeviceService()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>
                <tds:GetServicesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                  <tds:Service>
                    <tds:Namespace>http://www.onvif.org/ver20/ptz/wsdl</tds:Namespace>
                    <tds:XAddr>/onvif/ptz</tds:XAddr>
                  </tds:Service>
                </tds:GetServicesResponse>
              </s:Body>
            </s:Envelope>
            """;

        var services = OnvifPtzSoap.ParseServices(xml, Device);

        Assert.Equal("http://192.168.1.108/onvif/ptz", services.Ptz!.AbsoluteUri);
        Assert.Contains("ver20/ptz", services.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedCapabilitiesAreNotFatal()
    {
        var services = OnvifPtzSoap.ParseServices("<not-xml", Device);

        Assert.False(services.HasPtz);
        Assert.Null(services.Media);
    }

    [Fact]
    public void NodesReportContinuousPanTiltSupport()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>
                <tptz:GetNodesResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tptz:PTZNode token="node1">
                    <tt:SupportedPTZSpaces>
                      <tt:ContinuousPanTiltVelocitySpace />
                      <tt:ContinuousZoomVelocitySpace />
                    </tt:SupportedPTZSpaces>
                  </tptz:PTZNode>
                </tptz:GetNodesResponse>
              </s:Body>
            </s:Envelope>
            """;

        Assert.True(OnvifPtzSoap.ParseNodes(xml, out var detail));
        Assert.Contains("zoom", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NodesWithoutContinuousSpaceAreReportedAsUnsupported()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>
                <tptz:GetNodesResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
                  <tptz:PTZNode token="node1" />
                </tptz:GetNodesResponse>
              </s:Body>
            </s:Envelope>
            """;

        Assert.False(OnvifPtzSoap.ParseNodes(xml, out _));
    }

    [Fact]
    public void PresetsAreReadFromTheDevice()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>
                <tptz:GetPresetsResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                  <tptz:Preset token="1"><tt:Name>Gate</tt:Name></tptz:Preset>
                  <tptz:Preset token="2"><tt:Name>Car park</tt:Name></tptz:Preset>
                </tptz:GetPresetsResponse>
              </s:Body>
            </s:Envelope>
            """;

        var presets = OnvifPtzSoap.ParsePresets(xml);

        Assert.Equal(2, presets.Count);
        Assert.Equal("Car park", presets[1].Name);
    }

    [Fact]
    public void ContinuousMoveClampsSpeedAndEscapesTokens()
    {
        var body = OnvifPtzSoap.ContinuousMoveBody("token&amp;", 5, -7.25, 0.5);

        Assert.Contains("x=\"1\" y=\"-1\"", body, StringComparison.Ordinal);
        Assert.Contains("x=\"0.5\"", body, StringComparison.Ordinal);
        Assert.Contains("token&amp;amp;", body, StringComparison.Ordinal);
        Assert.Contains("ContinuousMove", body, StringComparison.Ordinal);
    }

    [Fact]
    public void StopBodyNamesTheAxesItStops()
    {
        var body = OnvifPtzSoap.StopBody("Token1", panTilt: true, zoom: false);

        Assert.Contains("<PanTilt>true</PanTilt>", body, StringComparison.Ordinal);
        Assert.Contains("<Zoom>false</Zoom>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void GotoPresetBodyCarriesBothTokens()
    {
        var body = OnvifPtzSoap.GotoPresetBody("Profile1", "7");

        Assert.Contains("<ProfileToken>Profile1</ProfileToken>", body, StringComparison.Ordinal);
        Assert.Contains("<PresetToken>7</PresetToken>", body, StringComparison.Ordinal);
    }
}

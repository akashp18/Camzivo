using Camzivo.Core.Devices.Onvif;
using Xunit;

namespace Camzivo.Core.Tests;

public class WsDiscoveryParserTests
{
    private const string DahuaStyleResponse = """
        <?xml version="1.0" encoding="UTF-8"?>
        <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope"
                      xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                      xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing">
          <env:Header/>
          <env:Body>
            <d:ProbeMatches>
              <d:ProbeMatch>
                <a:EndpointReference>
                  <a:Address>urn:uuid:5f1e7a52-0d7e-4f0e-9a41-0c0a1b2c3d4e</a:Address>
                </a:EndpointReference>
                <d:Types>dn:NetworkVideoTransmitter tds:Device</d:Types>
                <d:Scopes>onvif://www.onvif.org/type/video_encoder onvif://www.onvif.org/name/IPC-HDBW2231E onvif://www.onvif.org/hardware/HDBW2231E onvif://www.onvif.org/location/warehouse%20north</d:Scopes>
                <d:XAddrs>http://192.168.18.11/onvif/device_service</d:XAddrs>
                <d:MetadataVersion>1</d:MetadataVersion>
              </d:ProbeMatch>
            </d:ProbeMatches>
          </env:Body>
        </env:Envelope>
        """;

    private const string HikvisionStyleResponse = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope"
                           xmlns:wsdd="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                           xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing">
          <SOAP-ENV:Body>
            <wsdd:ProbeMatches>
              <wsdd:ProbeMatch>
                <wsa:EndpointReference><wsa:Address>uuid:11111111-2222-3333-4444-555555555555</wsa:Address></wsa:EndpointReference>
                <wsdd:Types>dn:NetworkVideoTransmitter</wsdd:Types>
                <wsdd:Scopes>onvif://www.onvif.org/type/NetworkVideoTransmitter onvif://www.onvif.org/name/DS-2CD2143G2-I</wsdd:Scopes>
                <wsdd:XAddrs>http://192.168.18.21:8000/onvif/device_service http://[fe80::1]:8000/onvif/device_service</wsdd:XAddrs>
              </wsdd:ProbeMatch>
            </wsdd:ProbeMatches>
          </SOAP-ENV:Body>
        </SOAP-ENV:Envelope>
        """;

    [Fact]
    public void ParsesAVendorResponseIncludingScopeMetadata()
    {
        var devices = WsDiscoveryParser.Parse(DahuaStyleResponse);

        var device = Assert.Single(devices);
        Assert.Equal("urn:uuid:5f1e7a52-0d7e-4f0e-9a41-0c0a1b2c3d4e", device.EndpointReference);
        Assert.Equal("IPC-HDBW2231E", device.Name);
        Assert.Equal("HDBW2231E", device.Hardware);
        Assert.Equal("warehouse north", device.Location);
        Assert.Equal(2, device.Types.Count);
        var address = Assert.Single(device.Addresses);
        Assert.Equal("192.168.18.11", address.Host);
        Assert.Equal("/onvif/device_service", address.AbsolutePath);
        Assert.Equal("IPC-HDBW2231E", device.DisplayName);
    }

    [Fact]
    public void ParsesAlternativeNamespacePrefixesAndMultipleAddresses()
    {
        var devices = WsDiscoveryParser.Parse(HikvisionStyleResponse);

        var device = Assert.Single(devices);
        Assert.Equal("DS-2CD2143G2-I", device.DisplayName);
        Assert.Equal(2, device.Addresses.Count);
        Assert.Equal(8000, device.Addresses[0].Port);
        Assert.True(device.Addresses[1].HostNameType == UriHostNameType.IPv6);
    }

    [Fact]
    public void EmptyOrBlankInputIsNotAnError()
    {
        Assert.Empty(WsDiscoveryParser.Parse(string.Empty));
        Assert.Empty(WsDiscoveryParser.Parse("   "));
    }

    [Fact]
    public void AResponseWithoutMatchesYieldsNoDevices()
    {
        const string noMatches = """
            <env:Envelope xmlns:env="http://www.w3.org/2003/05/soap-envelope"><env:Body/></env:Envelope>
            """;

        Assert.Empty(WsDiscoveryParser.Parse(noMatches));
    }

    [Fact]
    public void MalformedXmlIsReportedAsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => WsDiscoveryParser.Parse("<Envelope><Body>"));
    }

    [Fact]
    public void DuplicateProbeMatchesForTheSameEndpointAreCollapsed()
    {
        var duplicated = DahuaStyleResponse.Replace(
            "<d:ProbeMatches>",
            "<d:ProbeMatches>" + DahuaStyleResponse[DahuaStyleResponse.IndexOf("<d:ProbeMatch>", StringComparison.Ordinal)..DahuaStyleResponse.IndexOf("</d:ProbeMatch>", StringComparison.Ordinal)] + "</d:ProbeMatch>",
            StringComparison.Ordinal);

        var devices = WsDiscoveryParser.Parse(duplicated);

        Assert.Single(devices);
    }
}

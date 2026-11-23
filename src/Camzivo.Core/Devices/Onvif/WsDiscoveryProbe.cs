using System.Globalization;
using System.Xml;

namespace Camzivo.Core.Devices.Onvif;

/// <summary>
/// Builds the WS-Discovery probe that makes ONVIF cameras announce themselves.
///
/// The message id and timestamp are parameters so the exact datagram can be asserted in a test; a random id is the
/// production default. Parsing replies is <see cref="WsDiscoveryParser"/>'s job.
/// </summary>
public static class WsDiscoveryProbe
{
    public const string DiscoveryAddress = "soap.udp://239.255.255.250:3702";

    private const string DevicesType = "dn:NetworkVideoTransmitter";

    public static string BuildMessage(Guid messageId, DateTimeOffset created)
    {
        var createdText = created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                        xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
              <e:Header>
                <w:MessageID>uuid:{messageId:D}</w:MessageID>
                <w:To e:mustUnderstand="true">{DiscoveryAddress}</w:To>
                <w:Action e:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
                <w:ReplyTo>
                  <w:Address>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</w:Address>
                </w:ReplyTo>
                <d:AppSequence InstanceId="1" MessageNumber="1" />
              </e:Header>
              <e:Body>
                <d:Probe>
                  <d:Types>{DevicesType}</d:Types>
                </d:Probe>
              </e:Body>
            </e:Envelope>
            """;
    }

    /// <summary>
    /// Reads a datagram and returns the devices it announced. Malformed datagrams return an empty list: discovery is
    /// a noisy broadcast medium and one bad packet must not abort the scan.
    /// </summary>
    public static IReadOnlyList<DiscoveredDevice> ParseDatagram(string datagram)
    {
        try
        {
            return WsDiscoveryParser.Parse(datagram);
        }
        catch (InvalidDataException)
        {
            return Array.Empty<DiscoveredDevice>();
        }
        catch (XmlException)
        {
            return Array.Empty<DiscoveredDevice>();
        }
    }

    /// <summary>Production defaults: a fresh message id and the current UTC timestamp.</summary>
    public static string BuildMessage() => BuildMessage(Guid.NewGuid(), DateTimeOffset.UtcNow);
}

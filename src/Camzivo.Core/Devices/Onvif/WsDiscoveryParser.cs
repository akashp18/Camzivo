using System.Xml;
using System.Xml.Linq;

namespace Camzivo.Core.Devices.Onvif;

/// <summary>A device that answered a WS-Discovery probe.</summary>
public sealed record DiscoveredDevice(
    string EndpointReference,
    IReadOnlyList<Uri> Addresses,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Types,
    string? Name,
    string? Hardware,
    string? Location)
{
    public Uri? PrimaryAddress => Addresses.Count > 0 ? Addresses[0] : null;

    /// <summary>Best available human label; never null so the UI cannot render an empty row.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name!
        : !string.IsNullOrWhiteSpace(Hardware) ? Hardware!
        : PrimaryAddress?.Host ?? EndpointReference;
}

/// <summary>
/// Parses WS-Discovery <c>ProbeMatches</c> SOAP bodies into device records.
///
/// Deliberately namespace-prefix agnostic: vendors use <c>d:</c>, <c>wsd:</c>, <c>dn:</c> and default
/// namespaces interchangeably, so matching is by local name. This is the part of ONVIF that breaks most often
/// in the field, which is why it is a pure function with fixture tests.
/// </summary>
public static class WsDiscoveryParser
{
    private const string OnvifScopePrefix = "onvif://www.onvif.org/";

    public static IReadOnlyList<DiscoveredDevice> Parse(string soapXml)
    {
        if (string.IsNullOrWhiteSpace(soapXml))
        {
            return Array.Empty<DiscoveredDevice>();
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(soapXml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 512 * 1024,
                });
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("WS-Discovery response is not well-formed XML.", exception);
        }

        var matches = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ProbeMatch")
            .ToList();

        if (matches.Count == 0)
        {
            return Array.Empty<DiscoveredDevice>();
        }

        var results = new List<DiscoveredDevice>(matches.Count);
        foreach (var match in matches)
        {
            var endpoint = match
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "EndpointReference")?
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Address")?
                .Value
                .Trim() ?? string.Empty;

            var addresses = SplitValues(match, "XAddrs")
                .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
                .Where(uri => uri is not null)
                .Select(uri => uri!)
                .ToList();

            var scopes = SplitValues(match, "Scopes");
            var types = SplitValues(match, "Types");

            results.Add(new DiscoveredDevice(
                endpoint,
                addresses,
                scopes,
                types,
                ExtractScope(scopes, "name"),
                ExtractScope(scopes, "hardware"),
                ExtractScope(scopes, "location")));
        }

        // A multi-homed device answers once per interface; keep one row, richest address list first.
        return results
            .GroupBy(device => string.IsNullOrEmpty(device.EndpointReference) ? device.DisplayName : device.EndpointReference)
            .Select(group => group.First())
            .ToList();
    }

    private static List<string> SplitValues(XElement match, string localName) =>
        match
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)?
            .Value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList()
        ?? [];

    private static string? ExtractScope(IReadOnlyList<string> scopes, string key)
    {
        var prefix = OnvifScopePrefix + key + "/";
        foreach (var scope in scopes)
        {
            if (!scope.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = scope[prefix.Length..];
            if (value.Length == 0)
            {
                continue;
            }

            var decoded = Uri.UnescapeDataString(value).Trim();
            return decoded.Length == 0 ? null : decoded;
        }

        return null;
    }
}

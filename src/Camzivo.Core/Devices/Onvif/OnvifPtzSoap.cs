using System.Xml.Linq;

namespace Camzivo.Core.Devices.Onvif;

/// <summary>
/// ONVIF PTZ and service-discovery messages.
///
/// PTZ is sent to the PTZ service endpoint the device advertises (not the media endpoint), which is why capabilities
/// are read first. Bodies are the SOAP *body* only; <see cref="OnvifSoap.BuildEnvelope"/> adds the security header.
/// </summary>
public static class OnvifPtzSoap
{
    public const string PtzNamespace = "http://www.onvif.org/ver20/ptz/wsdl";

    public sealed record Preset(string Token, string? Name);

    public static string CapabilitiesBody() => """
        <GetCapabilities xmlns="http://www.onvif.org/ver10/device/wsdl">
          <Category>All</Category>
        </GetCapabilities>
        """;

    public static string ServicesBody() => """
        <GetServices xmlns="http://www.onvif.org/ver10/device/wsdl">
          <IncludeCapability>false</IncludeCapability>
        </GetServices>
        """;

    public static string PtzNodesBody() => $"""<GetNodes xmlns="{PtzNamespace}" />""";

    public static string PresetsBody(string profileToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileToken);
        return $"""<GetPresets xmlns="{PtzNamespace}"><ProfileToken>{Escape(profileToken)}</ProfileToken></GetPresets>""";
    }

    public static string ContinuousMoveBody(string profileToken, double pan, double tilt, double zoom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileToken);
        return $"""
            <ContinuousMove xmlns="{PtzNamespace}">
              <ProfileToken>{Escape(profileToken)}</ProfileToken>
              <Velocity>
                <PanTilt xmlns="http://www.onvif.org/ver10/schema" x="{Number(pan)}" y="{Number(tilt)}" />
                <Zoom xmlns="http://www.onvif.org/ver10/schema" x="{Number(zoom)}" />
              </Velocity>
            </ContinuousMove>
            """;
    }

    public static string StopBody(string profileToken, bool panTilt = true, bool zoom = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileToken);
        return $"""
            <Stop xmlns="{PtzNamespace}">
              <ProfileToken>{Escape(profileToken)}</ProfileToken>
              <PanTilt>{Bool(panTilt)}</PanTilt>
              <Zoom>{Bool(zoom)}</Zoom>
            </Stop>
            """;
    }

    public static string GotoPresetBody(string profileToken, string presetToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(presetToken);
        return $"""
            <GotoPreset xmlns="{PtzNamespace}">
              <ProfileToken>{Escape(profileToken)}</ProfileToken>
              <PresetToken>{Escape(presetToken)}</PresetToken>
            </GotoPreset>
            """;
    }

    /// <summary>
    /// Reads service addresses from GetCapabilities and/or GetServices. Relative XAddr values are resolved against the
    /// device service URI, because devices do publish relative paths.
    /// </summary>
    public static OnvifServices ParseServices(string soapXml, Uri deviceServiceUri)
    {
        ArgumentNullException.ThrowIfNull(soapXml);
        ArgumentNullException.ThrowIfNull(deviceServiceUri);

        XDocument document;
        try
        {
            document = XDocument.Parse(soapXml);
        }
        catch (System.Xml.XmlException)
        {
            return OnvifServices.None;
        }

        Uri? Find(string localName)
        {
            var element = document
                .Descendants()
                .FirstOrDefault(candidate => candidate.Name.LocalName == localName)
                ?.Descendants()
                .FirstOrDefault(candidate => candidate.Name.LocalName == "XAddr")
                ?.Value
                .Trim();

            return Resolve(element, deviceServiceUri);
        }

        // GetServices lists one <Service> per namespace with its own XAddr; match on the namespace, not on a
        // <Media>/<PTZ> wrapper, or a device that only answers GetServices looks like it has no PTZ at all.
        var names = new List<string>();
        Uri? fromServices(string namespaceFragment)
        {
            foreach (var service in document.Descendants().Where(element => element.Name.LocalName == "Service"))
            {
                var ns = service.Descendants().FirstOrDefault(child => child.Name.LocalName == "Namespace")?.Value?.Trim();

                if (string.IsNullOrWhiteSpace(ns))
                {
                    continue;
                }

                names.Add(ns);

                if (ns.Contains(namespaceFragment, StringComparison.OrdinalIgnoreCase))
                {
                    var address = service.Descendants().FirstOrDefault(child => child.Name.LocalName == "XAddr")?.Value?.Trim();
                    return Resolve(address, deviceServiceUri);
                }
            }

            return null;
        }

        var servicesMedia = fromServices("/media/");
        var servicesPtz = fromServices("/ptz/");
        var servicesReplay = fromServices("/replay/");
        var servicesRecording = fromServices("/recording/");
        var servicesSearch = fromServices("/search/");

        return new OnvifServices(
            Find("Media") ?? Find("Media2") ?? servicesMedia,
            Find("PTZ") ?? servicesPtz,
            Find("Replay") ?? servicesReplay,
            names.Distinct(StringComparer.Ordinal).ToList(),
            Find("Recording") ?? servicesRecording,
            Find("Search") ?? servicesSearch);
    }

    /// <summary>True when the device answered GetNodes, which is the honest test for "this PTZ works".</summary>
    public static bool ParseNodes(string soapXml, out string detail)
    {
        detail = string.Empty;

        try
        {
            var document = XDocument.Parse(soapXml);
            var nodes = document.Descendants().Where(element => element.Name.LocalName == "PTZNode").ToList();

            if (nodes.Count == 0)
            {
                detail = "The device advertised PTZ but returned no PTZ node.";
                return false;
            }

            var continuous = document.Descendants().Any(element => element.Name.LocalName == "ContinuousPanTiltVelocitySpace");
            var zoom = document.Descendants().Any(element => element.Name.LocalName == "ContinuousZoomVelocitySpace");

            detail = continuous
                ? zoom ? "Continuous pan/tilt and zoom supported" : "Continuous pan/tilt supported (no zoom)"
                : "PTZ node present, continuous movement not advertised";

            return continuous;
        }
        catch (System.Xml.XmlException)
        {
            detail = "The PTZ node response was not valid XML.";
            return false;
        }
    }

    public static IReadOnlyList<Preset> ParsePresets(string soapXml)
    {
        try
        {
            var document = XDocument.Parse(soapXml);
            return document
                .Descendants()
                .Where(element => element.Name.LocalName == "Preset")
                .Select(element => new Preset(
                    element.Attribute("token")?.Value ?? element.Attribute("Token")?.Value ?? string.Empty,
                    element.Descendants().FirstOrDefault(child => child.Name.LocalName == "Name")?.Value?.Trim()))
                .Where(preset => preset.Token.Length > 0)
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static Uri? Resolve(string? text, Uri baseUri) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : Uri.TryCreate(baseUri, text, out var uri) ? uri : null;

    private static string Number(double value) =>
        Math.Clamp(value, -1d, 1d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value);
}

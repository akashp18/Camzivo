using System.Xml;
using System.Xml.Linq;
using Camzivo.Core.Security;

namespace Camzivo.Core.Devices.Onvif;

public sealed record OnvifDeviceInformation(string? Manufacturer, string? Model, string? FirmwareVersion, string? SerialNumber);

/// <summary>Read-only device clock facts, used by diagnostics; never used to change the device.</summary>
public sealed record OnvifSystemTime(DateTimeOffset? Utc, string? Local, string? TimeZone);

public sealed record OnvifVideoProfile(
    string Token,
    string? Name,
    string? Encoding,
    int Width,
    int Height,
    double? FrameRate,
    bool HasAudio = false,
    string? SourceToken = null,
    string? EncoderToken = null)
{
    /// <summary>Rough pixel-rate used to pick the heaviest profile as "main".</summary>
    public long PixelRate => (long)Width * Height * (long)(FrameRate ?? 25d);

    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "unknown";
}

public sealed record OnvifMedia(Uri? StreamUri, string? ProfileToken, string? Encoding);

public sealed record OnvifVideoSource(string Token, string? Name);

public sealed record OnvifVideoEncoderConfiguration(
    string Token,
    string? Name,
    string? Encoding,
    int Width,
    int Height,
    double? FrameRate);

/// <summary>
/// ONVIF SOAP requests and responses for the four things onboarding needs: who are you, what can you do, which
/// profiles exist, and give me a stream URI.
///
/// Parsing matches by local name (vendor prefixes and namespace versions differ: ver10 vs ver20 media). All XML is
/// treated as untrusted: DTDs are prohibited, entity resolution is off, and the document size is capped.
/// </summary>
public static class OnvifSoap
{
    public const string SoapEnvelopeNamespace = "http://www.w3.org/2003/05/soap-envelope";

    public const string DeviceNamespace = "http://www.onvif.org/ver10/device/wsdl";

    public const string MediaNamespace = "http://www.onvif.org/ver10/media/wsdl";

    public const string SchemaNamespace = "http://www.onvif.org/ver10/schema";

    private const int MaxCharacters = 1024 * 1024;

    public static Uri DeviceServiceUri(string host, int port, bool useTls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var scheme = useTls ? "https" : "http";
        var hostPart = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
        return new Uri($"{scheme}://{hostPart}:{port}/onvif/device_service");
    }

    public static string BuildEnvelope(string bodyXml, string? username = null, string? password = null, DateTimeOffset? created = null, byte[]? nonce = null)
    {
        var header = string.Empty;

        if (!string.IsNullOrWhiteSpace(username))
        {
            var tokenNonce = nonce ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            header = "<s:Header>" +
                     OnvifUsernameToken.SecurityHeader(username, password ?? string.Empty, tokenNonce, created ?? DateTimeOffset.UtcNow) +
                     "</s:Header>";
        }

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="{SoapEnvelopeNamespace}">{header}<s:Body>{bodyXml}</s:Body></s:Envelope>
            """;
    }

    // These return the SOAP *body* only; BuildEnvelope wraps it with the security header. Returning a full envelope
    // here once caused the tester to nest envelopes, which devices reject with a generic fault.
    public static string DeviceInformationBody() => $"<GetDeviceInformation xmlns=\"{DeviceNamespace}\" />";

    public static string ProfilesBody() => $"<GetProfiles xmlns=\"{MediaNamespace}\" />";

    public static string VideoSourcesBody() => $"<GetVideoSources xmlns=\"{MediaNamespace}\" />";

    public static string VideoSourceConfigurationsBody() =>
        $"<GetVideoSourceConfigurations xmlns=\"{MediaNamespace}\" />";

    public static string VideoEncoderConfigurationsBody() =>
        $"<GetVideoEncoderConfigurations xmlns=\"{MediaNamespace}\" />";

    public static string SystemDateAndTimeBody() => $"<GetSystemDateAndTime xmlns=\"{DeviceNamespace}\" />";

    public static string ServiceCapabilitiesBody() => $"<GetServiceCapabilities xmlns=\"{DeviceNamespace}\" />";

    public static string StreamUriBody(string profileToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileToken);

        var safeToken = System.Security.SecurityElement.Escape(profileToken);
        return $"""
             <GetStreamUri xmlns="{MediaNamespace}">
               <StreamSetup>
                 <Stream xmlns="{SchemaNamespace}">RTP-Unicast</Stream>
                 <Transport xmlns="{SchemaNamespace}"><Protocol>RTSP</Protocol></Transport>
               </StreamSetup>
               <ProfileToken>{safeToken}</ProfileToken>
             </GetStreamUri>
             """;
    }

    public static OnvifDeviceInformation ParseDeviceInformation(string soapXml)
    {
        var root = Load(soapXml);

        string? Find(string localName) => root
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)?
            .Value
            .Trim();

        return new OnvifDeviceInformation(Find("Manufacturer"), Find("Model"), Find("FirmwareVersion"), Find("SerialNumber"));
    }

    public static IReadOnlyList<OnvifVideoProfile> ParseProfiles(string soapXml)
    {
        var root = Load(soapXml);

        // A camera or proxy that answers 200 with an HTML login page is well-formed XML but not an ONVIF reply.
        // Reporting "no profiles" there would send the operator after the wrong problem.
        if (!root.Descendants().Any(element => element.Name.LocalName is "GetProfilesResponse" or "Profiles"))
        {
            throw new InvalidDataException("Response is not an ONVIF GetProfiles response.");
        }

        var profiles = new List<OnvifVideoProfile>();
        foreach (var element in root.Descendants().Where(element => element.Name.LocalName == "Profiles"))
        {
            var token = element.Attribute("token")?.Value ?? element.Attribute("Token")?.Value;
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var sourceConfiguration = element
                .Descendants()
                .FirstOrDefault(child => child.Name.LocalName == "VideoSourceConfiguration");
            var sourceToken = sourceConfiguration?
                .Descendants()
                .FirstOrDefault(child => child.Name.LocalName == "SourceToken")?
                .Value
                .Trim();

            var encoder = element
                .Descendants()
                .FirstOrDefault(child => child.Name.LocalName == "VideoEncoderConfiguration");

            var encoderToken = encoder?.Attribute("token")?.Value ?? encoder?.Attribute("Token")?.Value;

            var encoding = encoder?.Descendants().FirstOrDefault(child => child.Name.LocalName == "Encoding")?.Value;
            var resolution = encoder?.Descendants().FirstOrDefault(child => child.Name.LocalName == "Resolution");
            var width = ParseInt(resolution?.Descendants().FirstOrDefault(child => child.Name.LocalName == "Width")?.Value);
            var height = ParseInt(resolution?.Descendants().FirstOrDefault(child => child.Name.LocalName == "Height")?.Value);

            var rateElement = encoder?
                .Descendants()
                .FirstOrDefault(child => child.Name.LocalName == "RateControl")?
                .Descendants()
                .FirstOrDefault(child => child.Name.LocalName == "FrameRateLimit")?.Value;

            // A profile that carries an audio encoder can carry audio in its stream. This is an advertised
            // capability, not proof that audio is enabled on the channel or that this build plays it.
            var hasAudio = element.Descendants().Any(child => child.Name.LocalName is "AudioEncoderConfiguration" or "AudioSourceConfiguration");

            profiles.Add(new OnvifVideoProfile(
                token!,
                element.Descendants().FirstOrDefault(child => child.Name.LocalName == "Name")?.Value?.Trim(),
                encoding?.Trim(),
                width,
                height,
                double.TryParse(rateElement, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate) ? rate : null,
                hasAudio,
                string.IsNullOrWhiteSpace(sourceToken) ? null : sourceToken,
                string.IsNullOrWhiteSpace(encoderToken) ? null : encoderToken));
        }

        return profiles;
    }

    public static IReadOnlyList<OnvifVideoSource> ParseVideoSources(string soapXml)
    {
        var root = Load(soapXml);

        // ONVIF names each repeated source element <VideoSources>; some firmware wraps them in a container with
        // <VideoSource> children. Both shapes are accepted so a real device is not reported as having no sources.
        var entries = root.Descendants()
            .Where(element => element.Name.LocalName == "VideoSources")
            .SelectMany(element =>
            {
                var children = element.Elements()
                    .Where(child => child.Name.LocalName == "VideoSource")
                    .ToList();
                return children.Count > 0 ? children : [element];
            });

        return entries
            .Select(element => new OnvifVideoSource(
                element.Attribute("token")?.Value ?? element.Attribute("Token")?.Value ?? string.Empty,
                element.Descendants().FirstOrDefault(child => child.Name.LocalName == "Name")?.Value?.Trim()))
            .Where(source => source.Token.Length > 0)
            .ToList();
    }

    public static IReadOnlyList<OnvifVideoEncoderConfiguration> ParseVideoEncoderConfigurations(string soapXml)
    {
        var root = Load(soapXml);

        // ONVIF returns the list as <Configurations> entries; some firmware repeats the element name instead.
        var entries = root.Descendants()
            .Where(element => element.Name.LocalName == "Configurations")
            .ToList();

        if (entries.Count == 0)
        {
            entries = root.Descendants()
                .Where(element => element.Name.LocalName == "VideoEncoderConfiguration")
                .ToList();
        }

        return entries
            .Select(element =>
            {
                var resolution = element.Descendants().FirstOrDefault(child => child.Name.LocalName == "Resolution");
                var width = ParseInt(resolution?.Descendants().FirstOrDefault(child => child.Name.LocalName == "Width")?.Value);
                var height = ParseInt(resolution?.Descendants().FirstOrDefault(child => child.Name.LocalName == "Height")?.Value);
                var rate = element
                    .Descendants()
                    .FirstOrDefault(child => child.Name.LocalName == "RateControl")?
                    .Descendants()
                    .FirstOrDefault(child => child.Name.LocalName == "FrameRateLimit")?.Value;

                return new OnvifVideoEncoderConfiguration(
                    element.Attribute("token")?.Value ?? element.Attribute("Token")?.Value ?? string.Empty,
                    element.Descendants().FirstOrDefault(child => child.Name.LocalName == "Name")?.Value?.Trim(),
                    element.Descendants().FirstOrDefault(child => child.Name.LocalName == "Encoding")?.Value?.Trim(),
                    width,
                    height,
                    double.TryParse(rate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var frameRate) ? frameRate : null);
            })
            .Where(configuration => configuration.Token.Length > 0)
            .ToList();
    }


    public static OnvifSystemTime ParseSystemDateAndTime(string soapXml)
    {
        try
        {
            var root = Load(soapXml);
            var utc = ReadDatePart(root, "UTCDateTime");
            var local = ReadDatePart(root, "LocalDateTime");
            var zone = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "TZ")?.Value?.Trim();
            var localText = local is null ? null : local.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            return new OnvifSystemTime(utc, localText, string.IsNullOrWhiteSpace(zone) ? null : zone);
        }
        catch (InvalidDataException)
        {
            return new OnvifSystemTime(null, null, null);
        }
    }

    public static OnvifMedia ParseStreamUri(string soapXml)
    {
        var root = Load(soapXml);

        var mediaUri = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "MediaUri");
        var uriText = mediaUri?
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Uri")?
            .Value
            .Trim();

        return new OnvifMedia(
            Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ? uri : null,
            null,
            null);
    }

    /// <summary>
    /// Picks the main and sub profiles: the heaviest pixel-rate profile is main, the lightest distinct one is sub.
    /// Returns (null, null) when the device exposes no usable profile.
    /// </summary>
    public static (OnvifVideoProfile? Main, OnvifVideoProfile? Sub) SelectStreams(IReadOnlyList<OnvifVideoProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var measured = profiles
            .Where(profile => profile.Width > 0 && profile.Height > 0)
            .OrderByDescending(profile => profile.PixelRate)
            .ToList();

        // Dahua recorders (and some Hikvision firmware) publish profiles whose VideoEncoderConfiguration carries no
        // Resolution element in GetProfiles; the resolution lives only in GetVideoEncoderConfigurations. Rejecting
        // those profiles reported "no video profile" on a recorder that streams perfectly well, so a profile that
        // still describes video — an encoder or source token, or an encoding — is accepted with an unknown
        // resolution. Nothing is stored on the strength of this guess: the stream and first-frame stages remain the
        // real gate, and they run on the token selected here.
        var usable = measured.Count > 0
            ? measured
            : profiles
                .Where(profile => profile.Width <= 0 || profile.Height <= 0)
                .Where(profile =>
                    !string.IsNullOrWhiteSpace(profile.EncoderToken) ||
                    !string.IsNullOrWhiteSpace(profile.SourceToken) ||
                    !string.IsNullOrWhiteSpace(profile.Encoding))
                .ToList();

        if (usable.Count == 0)
        {
            return (null, null);
        }

        var main = usable[0];
        var sub = usable.FirstOrDefault(profile => profile.Token != main.Token);
        return (main, sub);
    }

    /// <summary>Classifies a SOAP fault or HTTP status into the diagnostics vocabulary.</summary>
    public static Diagnostics.StreamFailure ClassifyFailure(int statusCode, string? body)
    {
        if (statusCode is 401)
        {
            return Diagnostics.StreamFailure.Unauthorized;
        }

        if (statusCode is 403)
        {
            return Diagnostics.StreamFailure.Forbidden;
        }

        if (statusCode is 404)
        {
            return Diagnostics.StreamFailure.NotFound;
        }

        if (statusCode is 405)
        {
            return Diagnostics.StreamFailure.RtspMethodNotAllowed;
        }

        if (statusCode >= 500)
        {
            return Diagnostics.StreamFailure.ProtocolViolation;
        }

        return Diagnostics.StreamFailure.Unknown;
    }

    private static XDocument Load(string soapXml)
    {
        if (string.IsNullOrWhiteSpace(soapXml))
        {
            throw new InvalidDataException("ONVIF response was empty.");
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(soapXml),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxCharacters,
                });
            return XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("ONVIF response is not well-formed XML.", exception);
        }
    }

    private static DateTimeOffset? ReadDatePart(XDocument root, string containerName)
    {
        var container = root.Descendants().FirstOrDefault(element => element.Name.LocalName == containerName);
        if (container is null)
        {
            return null;
        }

        int Read(string name) => ParseInt(container.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value);

        try
        {
            return new DateTimeOffset(
                Read("Year"), Read("Month"), Read("Day"), Read("Hour"), Read("Minute"), Read("Second"),
                TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int ParseInt(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
}

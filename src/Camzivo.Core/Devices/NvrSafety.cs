using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Camzivo.Core.Devices;

/// <summary>
/// The exact network boundary approved for one recorder session.
///
/// Camzivo must never follow an ONVIF service address to a different host or port, and must never open a camera
/// address returned by a recorder. Host comparison is deliberately literal: a hostname and its resolved IP are not
/// interchangeable here. An operator can approve that separately rather than have the client infer it.
/// </summary>
public sealed record NvrAccessPolicy(string Host, int OnvifPort, int RtspPort, bool UseTls)
{
    public static NvrAccessPolicy ForEndpoint(DeviceEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new NvrAccessPolicy(endpoint.Host.Trim(), endpoint.OnvifPort, endpoint.RtspPort, endpoint.UseTls);
    }

    public static NvrAccessPolicy ForService(Uri serviceUri, int rtspPort = 554)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        return new NvrAccessPolicy(
            serviceUri.Host,
            serviceUri.Port,
            rtspPort,
            serviceUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
    }

    public string ExpectedOnvifOrigin => $"{(UseTls ? "https" : "http")}://{Host}:{OnvifPort}";

    public string ExpectedRtspOrigin => $"rtsp://{Host}:{RtspPort}";

    public bool AllowsOnvifUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return string.IsNullOrEmpty(uri.UserInfo) &&
               uri.Scheme.Equals(UseTls ? "https" : "http", StringComparison.OrdinalIgnoreCase) &&
               HostEquals(uri) &&
               uri.Port == OnvifPort;
    }

    public bool AllowsStreamUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return string.IsNullOrEmpty(uri.UserInfo) &&
               uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase) &&
               HostEquals(uri) &&
               uri.Port == RtspPort;
    }

    public string Describe()
    {
        return $"ONVIF {ExpectedOnvifOrigin}; RTSP {ExpectedRtspOrigin}; no other host, port or redirect is allowed";
    }

    private bool HostEquals(Uri uri) =>
        uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);
}

public sealed class NvrAccessDeniedException(string operation, Uri uri, string reason)
    : InvalidOperationException($"{operation} was blocked: {reason} ({NvrSafety.Redact(uri)})")
{
    public string Operation { get; } = operation;

    public Uri Uri { get; } = uri;

    public string Reason { get; } = reason;
}

/// <summary>
/// A positive allowlist of ONVIF operations that Camzivo may send to a recorder.
///
/// This is a protocol boundary, not a UI preference. Unknown operations are denied by default, so a future feature
/// cannot accidentally become a configuration write just by calling the transport. The only non-Get entries are the
/// Profile G search session operations and EndSearch, which release a temporary search rather than change persistent
/// device state.
/// </summary>
public static class NvrReadOnlyOperations
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        // Device and service discovery.
        "GetDeviceInformation",
        "GetCapabilities",
        "GetServices",
        "GetServiceCapabilities",
        "GetSystemDateAndTime",

        // Media and stream addresses.
        "GetProfiles",
        "GetStreamUri",
        "GetSnapshotUri",
        "GetVideoSources",
        "GetVideoSourceConfigurations",
        "GetVideoEncoderConfigurations",
        "GetVideoEncoderConfigurationOptions",

        // PTZ metadata. Movement, preset changes and Stop are deliberately absent.
        "GetNodes",
        "GetPresets",
        "GetStatus",
        "GetConfigurationOptions",

        // Profile G observation and its bounded search session.
        "GetRecordings",
        "GetRecordingInformation",
        "FindRecordings",
        "GetRecordingSearchResults",
        "EndSearch",
        "GetReplayConfiguration",
        "GetReplayUri",
    };

    /// <summary>All operations that may be attempted by a read-only recorder session.</summary>
    public static IReadOnlyCollection<string> Approved => Allowed;

    public static bool IsAllowed(string operation) =>
        !string.IsNullOrWhiteSpace(operation) && Allowed.Contains(operation);

    public static string? FindOperation(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
            };

            using var reader = XmlReader.Create(new StringReader(body), settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var bodyElement = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("Body", StringComparison.Ordinal));
            return bodyElement?.Elements().FirstOrDefault()?.Name.LocalName;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static string? Validate(string body, out string operation)
    {
        operation = FindOperation(body) ?? "unreadable request";

        if (!IsAllowed(operation))
        {
            return $"{operation} is not in the read-only allowlist.";
        }

        return null;
    }
}

/// <summary>
/// The narrower allowlist used by <c>--probe-nvr-metadata</c>. It is intentionally separate from the full
/// read-only list: metadata verification must not be able to grow into stream or recording access by accident.
/// </summary>
public static class NvrMetadataOperations
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
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

    /// <summary>Operations the metadata probe will never send, even if a future caller asks for them.</summary>
    public static IReadOnlyList<string> Excluded { get; } =
    [
        "GetStreamUri",
        "GetReplayUri",
        "FindRecordings",
        "GetRecordingSearchResults",
        "GetRecordings",
        "GetRecordingInformation",
        "ContinuousMove",
        "Stop",
        "GotoPreset",
        "SetVideoEncoderConfiguration",
        "CreateUsers",
        "DeleteUsers",
        "RTSP DESCRIBE",
        "RTSP SETUP",
        "RTSP PLAY",
        "RTSP PAUSE",
        "RTSP TEARDOWN",
    ];

    public static IReadOnlyCollection<string> Approved => Allowed;

    public static bool IsAllowed(string operation) =>
        !string.IsNullOrWhiteSpace(operation) && Allowed.Contains(operation);

    public static string? Validate(string body, out string operation)
    {
        operation = NvrReadOnlyOperations.FindOperation(body) ?? "unreadable request";
        return IsAllowed(operation) ? null : $"{operation} is not in the metadata-only allowlist.";
    }
}

/// <summary>
/// Metadata verification has its own authorization decision. A successful full-NVR authorization cannot be reused:
/// the caller must pass the metadata flag, the exact host, the approved scheme and a matching confirmation.
/// </summary>
public static class NvrMetadataAccessGate
{
    public static HardwareAccessDecision Evaluate(
        string? host,
        int onvifPort,
        bool useTls,
        bool metadataAuthorized,
        string? confirmedHost)
    {
        var normalizedHost = host?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            return HardwareAccessDecision.Blocked("No exact NVR host was supplied.");
        }

        if (onvifPort is < 1 or > 65535)
        {
            return HardwareAccessDecision.Blocked("The ONVIF port must be between 1 and 65535.");
        }

        if (!metadataAuthorized)
        {
            return HardwareAccessDecision.Blocked(
                "Metadata-only hardware access was not explicitly authorized with --nvr-metadata-authorized.");
        }

        if (!string.Equals(confirmedHost?.Trim(), normalizedHost, StringComparison.OrdinalIgnoreCase))
        {
            return HardwareAccessDecision.Blocked(
                "A separate confirmation naming the exact NVR host is required before any network operation.");
        }

        return HardwareAccessDecision.Permit(
            $"Metadata-only authorization for {normalizedHost} only; {(useTls ? "https" : "http")} port {onvifPort}; no RTSP or recording operations.");
    }
}

/// <summary>
/// Read-only mode is on for every recorder session. It is intentionally not a user toggle: a production recorder is
/// not the place to discover that a feature writes configuration.
/// </summary>
public static class NvrSafety
{
    public const bool ReadOnlyMode = true;

    public const string ReadOnlyLabel = "READ-ONLY NVR MODE";

    public static string Redact(Uri? uri)
    {
        if (uri is null)
        {
            return "no endpoint";
        }

        if (string.IsNullOrEmpty(uri.UserInfo))
        {
            return uri.ToString();
        }

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri.ToString();
    }

    /// <summary>Masks a host for a report intended to leave the operator's computer.</summary>
    public static string MaskHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "not supplied";
        }

        var normalized = host.Trim();
        if (System.Net.IPAddress.TryParse(normalized, out var address))
        {
            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
            {
                return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.***";
            }

            if (bytes.Length > 2)
            {
                return string.Concat(bytes.Take(2).Select(part => part.ToString("x2"))) + ":****";
            }

            return "***";
        }

        var dot = normalized.IndexOf('.');
        return dot > 0 ? $"{normalized[..dot]}.***" : "***";
    }
}

/// <summary>
/// Decides whether a real NVR probe may contact hardware. Both an explicit authorization flag and a separate
/// confirmation naming the exact host are required; credentials, a saved device, or a reachable address are not
/// authorization.
/// </summary>
public static class NvrHardwareAccessGate
{
    public static HardwareAccessDecision Evaluate(
        string? host,
        int onvifPort,
        int rtspPort,
        bool authorized,
        string? confirmedHost)
    {
        var normalizedHost = host?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            return HardwareAccessDecision.Blocked("No exact NVR host was supplied.");
        }

        if (onvifPort is < 1 or > 65535 || rtspPort is < 1 or > 65535)
        {
            return HardwareAccessDecision.Blocked("The ONVIF and RTSP ports must be between 1 and 65535.");
        }

        if (!authorized)
        {
            return HardwareAccessDecision.Blocked("Hardware access was not explicitly authorized.");
        }

        if (!string.Equals(confirmedHost?.Trim(), normalizedHost, StringComparison.OrdinalIgnoreCase))
        {
            return HardwareAccessDecision.Blocked(
                "A separate confirmation naming the exact NVR host is required before any network operation.");
        }

        return HardwareAccessDecision.Permit(
            $"Authorized for {normalizedHost} only; ONVIF port {onvifPort}, RTSP port {rtspPort}; read-only operations only.");
    }
}

public sealed record HardwareAccessDecision(bool Allowed, string Detail)
{
    public static HardwareAccessDecision Permit(string detail) => new(true, detail);

    public static HardwareAccessDecision Blocked(string detail) => new(false, detail);
}

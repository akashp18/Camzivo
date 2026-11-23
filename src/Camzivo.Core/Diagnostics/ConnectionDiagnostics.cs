using Camzivo.Core.Security;

namespace Camzivo.Core.Diagnostics;

public enum StreamFailure
{
    None = 0,
    DnsFailure,
    ConnectionRefused,
    Timeout,
    Unauthorized,
    Forbidden,
    RtspMethodNotAllowed,
    NotFound,
    UnsupportedCodec,

    /// <summary>The stream connected and played, but produced no decodable video.</summary>
    NoVideo,

    /// <summary>The device refused the requested RTP transport (HTTP 461).</summary>
    UnsupportedTransport,

    DecoderInitFailed,
    GpuDeviceLost,
    BandwidthInsufficient,
    NvrPermissionDenied,
    ProtocolViolation,
    Unknown,
}

/// <summary>What happened, why, and what the operator can do. Codes are never shown alone.</summary>
public sealed record Diagnostic(
    StreamFailure Code,
    string Title,
    string Explanation,
    IReadOnlyList<string> Actions,
    string? Detail = null)
{
    public string CodeText => Code.ToString();
}

/// <summary>
/// Maps a classified stream failure to an actionable message. This exists because "Error 1005" is not a
/// support answer; every branch must produce at least one concrete action.
/// </summary>
public static class ConnectionDiagnostics
{
    public static Diagnostic Explain(StreamFailure failure, string? redactedDetail = null)
    {
        var detail = string.IsNullOrWhiteSpace(redactedDetail)
            ? null
            : CredentialRedactor.Sanitize(redactedDetail);

        var diagnostic = failure switch
        {
            StreamFailure.DnsFailure => new Diagnostic(
                failure,
                "Host name could not be resolved",
                "The device address is a name that DNS could not resolve on this network. Cameras usually answer by IP address.",
                ["Enter the camera's IPv4 address instead of a host name", "Check that the workstation is on the camera's network"]),

            StreamFailure.ConnectionRefused => new Diagnostic(
                failure,
                "Device refused the connection",
                "The host answered, but nothing is listening on the requested port. This is usually the wrong RTSP port, or the camera's RTSP service is disabled.",
                ["Confirm the RTSP port (Dahua default 554)", "Enable RTSP in the camera's network settings", "Verify no firewall blocks the port"]),

            StreamFailure.Timeout => new Diagnostic(
                failure,
                "Device did not answer in time",
                "The connection or stream setup exceeded the configured timeout. The device may be busy, rebooting, or unreachable through a firewall.",
                ["Retry the connection", "Check the device is powered and on the network", "Try the other transport (TCP/UDP)"]),

            StreamFailure.Unauthorized => new Diagnostic(
                failure,
                "Camera rejected the credentials",
                "The camera answered, but the stream requires authentication that failed. On many cameras ONVIF uses a separate account from the web interface.",
                ["Enable ONVIF in the camera and use its ONVIF account", "Re-enter the device password", "Check whether the account is locked by failed attempts"]),

            StreamFailure.Forbidden => new Diagnostic(
                failure,
                "Access forbidden for this account",
                "Credentials were accepted but the account is not allowed to view this stream. This is a device-side permission, not a Camzivo setting.",
                ["Use an account with live-view permission", "For NVR channels, check the user's channel rights", "Ask the device administrator to grant access"]),

            StreamFailure.RtspMethodNotAllowed => new Diagnostic(
                failure,
                "Device does not support this RTSP method",
                "The device speaks RTSP but refused DESCRIBE/SETUP. Some devices only expose streams through their own API or a specific URL.",
                ["Verify the stream path for this device type", "Enter the camera manually with its known RTSP path", "Check the device firmware supports standard RTSP"]),

            StreamFailure.NotFound => new Diagnostic(
                failure,
                "Stream path not found",
                "The device understood the request but no stream exists at that path. Channel numbers or profile tokens are the usual cause.",
                ["Check the channel number for NVR cameras", "Re-run capability detection", "Copy the stream path from the device's own web page"]),

            StreamFailure.UnsupportedCodec => new Diagnostic(
                failure,
                "Video codec not supported",
                "The stream uses a codec this build cannot decode. H.264 and H.265 are supported; others may not be.",
                ["Switch the camera's substream to H.264", "Change the camera encoding profile", "Copy diagnostics for support"]),

            StreamFailure.NoVideo => new Diagnostic(
                failure,
                "Stream connected but delivered no video",
                "RTSP accepted the session, but no frame could be decoded from what arrived. The device may be sending a codec or packet layout this build does not handle.",
                ["Switch the camera's encoding to H.264", "Try the camera's substream", "Check the device's RTSP packetization mode"]),

            StreamFailure.UnsupportedTransport => new Diagnostic(
                failure,
                "Device refused this transport",
                "The device will not send RTP over the requested transport. Many cameras only support TCP interleaved for remote access.",
                ["Use TCP transport", "Check the camera's RTP settings", "Try the device's own web client to confirm the stream works"]),

            StreamFailure.DecoderInitFailed => new Diagnostic(
                failure,
                "Video decoder could not start",
                "The stream is reachable but the decoder failed to initialise with the negotiated parameters (resolution, profile or surface pool).",
                ["Retry the stream", "Switch this camera to its substream", "Check GPU memory usage in System Health"]),

            StreamFailure.GpuDeviceLost => new Diagnostic(
                failure,
                "Graphics device was reset",
                "The GPU driver reset the device (TDR) or the adapter was removed. Camzivo is rebuilding its rendering device.",
                ["Wait for automatic recovery", "Update the graphics driver if this repeats", "Reduce the number of main-stream tiles"]),

            StreamFailure.BandwidthInsufficient => new Diagnostic(
                failure,
                "Not enough bandwidth for this stream",
                "Frames are arriving slower than the stream's frame rate, so the queue is dropping frames to stay live.",
                ["Switch this camera to its substream", "Lower the camera's bitrate or frame rate", "Use TCP transport if packet loss is present"]),

            StreamFailure.NvrPermissionDenied => new Diagnostic(
                failure,
                "NVR denied access to this channel",
                "The NVR accepted the login but refused this channel. Channel permissions are configured on the NVR, not in Camzivo.",
                ["Grant the user access to this channel on the NVR", "Use an administrator account to verify the channel works", "Check the NVR's user/channel matrix"]),

            StreamFailure.ProtocolViolation => new Diagnostic(
                failure,
                "Device response was not valid RTSP/ONVIF",
                "The device answered with something that does not follow the protocol (malformed SDP, unexpected status, truncated headers).",
                ["Check the device firmware version", "Try the vendor's documented RTSP path", "Copy diagnostics for support"]),

            _ => new Diagnostic(
                failure,
                "Stream failed for an unclassified reason",
                "Camzivo could not classify this failure. The raw detail is attached and redacted; it is safe to share.",
                ["Retry the connection", "Copy diagnostics for support", "Try the other transport (TCP/UDP)"]),
        };

        return diagnostic with { Detail = detail };
    }

    public static IReadOnlyList<Diagnostic> ExplainAll(IEnumerable<StreamFailure> failures) =>
        failures.Where(failure => failure != StreamFailure.None).Select(failure => Explain(failure)).ToList();
}

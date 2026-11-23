using System.Globalization;

namespace Camzivo.Desktop.Services;

/// <summary>
/// Local-only validation for the manual NVR connection window.
///
/// It parses the operator's text and nothing else: no socket is opened, no name is resolved, and no device is
/// discovered. Rejecting bad input here is what lets the window refuse to offer a confirmation for a destination that
/// is not a single host and an approved port.
/// </summary>
public static class NvrConnectionForm
{
    public static int DefaultPort(bool useTls) => useTls ? 443 : 80;

    /// <summary>Returns an operator-facing error, or null with the parsed port.</summary>
    public static string? Validate(string? host, string? portText, bool useTls, out int port)
    {
        port = DefaultPort(useTls);

        var trimmed = host?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return "Enter the NVR IP address or hostname.";
        }

        // One host only. A scheme, path, credential or space here would silently change what gets contacted.
        if (trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.Contains('@', StringComparison.Ordinal) ||
            trimmed.Contains(' ', StringComparison.Ordinal) ||
            trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return "Enter only the IP address or hostname; protocol and port have their own fields.";
        }

        if (!Uri.TryCreate($"{(useTls ? "https" : "http")}://{trimmed}", UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return "That is not a valid IP address or hostname.";
        }

        if (!string.IsNullOrWhiteSpace(portText))
        {
            if (!int.TryParse(portText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
            {
                return "The ONVIF port must be a whole number.";
            }

            if (port is < 1 or > 65535)
            {
                return "The ONVIF port must be between 1 and 65535.";
            }
        }

        return null;
    }
}

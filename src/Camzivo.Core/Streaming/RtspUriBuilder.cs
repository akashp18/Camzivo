namespace Camzivo.Core.Streaming;

/// <summary>
/// Builds RTSP URIs without ever embedding credentials.
/// Query composition is explicit so we cannot produce the double-<c>?</c> URIs that break real cameras.
/// </summary>
public static class RtspUriBuilder
{
    public const int DefaultRtspPort = 554;

    public static Uri Build(string host, int port, string? path, RtspTransport transport)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host is required.", nameof(host));
        }

        host = host.Trim();

        if (host.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException("Host must not contain user information; credentials belong in the secret store.", nameof(host));
        }

        if (host.Contains("://", StringComparison.Ordinal))
        {
            throw new ArgumentException("Host must not include a scheme.", nameof(host));
        }

        if (host.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("Host must not include a path.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
        }

        var normalizedPath = NormalizePath(path);
        var transportValue = transport == RtspTransport.Udp ? "udp" : "tcp";
        var separator = normalizedPath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var hostPart = FormatHost(host);

        var uriText = $"rtsp://{hostPart}:{port}{normalizedPath}{separator}transport={transportValue}";

        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"'{uriText}' is not a valid RTSP URI.", nameof(path));
        }

        return uri;
    }

    public static Uri Build(string host, string? path = null, RtspTransport transport = RtspTransport.Tcp) =>
        Build(host, DefaultRtspPort, path, transport);

    /// <summary>Replaces user information with a fixed mask, for display and logging.</summary>
    public static string Redact(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (string.IsNullOrEmpty(uri.UserInfo))
        {
            return uri.ToString();
        }

        var builder = new UriBuilder(uri)
        {
            UserName = "***",
            Password = string.Empty,
        };

        return builder.Uri.ToString();
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        path = path.Trim();
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static string FormatHost(string host)
    {
        // Uri requires IPv6 literals in brackets; accept both "fe80::1" and "[fe80::1]".
        if (host.Contains(':', StringComparison.Ordinal))
        {
            return host.StartsWith('[') ? host : $"[{host}]";
        }

        return host;
    }
}

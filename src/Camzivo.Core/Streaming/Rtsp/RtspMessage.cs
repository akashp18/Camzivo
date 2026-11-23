using System.Globalization;
using System.Text;

namespace Camzivo.Core.Streaming.Rtsp;

/// <summary>
/// One RTSP request or response. Parsing is strict about the status line and lenient about header names, because
/// cameras disagree on casing and some send duplicated headers.
/// </summary>
public sealed record RtspMessage(int StatusCode, string Reason, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Parses a response head (status line plus headers, no body).</summary>
    public static RtspMessage ParseResponse(string head)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(head);

        var lines = head.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var statusLine = lines[0].Trim();

        if (!statusLine.StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Not an RTSP response: '{Truncate(statusLine)}'.");
        }

        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
        {
            throw new InvalidDataException($"RTSP response has no numeric status: '{Truncate(statusLine)}'.");
        }

        var headers = ParseHeaders(lines, 1);
        return new RtspMessage(status, parts.Length > 2 ? parts[2] : string.Empty, headers, string.Empty);
    }

    public static IReadOnlyDictionary<string, string> ParseHeaders(IReadOnlyList<string> lines, int start)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = start; index < lines.Count; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.Length == 0)
            {
                break;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            // First value wins: a duplicated CSeq or Session must not be silently overwritten.
            headers.TryAdd(name, value);
        }

        return headers;
    }

    private static string Truncate(string value) => value.Length <= 80 ? value : value[..80] + "…";
}

/// <summary>Builder for the handful of requests the client sends. Sequence numbers are explicit so they are testable.</summary>
public sealed class RtspRequest
{
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

    public RtspRequest(string method, Uri uri, int cseq)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(uri);

        Method = method;
        Uri = uri;
        CSeq = cseq;

        _headers["CSeq"] = cseq.ToString(CultureInfo.InvariantCulture);
        _headers["User-Agent"] = "Camzivo/0.3";
    }

    public string Method { get; }

    public Uri Uri { get; }

    public int CSeq { get; }

    public string? Body { get; init; }

    public RtspRequest WithHeader(string name, string value)
    {
        _headers[name] = value;
        return this;
    }

    /// <summary>
    /// Renders the request with the absolute URL in the request line, as RFC 2326 allows and as ffmpeg, VLC and
    /// OpenCV do.
    ///
    /// This was origin-form for a long time on the belief that some cameras reject absolute URIs. A Dahua recorder
    /// proved the opposite for its firmware: with the origin-form path it answers 503 for every channel, and with the
    /// absolute URL it answers the normal 401 Digest challenge and then streams. The digest <c>uri</c> field is built
    /// from the same text, so the two always agree.
    /// </summary>
    public string RequestUriText => Uri.AbsoluteUri;

    public string Build()
    {
        var builder = new StringBuilder();
        builder.Append(Method).Append(' ').Append(RequestUriText).Append(" RTSP/1.0\r\n");

        foreach (var header in _headers)
        {
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        if (!string.IsNullOrEmpty(Body))
        {
            builder.Append("Content-Length: ")
                .Append(Encoding.UTF8.GetByteCount(Body).ToString(CultureInfo.InvariantCulture))
                .Append("\r\n");
        }

        builder.Append("\r\n");

        if (!string.IsNullOrEmpty(Body))
        {
            builder.Append(Body);
        }

        return builder.ToString();
    }
}

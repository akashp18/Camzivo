using System.Globalization;

namespace Camzivo.Core.Streaming.Rtsp;

/// <summary>A codec advertised in an SDP media section.</summary>
public sealed record SdpCodec(int PayloadType, string Encoding, int ClockRate, string? Parameters)
{
    public bool IsH264 => Encoding.Equals("H264", StringComparison.OrdinalIgnoreCase);

    public bool IsH265 => Encoding.Equals("H265", StringComparison.OrdinalIgnoreCase)
                          || Encoding.Equals("HEVC", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One <c>m=</c> section, reduced to what a player needs.</summary>
public sealed record SdpMedia(
    string MediaType,
    int Port,
    string Protocol,
    IReadOnlyList<int> PayloadTypes,
    string? Control,
    IReadOnlyList<SdpCodec> Codecs,
    IReadOnlyList<string> FormatParameters,
    bool IsSendOnly)
{
    public SdpCodec? VideoCodec => Codecs.FirstOrDefault(codec => codec.IsH264 || codec.IsH265);

    /// <summary>SPS/PPS carried in <c>fmtp</c> as base64, when the device chose to include them.</summary>
    public IReadOnlyList<byte[]> ParameterSets
    {
        get
        {
            var result = new List<byte[]>();

            foreach (var line in FormatParameters)
            {
                var marker = line.IndexOf("sprop-parameter-sets=", StringComparison.OrdinalIgnoreCase);
                if (marker < 0)
                {
                    continue;
                }

                var value = line[(marker + "sprop-parameter-sets=".Length)..].Split(';')[0];

                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        result.Add(Convert.FromBase64String(part));
                    }
                    catch (FormatException)
                    {
                        // A device sending an unreadable parameter set is not fatal: the stream itself carries them.
                    }
                }
            }

            return result;
        }
    }

    /// <summary>Absolute playback URI for this section; empty control means the base URL.</summary>
    public Uri ResolveControl(Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(Control))
        {
            return baseUri;
        }

        var control = Control.Trim();

        if (control.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(control);
        }

        return control == "*" ? baseUri : new Uri(baseUri, control);
    }
}

/// <summary>
/// Minimal SDP (RFC 4566) reader for camera streams: session attributes, media sections, rtpmap and fmtp.
///
/// Cameras are inconsistent here, so parsing never throws for unknown lines — only a document with no video
/// section is an error, and that is reported as a stream failure rather than an exception at the call site.
/// </summary>
public sealed record SdpDescription(string SessionName, IReadOnlyList<SdpMedia> Media)
{
    public SdpMedia? Video => Media.FirstOrDefault(media =>
        media.MediaType.Equals("video", StringComparison.OrdinalIgnoreCase));

    public SdpMedia? Audio => Media.FirstOrDefault(media =>
        media.MediaType.Equals("audio", StringComparison.OrdinalIgnoreCase));

    public static SdpDescription Parse(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
        {
            throw new InvalidDataException("The device returned an empty description.");
        }

        var lines = sdp.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var media = new List<SdpMedia>();

        string? sessionName = null;
        string? pendingControl = null;
        var pendingRtpmap = new Dictionary<int, SdpCodec>();
        var pendingFmtp = new List<string>();

        string? currentType = null;
        var currentPort = 0;
        var currentProtocol = string.Empty;
        var currentPayloads = new List<int>();
        var currentSendOnly = false;

        void Flush()
        {
            if (currentType is null)
            {
                return;
            }

            var codecs = currentPayloads
                .Select(payload => pendingRtpmap.TryGetValue(payload, out var codec) ? codec : null)
                .Where(codec => codec is not null)
                .Select(codec => codec!)
                .ToList();

            media.Add(new SdpMedia(
                currentType,
                currentPort,
                currentProtocol,
                currentPayloads.ToList(),
                pendingControl,
                codecs,
                pendingFmtp.ToList(),
                currentSendOnly));

            currentType = null;
            currentPort = 0;
            currentProtocol = string.Empty;
            currentPayloads = [];
            currentSendOnly = false;
            pendingControl = null;
            pendingRtpmap = [];
            pendingFmtp = [];
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2 || line[1] != '=')
            {
                continue;
            }

            var value = line[2..];

            switch (line[0])
            {
                case 's':
                    if (sessionName is null)
                    {
                        sessionName = value;
                    }

                    break;

                case 'm':
                    Flush();
                    ParseMediaLine(value, out currentType, out currentPort, out currentProtocol, currentPayloads);
                    break;

                case 'a':
                    if (value.StartsWith("control:", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingControl = value["control:".Length..].Trim();
                    }
                    else if (value.StartsWith("rtpmap:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ParseRtpmap(value["rtpmap:".Length..]) is { } codec)
                        {
                            pendingRtpmap[codec.PayloadType] = codec;
                        }
                    }
                    else if (value.StartsWith("fmtp:", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingFmtp.Add(value["fmtp:".Length..]);
                    }
                    else if (value.Equals("sendonly", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSendOnly = true;
                    }

                    break;
            }
        }

        Flush();

        if (media.Count == 0)
        {
            throw new InvalidDataException("The description contains no media section.");
        }

        return new SdpDescription(sessionName ?? string.Empty, media);
    }

    private static void ParseMediaLine(
        string value,
        out string? mediaType,
        out int port,
        out string protocol,
        List<int> payloads)
    {
        mediaType = null;
        port = 0;
        protocol = string.Empty;

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return;
        }

        mediaType = parts[0];

        // "554" or "554/2" (number of ports for RTP/RTCP); only the RTP port matters here.
        var portText = parts[1].Split('/')[0];
        _ = int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
        protocol = parts[2];

        for (var index = 3; index < parts.Length; index++)
        {
            if (int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var payload))
            {
                payloads.Add(payload);
            }
        }
    }

    private static SdpCodec? ParseRtpmap(string value)
    {
        var space = value.IndexOf(' ');
        if (space <= 0)
        {
            return null;
        }

        if (!int.TryParse(value[..space], NumberStyles.Integer, CultureInfo.InvariantCulture, out var payload))
        {
            return null;
        }

        var rest = value[(space + 1)..].Split('/', 3);
        var clock = rest.Length > 1 && int.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedClock)
            ? parsedClock
            : 90000;

        return new SdpCodec(payload, rest[0].Trim(), clock, rest.Length > 2 ? rest[2] : null);
    }
}

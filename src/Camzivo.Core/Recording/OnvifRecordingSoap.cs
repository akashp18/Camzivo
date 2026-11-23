using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Camzivo.Core.Recording;

/// <summary>Service addresses a recorder advertises for investigation. Null means "not advertised".</summary>
public sealed record RecordingServices(Uri? Recording, Uri? Search, Uri? Replay)
{
    public static RecordingServices None { get; } = new(null, null, null);

    public bool HasAny => Recording is not null || Search is not null || Replay is not null;

    public bool CanSearchAndReplay => Search is not null && Replay is not null;
}

public sealed record RecordingSearchPage(
    IReadOnlyList<RecordingSegment> Segments,
    string? SearchToken,
    string State,
    bool Completed,
    int ResultCount)
{
    public static RecordingSearchPage Empty { get; } = new([], null, "Unknown", false, 0);
}

/// <summary>
/// ONVIF Profile G: the search and replay operations an investigation needs.
///
/// Profile G is explicitly not part of base ONVIF: a device can stream live video and still have no recording
/// service. Every parser here is tolerant of what devices actually return (vendor prefixes, one or many tracks,
/// multiple recordings per result) and hostile input is rejected rather than trusted.
/// </summary>
public static class OnvifRecordingSoap
{
    public const string RecordingNamespace = "http://www.onvif.org/ver10/recording/wsdl";
    public const string SearchNamespace = "http://www.onvif.org/ver10/search/wsdl";
    public const string ReplayNamespace = "http://www.onvif.org/ver10/replay/wsdl";

    private const int MaxCharacters = 2 * 1024 * 1024;

    // ---- requests ---------------------------------------------------------------------------------------------

    public static string FindRecordingsBody(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string? sourceToken = null,
        string? recordingToken = null)
    {
        // SourceToken belongs in IncludedSources, not IncludedRecordings. Putting a source token in the recording
        // field makes a conforming recorder return nothing (or the wrong recording), while permissive simulators hide
        // the mistake. Both filters are omitted when null, which means "everything" per Profile G.
        var sources = string.IsNullOrWhiteSpace(sourceToken)
            ? "<IncludedSources />"
            : $"<IncludedSources><SourceToken>{Escape(sourceToken)}</SourceToken></IncludedSources>";
        var recordings = string.IsNullOrWhiteSpace(recordingToken)
            ? "<IncludedRecordings />"
            : $"<IncludedRecordings>{Escape(recordingToken)}</IncludedRecordings>";

        return $"""
            <FindRecordings xmlns="{SearchNamespace}">
              <Scope>
                {sources}
                {recordings}
                <RecordingInformationFilter />
              </Scope>
              <StartPosition>{Utc(startUtc)}</StartPosition>
              <EndPosition>{Utc(endUtc)}</EndPosition>
              <MaxMatches>1000</MaxMatches>
              <KeepAliveTime>PT30S</KeepAliveTime>
            </FindRecordings>
            """;
    }

    public static string GetRecordingSearchResultsBody(string searchToken, int minResults = 1, int maxResults = 1000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchToken);

        return $"""
            <GetRecordingSearchResults xmlns="{SearchNamespace}">
              <SearchToken>{Escape(searchToken)}</SearchToken>
              <MinResults>{minResults.ToString(CultureInfo.InvariantCulture)}</MinResults>
              <MaxResults>{maxResults.ToString(CultureInfo.InvariantCulture)}</MaxResults>
              <WaitTime>PT5S</WaitTime>
            </GetRecordingSearchResults>
            """;
    }

    public static string EndSearchBody(string searchToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchToken);

        return $"""
            <EndSearch xmlns="{SearchNamespace}">
              <SearchToken>{Escape(searchToken)}</SearchToken>
            </EndSearch>
            """;
    }

    public static string GetRecordingsBody() => $"""<GetRecordings xmlns="{RecordingNamespace}" />""";

    public static string GetRecordingInformationBody(string recordingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingToken);

        return $"""
            <GetRecordingInformation xmlns="{RecordingNamespace}">
              <RecordingToken>{Escape(recordingToken)}</RecordingToken>
            </GetRecordingInformation>
            """;
    }

    public static string GetReplayUriBody(string recordingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingToken);

        return $"""
            <GetReplayUri xmlns="{ReplayNamespace}">
              <StreamSetup>
                <Stream xmlns="http://www.onvif.org/ver10/schema">RTP-Unicast</Stream>
                <Transport xmlns="http://www.onvif.org/ver10/schema"><Protocol>RTSP</Protocol></Transport>
              </StreamSetup>
              <RecordingToken>{Escape(recordingToken)}</RecordingToken>
            </GetReplayUri>
            """;
    }

    public static string StopBody(string recordingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingToken);

        return $"""
            <Stop xmlns="{RecordingNamespace}">
              <RecordingToken>{Escape(recordingToken)}</RecordingToken>
            </Stop>
            """;
    }

    // ---- responses --------------------------------------------------------------------------------------------

    /// <summary>Reads FindRecordings' SearchToken and its immediate state.</summary>
    public static (string? Token, string State) ParseSearchStarted(string soapXml)
    {
        var root = Load(soapXml);
        var token = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "SearchToken")?.Value?.Trim();
        var state = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "SearchState")?.Value?.Trim() ?? "Unknown";

        return (string.IsNullOrWhiteSpace(token) ? null : token, state);
    }

    /// <summary>
    /// Reads one page of search results. Each RecordingInformation is one recording token with the time range it
    /// matched; the ranges are what the timeline draws, so they are never invented when absent.
    /// </summary>
    public static RecordingSearchPage ParseSearchResults(string soapXml)
    {
        var root = Load(soapXml);

        if (!root.Descendants().Any(element => element.Name.LocalName is "GetRecordingSearchResultsResponse" or "RecordingInformation"))
        {
            throw new InvalidDataException("Response is not a Profile G search result.");
        }

        var state = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "SearchState")?.Value?.Trim() ?? "Unknown";
        var token = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "SearchToken")?.Value?.Trim();

        var segments = new List<RecordingSegment>();

        foreach (var information in root.Descendants().Where(element => element.Name.LocalName == "RecordingInformation"))
        {
            var recordingToken = information
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "RecordingToken")?.Value?.Trim()
                ?? information.Attribute("token")?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(recordingToken))
            {
                continue;
            }

            var start = ReadTime(information, "EarliestRecording") ??
                        ReadTime(information, "StartTime") ??
                        ReadTime(information, "Earliest");
            var end = ReadTime(information, "LatestRecording") ??
                      ReadTime(information, "StopTime") ??
                      ReadTime(information, "Latest");

            if (start is null || end is null || end <= start)
            {
                // A result without a usable range cannot be drawn on a timeline: skipping it is honest, and the
                // caller still sees the recording token count.
                continue;
            }

            var track = information.Descendants().FirstOrDefault(element => element.Name.LocalName == "Track");
            var trackToken = track?.Attribute("token")?.Value ?? track?
                .Descendants().FirstOrDefault(element => element.Name.LocalName == "Token")?.Value;

                        var sourceElement = information.Descendants().FirstOrDefault(element => element.Name.LocalName == "Source");
            var source = sourceElement?.Descendants().FirstOrDefault(element => element.Name.LocalName == "Name")?.Value?.Trim();
            var sourceToken = sourceElement?.Descendants().FirstOrDefault(element => element.Name.LocalName == "SourceId")?.Value?.Trim();

            segments.Add(new RecordingSegment(recordingToken!, trackToken, start.Value, end.Value, source, sourceToken));
        }

        segments = Merge(segments);

        return new RecordingSearchPage(
            segments,
            string.IsNullOrWhiteSpace(token) ? null : token,
            state,
            state is "Completed" or "Unknown",
            segments.Count);
    }

    /// <summary>Reads the replay RTSP URI for a recording token.</summary>
    public static Uri? ParseReplayUri(string soapXml)
    {
        var root = Load(soapXml);
        var text = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "Uri")?.Value?.Trim();

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>A recording token and the source it belongs to, from GetRecordings/GetRecordingInformation.</summary>
    public sealed record RecordingSource(string RecordingToken, string? SourceToken, string? SourceName);

    /// <summary>
    /// Reads the source token/name of every recording. Channel discovery uses this to map an ONVIF media profile to the
    /// recording source the NVR actually expects; a source token is never invented from a channel number.
    /// </summary>
    public static IReadOnlyList<RecordingSource> ParseRecordingSources(string soapXml)
    {
        try
        {
            var root = Load(soapXml);
            return root.Descendants()
                .Where(element => element.Name.LocalName == "RecordingInformation")
                .Select(element =>
                {
                    var token = element.Descendants().FirstOrDefault(child => child.Name.LocalName == "RecordingToken")?.Value?.Trim()
                                ?? element.Attribute("token")?.Value?.Trim();
                    var source = element.Descendants().FirstOrDefault(child => child.Name.LocalName == "Source");
                    var sourceToken = source?.Descendants().FirstOrDefault(child => child.Name.LocalName == "SourceId")?.Value?.Trim();
                    var sourceName = source?.Descendants().FirstOrDefault(child => child.Name.LocalName == "Name")?.Value?.Trim();
                    return string.IsNullOrWhiteSpace(token) ? null : new RecordingSource(token, sourceToken, sourceName);
                })
                .Where(source => source is not null)
                .Select(source => source!)
                .DistinctBy(source => source.RecordingToken, StringComparer.Ordinal)
                .ToList();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>Reads recording tokens from GetRecordings.</summary>
    public static IReadOnlyList<string> ParseRecordings(string soapXml)
    {
        try
        {
            var root = Load(soapXml);
            return root.Descendants()
                .Where(element => element.Name.LocalName == "RecordingItem")
                .Select(element => element.Descendants().FirstOrDefault(child => child.Name.LocalName == "RecordingToken")?.Value?.Trim())
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .Select(token => token!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>Reads the recording service addresses out of a GetServices/GetCapabilities response.</summary>
    public static RecordingServices ParseServices(string soapXml, Uri deviceServiceUri)
    {
        ArgumentNullException.ThrowIfNull(soapXml);
        ArgumentNullException.ThrowIfNull(deviceServiceUri);

        XDocument document;
        try
        {
            document = XDocument.Parse(soapXml);
        }
        catch (XmlException)
        {
            return RecordingServices.None;
        }

        Uri? Find(string namespaceFragment)
        {
            foreach (var service in document.Descendants().Where(element => element.Name.LocalName == "Service"))
            {
                var ns = service.Descendants().FirstOrDefault(child => child.Name.LocalName == "Namespace")?.Value?.Trim();

                if (ns is not null && ns.Contains(namespaceFragment, StringComparison.OrdinalIgnoreCase))
                {
                    var address = service.Descendants().FirstOrDefault(child => child.Name.LocalName == "XAddr")?.Value?.Trim();

                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        return Uri.TryCreate(deviceServiceUri, address, out var uri) ? uri : null;
                    }
                }
            }

            return null;
        }

        return new RecordingServices(
            Find("/recording/"),
            Find("/search/"),
            Find("/replay/"));
    }

    /// <summary>Merges overlapping or touching ranges so the timeline shows one block per continuous recording.</summary>
    public static List<RecordingSegment> Merge(IEnumerable<RecordingSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var ordered = segments
            .Where(segment => segment.EndUtc > segment.StartUtc)
            .OrderBy(segment => segment.StartUtc)
            .ToList();

        var merged = new List<RecordingSegment>(ordered.Count);

        foreach (var segment in ordered)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];

                if (segment.StartUtc <= last.EndUtc)
                {
                    merged[^1] = last with
                    {
                        EndUtc = segment.EndUtc > last.EndUtc ? segment.EndUtc : last.EndUtc,
                        TrackToken = last.TrackToken ?? segment.TrackToken,
                    };
                    continue;
                }
            }

            merged.Add(segment);
        }

        return merged;
    }

    private static DateTimeOffset? ReadTime(XElement information, string localName)
    {
        var text = information
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)?
            .Value
            .Trim();

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }

    private static XDocument Load(string soapXml)
    {
        if (string.IsNullOrWhiteSpace(soapXml))
        {
            throw new InvalidDataException("The recorder returned an empty response.");
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
            throw new InvalidDataException("The recorder returned malformed XML.", exception);
        }
    }

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value);
}

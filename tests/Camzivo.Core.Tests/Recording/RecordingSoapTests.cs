using System.Text;
using Xunit;
using Camzivo.Core.Recording;

namespace Camzivo.Core.Tests.Recording;

public sealed class RecordingSoapTests
{
    private const string SearchResponse = """
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
          <s:Body>
            <GetRecordingSearchResultsResponse xmlns="http://www.onvif.org/ver10/search/wsdl">
              <ResultList>
                <RecordingInformation token="rec-1">
                  <RecordingToken>rec-1</RecordingToken>
                  <EarliestRecording>2026-11-22T09:00:00Z</EarliestRecording>
                  <LatestRecording>2026-11-22T10:30:00Z</LatestRecording>
                  <Content><Track token="track-1"><TrackType>Video</TrackType></Track></Content>
                </RecordingInformation>
                <RecordingInformation token="rec-2">
                  <RecordingToken>rec-2</RecordingToken>
                  <EarliestRecording>2026-11-22T10:00:00Z</EarliestRecording>
                  <LatestRecording>2026-11-22T12:00:00Z</LatestRecording>
                </RecordingInformation>
                <RecordingInformation token="broken">
                  <RecordingToken>broken</RecordingToken>
                  <EarliestRecording>not a time</EarliestRecording>
                </RecordingInformation>
              </ResultList>
              <SearchState>Completed</SearchState>
            </GetRecordingSearchResultsResponse>
          </s:Body>
        </s:Envelope>
        """;

    [Fact]
    public void FindRecordingsPutsTheSourceTokenInIncludedSources()
    {
        var body = OnvifRecordingSoap.FindRecordingsBody(
            DateTimeOffset.Parse("2026-11-22T00:00:00Z"),
            DateTimeOffset.Parse("2026-11-23T00:00:00Z"),
            "channel-7");

        Assert.Contains("<IncludedSources><SourceToken>channel-7</SourceToken></IncludedSources>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<IncludedRecordings>channel-7", body, StringComparison.Ordinal);
        Assert.Contains("<IncludedRecordings />", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingInformationSourceIsParsedFromAProfileGResponse()
    {
        var sources = OnvifRecordingSoap.ParseRecordingSources("""
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
              <trc:GetRecordingInformationResponse xmlns:trc="http://www.onvif.org/ver10/recording/wsdl" xmlns:tt="http://www.onvif.org/ver10/schema">
                <tt:RecordingInformation token="rec-1">
                  <tt:RecordingToken>rec-1</tt:RecordingToken>
                  <tt:Source><tt:SourceId>channel-1</tt:SourceId><tt:Name>Channel 1</tt:Name></tt:Source>
                </tt:RecordingInformation>
              </trc:GetRecordingInformationResponse>
            </s:Body></s:Envelope>
            """);

        var source = Assert.Single(sources);
        Assert.Equal("rec-1", source.RecordingToken);
        Assert.Equal("channel-1", source.SourceToken);
        Assert.Equal("Channel 1", source.SourceName);
    }

    [Fact]
    public void SearchResultsCarryTheRecordingSourceTokenAndName()
    {
        var page = OnvifRecordingSoap.ParseSearchResults("""
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body>
              <GetRecordingSearchResultsResponse xmlns="http://www.onvif.org/ver10/search/wsdl">
                <ResultList><RecordingInformation token="rec-1">
                  <RecordingToken>rec-1</RecordingToken>
                  <Source><SourceId>channel-7</SourceId><Name>Loading bay</Name></Source>
                  <EarliestRecording>2026-11-22T09:00:00Z</EarliestRecording>
                  <LatestRecording>2026-11-22T10:00:00Z</LatestRecording>
                </RecordingInformation></ResultList>
                <SearchState>Completed</SearchState>
              </GetRecordingSearchResultsResponse>
            </s:Body></s:Envelope>
            """);

        var segment = Assert.Single(page.Segments);
        Assert.Equal("channel-7", segment.SourceToken);
        Assert.Equal("Loading bay", segment.SourceName);
    }

    [Fact]
    public void SearchResultsAreReadAndOverlapsMerged()
    {
        var page = OnvifRecordingSoap.ParseSearchResults(SearchResponse);

        Assert.True(page.Completed);
        // rec-2 starts before rec-1 ends, so the timeline shows one continuous block from 09:00 to 12:00.
        var segment = Assert.Single(page.Segments);
        Assert.Equal("rec-1", segment.RecordingToken);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T09:00:00Z"), segment.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-11-22T12:00:00Z"), segment.EndUtc);
        Assert.DoesNotContain(page.Segments, segment => segment.RecordingToken == "broken");
    }

    [Fact]
    public void MalformedXmlIsReportedNotGuessed()
    {
        Assert.Throws<InvalidDataException>(() => OnvifRecordingSoap.ParseSearchResults("<not-closed"));
    }

    [Fact]
    public void ServicesAreDetectedOnlyWhenAdvertised()
    {
        var xml = """
            <GetServicesResponse xmlns="http://www.onvif.org/ver10/device/wsdl">
              <Service><Namespace>http://www.onvif.org/ver10/search/wsdl</Namespace>
                <XAddr>/onvif/search</XAddr></Service>
              <Service><Namespace>http://www.onvif.org/ver10/replay/wsdl</Namespace>
                <XAddr>http://127.0.0.1:8099/onvif/replay</XAddr></Service>
            </GetServicesResponse>
            """;

        var services = OnvifRecordingSoap.ParseServices(xml, new Uri("http://127.0.0.1:8099/onvif/device_service"));

        Assert.NotNull(services.Search);
        Assert.NotNull(services.Replay);
        Assert.Null(services.Recording);
        Assert.Equal("http://127.0.0.1:8099/onvif/search", services.Search!.ToString());

        var liveOnly = OnvifRecordingSoap.ParseServices(
            """<GetServicesResponse xmlns="http://www.onvif.org/ver10/device/wsdl"><Service><Namespace>http://www.onvif.org/ver10/media/wsdl</Namespace><XAddr>http://127.0.0.1/onvif/media</XAddr></Service></GetServicesResponse>""",
            new Uri("http://127.0.0.1/onvif/device_service"));

        Assert.False(liveOnly.HasAny);
        Assert.False(liveOnly.CanSearchAndReplay);
    }

    [Fact]
    public void DayBoundsFollowTheLocalCalendarAcrossDaylightSaving()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

        var normal = RecordingAvailability.DayBounds(new DateOnly(2026, 11, 22), zone);
        Assert.Equal(TimeSpan.FromHours(24), normal.EndUtc - normal.StartUtc);

        // 8 March 2026 is the spring-forward day in the US: 23 hours, not 24.
        var spring = RecordingAvailability.DayBounds(new DateOnly(2026, 3, 8), zone);
        Assert.Equal(TimeSpan.FromHours(23), spring.EndUtc - spring.StartUtc);

        // 1 November 2026 is the fall-back day: 25 hours.
        var autumn = RecordingAvailability.DayBounds(new DateOnly(2026, 11, 1), zone);
        Assert.Equal(TimeSpan.FromHours(25), autumn.EndUtc - autumn.StartUtc);
    }

    [Fact]
    public void SearchWindowsAreValidatedBeforeAnyNetworkCall()
    {
        var start = DateTimeOffset.Parse("2026-11-22T00:00:00Z");

        Assert.Null(RecordingWindow.Validate(start, start.AddHours(1)));
        Assert.NotNull(RecordingWindow.Validate(start, start));
        Assert.NotNull(RecordingWindow.Validate(start, start.AddDays(8)));
    }

    [Fact]
    public void Mp4WriterProducesAContainerWithItsSampleTable()
    {
        // Minimal SPS/PPS and one IDR access unit: the muxer's job is container structure, not video content.
        var sps = new byte[] { 0x67, 0x42, 0xC0, 0x16, 0x8C, 0x68 };
        var pps = new byte[] { 0x68, 0xCE, 0x3C, 0x80 };
        var accessUnit = new byte[] { 0, 0, 0, 1, 0x65, 0xAA, 0xBB, 0xCC };

        using var stream = new MemoryStream();

        using (var writer = new Mp4Writer(stream, sps, pps, 640, 360))
        {
            writer.AddSample(accessUnit, 0);
            writer.AddSample(accessUnit, 3600);
            writer.Complete();
        }

        var bytes = stream.ToArray();
        var text = Encoding.ASCII.GetString(bytes);

        Assert.Equal(1, CountBoxes(text, "ftyp"));
        Assert.Contains("mdat", text, StringComparison.Ordinal);
        Assert.Contains("moov", text, StringComparison.Ordinal);
        Assert.Contains("avcC", text, StringComparison.Ordinal);
        Assert.Contains("stsz", text, StringComparison.Ordinal);
        Assert.True(bytes.Length > 300);
    }

    private static int CountBoxes(string text, string box) =>
        text.Split(box, StringSplitOptions.None).Length - 1;
}

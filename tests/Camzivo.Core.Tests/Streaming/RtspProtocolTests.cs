using System.Buffers.Binary;
using System.Text;
using Camzivo.Core.Streaming.Rtsp;
using Xunit;

namespace Camzivo.Core.Tests.Streaming;

/// <summary>
/// RTSP/RTP/SDP handling with fixtures shaped like real camera replies. Pure protocol tests: nothing here proves a
/// camera works, it proves the parser does not misread what a camera sends.
/// </summary>
public sealed class RtspProtocolTests
{
    private const string DahuaLikeSdp = """
        v=0
        o=- 1465901298 1 IN IP4 192.168.1.108
        s=Media Server
        c=IN IP4 0.0.0.0
        t=0 0
        a=control:*
        a=packetization-supported:DH
        a=rtppayload-supported:DH
        m=video 0 RTP/AVP 96
        a=control:trackID=0
        a=rtpmap:96 H264/90000
        a=fmtp:96 packetization-mode=1;profile-level-id=42C016;sprop-parameter-sets=Z0LAFoxoCgL3lwEB4RCNQA==,aM48gA==
        a=framerate:25
        m=audio 0 RTP/AVP 8
        a=control:trackID=1
        a=rtpmap:8 PCMA/8000
        """;

    [Fact]
    public void Sdp_ReadsVideoTrackCodecAndControl()
    {
        var sdp = SdpDescription.Parse(DahuaLikeSdp);

        var video = sdp.Video;
        Assert.NotNull(video);
        Assert.Equal(96, video!.PayloadTypes[0]);
        Assert.Equal("H264", video.VideoCodec!.Encoding);
        Assert.Equal(90000, video.VideoCodec.ClockRate);
        Assert.Equal("trackID=0", video.Control);
        Assert.Equal("PCMA", sdp.Audio!.VideoCodec?.Encoding ?? sdp.Audio.Codecs[0].Encoding);
    }

    [Fact]
    public void Sdp_ExtractsSpropParameterSets()
    {
        var video = SdpDescription.Parse(DahuaLikeSdp).Video!;
        var sets = video.ParameterSets;

        Assert.Equal(2, sets.Count);
        Assert.Equal(7, sets[0][0] & 0x1F); // SPS NAL type
        Assert.Equal(8, sets[1][0] & 0x1F); // PPS NAL type
    }

    [Fact]
    public void Sdp_ResolvesRelativeAbsoluteAndEmptyControl()
    {
        var baseUri = new Uri("rtsp://192.168.1.108:554/cam/realmonitor?channel=1&subtype=0");

        var relative = new SdpMedia("video", 0, "RTP/AVP", [96], "trackID=0", [], [], false);
        Assert.Equal("rtsp://192.168.1.108:554/cam/trackID=0", relative.ResolveControl(baseUri).AbsoluteUri);

        var absolute = new SdpMedia("video", 0, "RTP/AVP", [96], "rtsp://10.0.0.2/live", [], [], false);
        Assert.Equal("rtsp://10.0.0.2/live", absolute.ResolveControl(baseUri).AbsoluteUri);

        var empty = new SdpMedia("video", 0, "RTP/AVP", [96], null, [], [], false);
        Assert.Equal(baseUri, empty.ResolveControl(baseUri));
    }

    [Fact]
    public void Sdp_RejectsDocumentWithoutMedia()
    {
        Assert.Throws<InvalidDataException>(() => SdpDescription.Parse("v=0\ns=nothing\n"));
    }

    [Fact]
    public void Rtp_ParsesHeaderFieldsAndPayload()
    {
        var packet = BuildRtp(0x80, 0xE0, 0x1234, 0xDEADBEEF, 0x01020304, [0x65, 0xAA, 0xBB]);
        var rtp = new RtpPacket(0, false, 0, 0, 0, []);

        Assert.True(rtp.TryParse(packet, out var parsed, out _));
        Assert.Equal(96, parsed!.PayloadType);
        Assert.True(parsed.Marker);
        Assert.Equal(0x1234, parsed.Sequence);
        Assert.Equal(0xDEADBEEFu, parsed.Timestamp);
        Assert.Equal([0x65, 0xAA, 0xBB], parsed.Payload);
    }

    [Fact]
    public void Rtp_HonoursCsrcExtensionAndPadding()
    {
        // P=1, X=1, CC=2: 12-byte header, two CSRCs, one 4-byte extension header plus one word, payload, padding.
        var datagram = new List<byte> { 0xB2, 0x60, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01 };
        datagram.AddRange(new byte[8]);
        datagram.AddRange([0xBE, 0xDE, 0x00, 0x01, 0, 0, 0, 0]);
        datagram.AddRange([0x65, 0x01, 0x02]);
        datagram.AddRange([0x00, 0x00, 0x03]);

        Assert.True(new RtpPacket(0, false, 0, 0, 0, []).TryParse(datagram.ToArray(), out var parsed, out _));
        Assert.Equal([0x65, 0x01, 0x02], parsed!.Payload);
    }

    [Fact]
    public void Rtp_RejectsTruncatedAndWrongVersion()
    {
        Assert.False(new RtpPacket(0, false, 0, 0, 0, []).TryParse(new byte[6], out _, out var error));
        Assert.NotNull(error);

        var wrongVersion = BuildRtp(0x40, 0x60, 1, 1, 1, [0x65]);
        Assert.False(new RtpPacket(0, false, 0, 0, 0, []).TryParse(wrongVersion, out _, out _));
    }

    [Fact]
    public void H264_SingleNalBecomesOneAccessUnitAtMarker()
    {
        var depacketizer = new H264Depacketizer();
        var sps = new byte[] { 0x67, 0x42, 0x00 };
        var slice = new byte[] { 0x65, 0x11, 0x22 };

        Assert.False(depacketizer.TryAdd(sps, marker: false, out _));
        Assert.True(depacketizer.TryAdd(slice, marker: true, out var accessUnit));

        Assert.Equal([0, 0, 0, 1, 0x67, 0x42, 0x00, 0, 0, 0, 1, 0x65, 0x11, 0x22], accessUnit!);
    }

    [Fact]
    public void H264_FuAReassemblesFragmentSequence()
    {
        var depacketizer = new H264Depacketizer();

        // FU indicator 0x7C (type 28), FU header: start, idr (5), then middle, then end.
        Assert.False(depacketizer.TryAdd([0x7C, 0x85, 0x01, 0x02], marker: false, out _));
        Assert.False(depacketizer.TryAdd([0x7C, 0x05, 0x03, 0x04], marker: false, out _));
        Assert.True(depacketizer.TryAdd([0x7C, 0x45, 0x05], marker: true, out var accessUnit));

        Assert.Equal([0, 0, 0, 1, 0x65, 0x01, 0x02, 0x03, 0x04, 0x05], accessUnit!);
    }

    [Fact]
    public void H264_FuAContinuationWithoutStartIsDroppedAndCounted()
    {
        var depacketizer = new H264Depacketizer();

        Assert.False(depacketizer.TryAdd([0x7C, 0x45, 0x05], marker: true, out var accessUnit));

        Assert.Null(accessUnit);
        Assert.Equal(1, depacketizer.DroppedAccessUnits);
    }

    [Fact]
    public void H264_StapAAggregateSplitsIntoNals()
    {
        var depacketizer = new H264Depacketizer();

        byte[] stap = [0x78, 0x00, 0x02, 0x67, 0x42, 0x00, 0x02, 0x68, 0xCE];
        Assert.True(depacketizer.TryAdd(stap, marker: true, out var accessUnit));

        Assert.Equal([0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0xCE], accessUnit!);
    }

    [Fact]
    public void H264_PrependsSdpParameterSetsOnce()
    {
        var depacketizer = new H264Depacketizer([[0x67, 0x42], [0x68, 0xCE]]);

        Assert.True(depacketizer.TryAdd([0x65, 0x01], marker: true, out var first));
        Assert.True(depacketizer.TryAdd([0x65, 0x02], marker: true, out var second));

        Assert.Equal([0, 0, 0, 1, 0x67, 0x42, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 0, 1, 0x65, 0x01], first!);
        Assert.Equal([0, 0, 0, 1, 0x65, 0x02], second!);
    }

    [Fact]
    public void Rtsp_ParsesResponseStatusHeadersAndCasing()
    {
        var response = RtspMessage.ParseResponse(
            "RTSP/1.0 401 Unauthorized\r\nCSeq: 2\r\nWWW-Authenticate: Digest realm=\"IP Camera\", nonce=\"abc\"\r\n\r\n");

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("2", response.Header("cseq"));
        Assert.Contains("nonce=\"abc\"", response.Header("WWW-Authenticate"), StringComparison.Ordinal);
    }

    [Fact]
    public void Rtsp_RejectsGarbageInsteadOfGuessing()
    {
        Assert.Throws<InvalidDataException>(() => RtspMessage.ParseResponse("HTTP/1.1 200 OK\r\n\r\n"));
        Assert.Throws<InvalidDataException>(() => RtspMessage.ParseResponse("RTSP/1.0 OK\r\n\r\n"));
    }

    [Fact]
    public void Rtsp_RequestUsesAbsoluteUrlAndCountsBytes()
    {
        var uri = new Uri("rtsp://192.168.1.108:554/cam/realmonitor?channel=1&subtype=0");
        var request = new RtspRequest("DESCRIBE", uri, 7)
            .WithHeader("Accept", "application/sdp")
            .Build();

        // A Dahua recorder answers 503 to the origin-form path and the normal 401 challenge to the absolute URL, which
        // is also what ffmpeg, VLC and OpenCV send.
        Assert.StartsWith(
            "DESCRIBE rtsp://192.168.1.108:554/cam/realmonitor?channel=1&subtype=0 RTSP/1.0\r\n",
            request,
            StringComparison.Ordinal);
        Assert.Contains("CSeq: 7\r\n", request, StringComparison.Ordinal);
        Assert.Contains("Accept: application/sdp\r\n", request, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", request, StringComparison.Ordinal);
    }

    private static byte[] BuildRtp(byte first, byte second, ushort sequence, uint timestamp, uint ssrc, byte[] payload, int? totalLength = null)
    {
        var buffer = new byte[12 + (totalLength ?? payload.Length)];
        buffer[0] = first;
        buffer[1] = second;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), ssrc);
        payload.CopyTo(buffer.AsSpan(12));
        return buffer;
    }
}

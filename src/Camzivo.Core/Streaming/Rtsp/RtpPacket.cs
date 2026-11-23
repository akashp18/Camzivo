using System.Buffers.Binary;

namespace Camzivo.Core.Streaming.Rtsp;

/// <summary>
/// One RTP packet (RFC 3550), header parsed and payload copied out.
///
/// The payload is copied rather than kept as a slice: TCP interleaved reads reuse their buffer, and a retained slice
/// would be silently overwritten — the classic "works at 5 fps, corrupts at 25" bug.
/// </summary>
public sealed record RtpPacket(
    int PayloadType,
    bool Marker,
    ushort Sequence,
    uint Timestamp,
    uint Ssrc,
    byte[] Payload)
{
    public bool TryParse(ReadOnlySpan<byte> datagram, out RtpPacket? packet, out string? error)
    {
        packet = null;
        error = null;

        if (datagram.Length < 12)
        {
            error = "RTP packet is shorter than its header.";
            return false;
        }

        var first = datagram[0];
        var version = first >> 6;

        if (version != 2)
        {
            error = $"Unsupported RTP version {version}.";
            return false;
        }

        var hasPadding = (first & 0x20) != 0;
        var hasExtension = (first & 0x10) != 0;
        var csrcCount = first & 0x0F;
        var marker = (datagram[1] & 0x80) != 0;
        var payloadType = datagram[1] & 0x7F;
        var sequence = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]);
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]);

        var offset = 12 + (csrcCount * 4);
        if (offset > datagram.Length)
        {
            error = "RTP packet header is truncated.";
            return false;
        }

        if (hasExtension)
        {
            if (offset + 4 > datagram.Length)
            {
                error = "RTP extension header is truncated.";
                return false;
            }

            var words = BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..]);
            offset += 4 + (words * 4);

            if (offset > datagram.Length)
            {
                error = "RTP extension exceeds the packet.";
                return false;
            }
        }

        var end = datagram.Length;
        if (hasPadding)
        {
            var padding = datagram[end - 1];
            if (padding == 0 || padding > end - offset)
            {
                error = "RTP padding length is invalid.";
                return false;
            }

            end -= padding;
        }

        if (end <= offset)
        {
            error = "RTP payload is empty.";
            return false;
        }

        packet = new RtpPacket(payloadType, marker, sequence, timestamp, ssrc, datagram[offset..end].ToArray());
        return true;
    }
}

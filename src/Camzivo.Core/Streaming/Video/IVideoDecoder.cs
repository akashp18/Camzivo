namespace Camzivo.Core.Streaming.Video;

/// <summary>
/// Decoder seam. Two implementations are planned: Media Foundation (present in Windows, no redistributable) and
/// FFmpeg (LGPL shared build, optional). Nothing above this interface knows which one produced the frame, and a
/// decoder that cannot initialise must fail loudly rather than return black frames.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>Backend name for diagnostics, e.g. "Media Foundation H.264".</summary>
    string Name { get; }

    /// <summary>True only when the backend reports hardware acceleration is actually in use.</summary>
    bool HardwareAccelerated { get; }

    /// <summary>Frame dimensions once known; 0 before the first decoded frame.</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>
    /// Decodes one Annex-B access unit. Returns zero frames while the decoder is still consuming parameter sets, or
    /// when reordering withholds output — never a placeholder frame.
    /// </summary>
    IReadOnlyList<VideoFrame> Decode(ReadOnlySpan<byte> annexBAccessUnit, TimeSpan timestamp);

    /// <summary>Drops buffered state on stream restart (new SPS/PPS, resolution change, reconnect).</summary>
    void Reset();
}

/// <summary>
/// Optional decoder seam for accelerated playback: lets a session reject presentation of a decoded picture before the
/// expensive NV12→BGRA conversion. The access unit is still decoded, so reference frames and decoder state are intact.
/// </summary>
public interface IPresentationFilteredDecoder
{
    /// <summary>Returns false to discard presentation (not decoding) for this source timestamp.</summary>
    Func<TimeSpan, bool>? ShouldPresent { get; set; }
}

public sealed class VideoDecoderException(string message, Exception? inner = null) : Exception(message, inner);

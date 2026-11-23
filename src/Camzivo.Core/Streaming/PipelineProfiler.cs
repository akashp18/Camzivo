using System.Diagnostics;

namespace Camzivo.Core.Streaming;

/// <summary>
/// Per-stage timing for one stream. Every stage is measured with <see cref="Stopwatch.GetTimestamp"/> around the work
/// it names, so the numbers come from the running pipeline rather than from a guess about where time goes.
///
/// Read-only snapshot at any time; all writers use interlocked adds so the reader never blocks the decoder.
/// </summary>
public sealed class PipelineProfiler
{
    private long _receiveTicks;
    private long _depacketizeTicks;
    private long _decodeTicks;
    private long _convertTicks;
    private long _packets;
    private long _bytes;
    private long _accessUnits;
    private long _frames;

    public static double MillisecondsPerTick => 1000.0 / Stopwatch.Frequency;

    public void AddReceive(long ticks, int bytes)
    {
        Interlocked.Add(ref _receiveTicks, ticks);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Increment(ref _packets);
    }

    public void AddDepacketize(long ticks) => Interlocked.Add(ref _depacketizeTicks, ticks);

    public void AddDecode(long ticks) => Interlocked.Add(ref _decodeTicks, ticks);

    public void AddConvert(long ticks) => Interlocked.Add(ref _convertTicks, ticks);

    public void AddAccessUnit() => Interlocked.Increment(ref _accessUnits);

    public void AddFrames(int count) => Interlocked.Add(ref _frames, count);

    public PipelineSnapshot Snapshot() => new(
        Interlocked.Read(ref _packets),
        Interlocked.Read(ref _bytes),
        Interlocked.Read(ref _accessUnits),
        Interlocked.Read(ref _frames),
        Interlocked.Read(ref _receiveTicks) * MillisecondsPerTick,
        Interlocked.Read(ref _depacketizeTicks) * MillisecondsPerTick,
        Interlocked.Read(ref _decodeTicks) * MillisecondsPerTick,
        Interlocked.Read(ref _convertTicks) * MillisecondsPerTick);
}

/// <summary>Accumulated milliseconds per stage, plus the counts needed to turn them into per-frame costs.</summary>
public sealed record PipelineSnapshot(
    long Packets,
    long Bytes,
    long AccessUnits,
    long Frames,
    double ReceiveMs,
    double DepacketizeMs,
    double DecodeMs,
    double ConvertMs)
{
    public static PipelineSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    public PipelineSnapshot Minus(PipelineSnapshot earlier) => new(
        Packets - earlier.Packets,
        Bytes - earlier.Bytes,
        AccessUnits - earlier.AccessUnits,
        Frames - earlier.Frames,
        ReceiveMs - earlier.ReceiveMs,
        DepacketizeMs - earlier.DepacketizeMs,
        DecodeMs - earlier.DecodeMs,
        ConvertMs - earlier.ConvertMs);

    public double DecodeMsPerFrame => Frames > 0 ? (DecodeMs - ConvertMs) / Frames : 0;

    public double ConvertMsPerFrame => Frames > 0 ? ConvertMs / Frames : 0;

    public double DepacketizeMsPerAccessUnit => AccessUnits > 0 ? DepacketizeMs / AccessUnits : 0;

    public double ReceiveMsPerPacket => Packets > 0 ? ReceiveMs / Packets : 0;
}

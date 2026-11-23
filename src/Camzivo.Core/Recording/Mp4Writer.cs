using System.Buffers.Binary;
using System.Text;

namespace Camzivo.Core.Recording;

/// <summary>
/// Minimal, honest MP4 (ISO base media) muxer for H.264 evidence: one video track, real sample table, real avcC from
/// the stream's own SPS/PPS. It does not copy Bitstream H.264 into an .mp4 container by renaming: Annex-B start codes
/// become length-prefixed NAL units, and durations come from the RTP clock.
///
/// Sample data streams to disk (only sizes and timestamps are kept in memory), so a long export cannot exhaust RAM.
/// </summary>
public sealed class Mp4Writer : IDisposable
{
    private const int Timescale = 90000;
    private const uint Brand = 0x69736F6D; // "isom"
    private const uint Avc1 = 0x61766331;
    private const uint Ftyp = 0x66747970;
    private const uint Mdat = 0x6D646174;
    private const uint Moov = 0x6D6F6F76;

    private readonly Stream _output;
    private readonly BeWriter _writer;
    private readonly List<(uint Size, uint Duration, bool Sync)> _samples = [];
    private readonly byte[] _sps;
    private readonly byte[] _pps;
    private readonly int _width;
    private readonly int _height;
    private readonly long _mdatStart;
    private long _mdatDataStart;
    private uint? _lastTimestamp;
    private bool _completed;

    public Mp4Writer(Stream output, byte[] sps, byte[] pps, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(sps);
        ArgumentNullException.ThrowIfNull(pps);

        _output = output;
        _writer = new BeWriter(output, Encoding.UTF8, leaveOpen: true);
        _sps = sps;
        _pps = pps;
        _width = width;
        _height = height;

        WriteFtyp();
        _mdatStart = _output.Position;
        WriteUInt32(0); // mdat size, patched on completion
        WriteUInt32(Mdat);
        _mdatDataStart = _output.Position;
    }

    public int SampleCount => _samples.Count;

    /// <summary>Adds one Annex-B access unit; its duration is the gap to the next sample (fixup at completion).</summary>
    public void AddSample(byte[] annexB, uint timestamp)
    {
        ArgumentNullException.ThrowIfNull(annexB);
        ObjectDisposedException.ThrowIf(_completed, this);

        var nals = SplitAnnexB(annexB);
        if (nals.Count == 0)
        {
            return;
        }

        var isSync = nals.Any(nal => (nal[0] & 0x1F) == 5);
        var sizeStart = _output.Position;

        foreach (var nal in nals)
        {
            WriteUInt32((uint)nal.Length);
            _output.Write(nal);
        }

        var size = (uint)(_output.Position - sizeStart);

        // The previous sample's duration is this gap; the last sample gets the same duration as its predecessor.
        uint duration = 3600;
        if (_lastTimestamp is { } previous)
        {
            var delta = timestamp >= previous ? timestamp - previous : 0;
            duration = delta == 0 ? 3600u : delta;

            if (_samples.Count > 0)
            {
                _samples[^1] = (_samples[^1].Size, duration, _samples[^1].Sync);
            }
        }

        _samples.Add((size, duration, isSync));
        _lastTimestamp = timestamp;
    }

    /// <summary>Writes the sample tables and finishes the file. Calling it twice is a no-op.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        if (_samples.Count == 0)
        {
            throw new InvalidOperationException("An export with no decoded samples cannot be written as evidence.");
        }

        var mdatEnd = _output.Position;
        _output.Position = _mdatStart;
        WriteUInt32((uint)(mdatEnd - _mdatStart));
        _output.Position = mdatEnd;

        WriteMoov();
        _writer.Flush();
        _output.Flush();
        _completed = true;
    }

    private void WriteFtyp()
    {
        // 8-byte header + major brand + minor version + three compatible brands.
        WriteUInt32(32);
        WriteUInt32(Ftyp);
        WriteUInt32(Brand);
        WriteUInt32(0x00000200);
        WriteUInt32(Brand);
        WriteUInt32(0x69736F32); // isom
        WriteUInt32(0x61766331); // avc1
        WriteUInt32(0x6D703431); // mp41
    }

    private void WriteMoov()
    {
        // BuildMoov returns the moov payload; the box header is written here so the file has a real top-level moov.
        var payload = BuildMoov();
        _writer.Write((uint)(payload.Length + 8));
        _writer.Write(Ascii("moov"));
        _writer.Write(payload);
    }

    private byte[] BuildMoov()
    {
        using var moov = new MemoryStream();
        using var writer = new BeWriter(moov, Encoding.UTF8, leaveOpen: true);

        WriteBoxHeader(writer, "mvhd", 100);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(Timescale);
        writer.Write(TotalDuration());
        writer.Write(0x00010000);
        writer.Write((short)0x0100);
        writer.Write(new byte[10]);
        WriteMatrix(writer);
        for (var index = 0; index < 6; index++)
        {
            writer.Write(0);
        }

        writer.Write(NextTrackId);

        using var trak = new MemoryStream();
        using (var trakWriter = new BeWriter(trak, Encoding.UTF8, leaveOpen: true))
        {
            WriteBoxHeader(trakWriter, "tkhd", 84);
            trakWriter.Write(0x00000007);
            trakWriter.Write(0);
            trakWriter.Write(0);
            trakWriter.Write(1);
            trakWriter.Write(0);
            trakWriter.Write(TotalDuration());
            trakWriter.Write(0);
            trakWriter.Write(0);
            trakWriter.Write((short)0);
            trakWriter.Write((short)0);
            trakWriter.Write((short)0);
            trakWriter.Write((short)0);
            WriteMatrix(trakWriter);
            trakWriter.Write(Fixed(_width));
            trakWriter.Write(Fixed(_height));

            using var mdia = new MemoryStream();
            using (var mdiaWriter = new BeWriter(mdia, Encoding.UTF8, leaveOpen: true))
            {
                WriteBoxHeader(mdiaWriter, "mdhd", 24);
                mdiaWriter.Write(0);
                mdiaWriter.Write(0);
                mdiaWriter.Write(0);
                mdiaWriter.Write(Timescale);
                mdiaWriter.Write(TotalDuration());
                mdiaWriter.Write((short)0x55C4);
                mdiaWriter.Write((short)0);

                WriteBoxHeader(mdiaWriter, "hdlr", 37);
                mdiaWriter.Write(0);
                mdiaWriter.Write(0);
                mdiaWriter.Write(Ascii("vide"));
                mdiaWriter.Write(0);
                mdiaWriter.Write(0);
                mdiaWriter.Write(0);
                mdiaWriter.Write(Ascii("VideoHandler"));
                mdiaWriter.Write((byte)0);

                using var minf = new MemoryStream();
                using (var minfWriter = new BeWriter(minf, Encoding.UTF8, leaveOpen: true))
                {
                    WriteBoxHeader(minfWriter, "vmhd", 12);
                    minfWriter.Write(1);
                    minfWriter.Write((short)0);
                    minfWriter.Write((short)0);
                    minfWriter.Write((short)0);
                    minfWriter.Write((short)0);

                    using var dinf = new MemoryStream();
                    using (var dinfWriter = new BeWriter(dinf, Encoding.UTF8, leaveOpen: true))
                    {
                        WriteBoxHeader(dinfWriter, "dref", 20);
                        dinfWriter.Write(0);
                        dinfWriter.Write(1);
                        WriteBoxHeader(dinfWriter, "url ", 4);
                        dinfWriter.Write(1);
                    }

                    WriteBox(minfWriter, "dinf", dinf.ToArray());

                    using var stbl = new MemoryStream();
                    using (var stblWriter = new BeWriter(stbl, Encoding.UTF8, leaveOpen: true))
                    {
                        WriteStsd(stblWriter);
                        WriteStts(stblWriter);
                        WriteStss(stblWriter);
                        WriteStsc(stblWriter);
                        WriteStsz(stblWriter);
                        WriteStco(stblWriter);
                    }

                    WriteBox(minfWriter, "stbl", stbl.ToArray());
                }

                WriteBox(mdiaWriter, "minf", minf.ToArray());
            }

            WriteBox(trakWriter, "mdia", mdia.ToArray());
        }

        WriteBox(writer, "trak", trak.ToArray());
        writer.Flush();
        return moov.ToArray();
    }

    private void WriteStsd(BeWriter writer)
    {
        using var payload = new MemoryStream();
        using (var p = new BeWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            p.Write(0);
            p.Write(1);

            using var avc1 = new MemoryStream();
            using (var a = new BeWriter(avc1, Encoding.UTF8, leaveOpen: true))
            {
                a.Write(new byte[6]);
                a.Write((short)1);
                a.Write(0);
                a.Write(0);
                a.Write(0);
                a.Write(0);
                a.Write((short)_width);
                a.Write((short)_height);
                a.Write(0x00480000);
                a.Write(0x00480000);
                a.Write(0);
                a.Write((short)1);
                a.Write(CompressorName("Camzivo H.264"));
                a.Write((short)24);
                a.Write((short)-1);
                WriteBox(a, "avcC", AvcC());
            }

            WriteBox(p, "avc1", avc1.ToArray());
        }

        WriteBox(writer, "stsd", payload.ToArray());
    }

    private byte[] AvcC()
    {
        using var stream = new MemoryStream();
        using var writer = new BeWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write((byte)1);
        writer.Write(_sps.Length > 1 ? _sps[1] : (byte)0x42);
        writer.Write(_sps.Length > 2 ? _sps[2] : (byte)0x00);
        writer.Write(_sps.Length > 3 ? _sps[3] : (byte)0x1E);
        writer.Write((byte)0xFF); // 4-byte NAL lengths
        writer.Write((byte)0xE1);
        writer.Write((short)_sps.Length);
        writer.Write(_sps);
        writer.Write((byte)1);
        writer.Write((short)_pps.Length);
        writer.Write(_pps);
        writer.Flush();

        return stream.ToArray();
    }

    private void WriteStts(BeWriter writer)
    {
        // Consecutive samples with equal durations share one entry; real replay streams are mostly constant-rate.
        var entries = new List<(uint Count, uint Duration)>();

        foreach (var sample in _samples)
        {
            if (entries.Count > 0 && entries[^1].Duration == sample.Duration)
            {
                entries[^1] = (entries[^1].Count + 1, sample.Duration);
            }
            else
            {
                entries.Add((1, sample.Duration));
            }
        }

        using var payload = new MemoryStream();
        using (var p = new BeWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            p.Write(0);
            p.Write((uint)entries.Count);

            foreach (var (count, duration) in entries)
            {
                p.Write(count);
                p.Write(duration);
            }
        }

        WriteBox(writer, "stts", payload.ToArray());
    }

    private void WriteStss(BeWriter writer)
    {
        var sync = _samples.Select((sample, index) => (sample, index: index + 1))
            .Where(pair => pair.sample.Sync)
            .Select(pair => (uint)pair.index)
            .ToList();

        using var payload = new MemoryStream();
        using (var p = new BeWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            p.Write(0);
            p.Write((uint)sync.Count);

            foreach (var index in sync)
            {
                p.Write(index);
            }
        }

        WriteBox(writer, "stss", payload.ToArray());
    }

    private void WriteStsc(BeWriter writer)
    {
        using var payload = new MemoryStream();
        using (var p = new BeWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            p.Write(0);
            p.Write(1);
            p.Write(1);
            p.Write((uint)_samples.Count);
            p.Write(1);
        }

        WriteBox(writer, "stsc", payload.ToArray());
    }

    private void WriteStsz(BeWriter writer)
    {
        using var payload = new MemoryStream();
        using (var p = new BeWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            p.Write(0);
            p.Write(0);
            p.Write((uint)_samples.Count);

            foreach (var sample in _samples)
            {
                p.Write(sample.Size);
            }
        }

        WriteBox(writer, "stsz", payload.ToArray());
    }

    private void WriteStco(BeWriter writer)
    {
        using var payload = new MemoryStream();
        using (var p = new BeWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            p.Write(0);
            p.Write(1);
            p.Write((uint)_mdatDataStart);
        }

        WriteBox(writer, "stco", payload.ToArray());
    }

    private uint TotalDuration()
    {
        var total = 0u;

        foreach (var sample in _samples)
        {
            total += sample.Duration;
        }

        return total;
    }

    private const uint NextTrackId = 2;

    private static int Fixed(int value) => value << 16;

    private static void WriteMatrix(BeWriter writer)
    {
        writer.Write(0x00010000);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0x00010000);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0x40000000);
    }

    private static void WriteBoxHeader(BeWriter writer, string type, int size)
    {
        writer.Write((uint)(size + 8));
        writer.Write(Ascii(type));
    }

    private static void WriteBox(BeWriter writer, string type, byte[] payload)
    {
        writer.Write((uint)(payload.Length + 8));
        writer.Write(Ascii(type));
        writer.Write(payload);
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    /// <summary>avc1's compressorname is a fixed 32-byte Pascal string; a short one misaligns every later field.</summary>
    private static byte[] CompressorName(string name)
    {
        var field = new byte[32];
        var bytes = Encoding.ASCII.GetBytes(name);
        var length = Math.Min(bytes.Length, 31);
        field[0] = (byte)length;
        Array.Copy(bytes, 0, field, 1, length);
        return field;
    }

    private void WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        _output.Write(buffer);
    }

    private static List<byte[]> SplitAnnexB(byte[] data)
    {
        var starts = new List<int>();

        for (var index = 0; index + 3 <= data.Length; index++)
        {
            if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
            {
                starts.Add(index + 3);
                index += 2;
            }
            else if (index + 4 <= data.Length && data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 0 && data[index + 3] == 1)
            {
                starts.Add(index + 4);
                index += 3;
            }
        }

        var nals = new List<byte[]>(starts.Count);

        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            var end = index + 1 < starts.Count ? starts[index + 1] - 3 : data.Length;

            while (end > start && (data[end - 1] == 0 || data[end - 1] == 1))
            {
                end--;
            }

            if (end > start)
            {
                var nal = new byte[end - start];
                Array.Copy(data, start, nal, 0, nal.Length);
                nals.Add(nal);
            }
        }

        return nals;
    }

    public void Dispose()
    {
        _writer.Dispose();
    }

    /// <summary>Big-endian writer for ISO base media fields.</summary>
    private sealed class BeWriter(Stream stream, Encoding encoding, bool leaveOpen = false) : IDisposable
    {
        private readonly Stream _stream = stream;

        public void Write(byte value) => _stream.WriteByte(value);

        public void Write(byte[] value) => _stream.Write(value, 0, value.Length);

        public void Write(short value) => Write((ushort)value);

        public void Write(int value) => Write((uint)value);

        public void Write(uint value)
        {
            _stream.WriteByte((byte)(value >> 24));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void Write(ushort value)
        {
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)value);
        }

        public void Flush() => _stream.Flush();

        public void Dispose()
        {
            if (!leaveOpen)
            {
                _stream.Dispose();
            }
        }

        private readonly Encoding _encoding = encoding;
    }
}

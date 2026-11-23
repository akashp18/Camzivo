using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Camzivo.Core.Streaming.Video;

/// <summary>
/// H.264 decoding through the Windows Media Foundation decoder MFT.
///
/// Why this backend first: it is an OS component (nothing to redistribute, no licence question), its COM ABI is
/// stable across Windows versions, and it is present on every supported desktop. Hardware acceleration is reported
/// honestly as false: DXVA needs a D3D11 device manager wired into the MFT, which this milestone does not do.
/// The FFmpeg backend implements the same interface once an LGPL build is vendored.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationH264Decoder : IVideoDecoder, IPresentationFilteredDecoder
{
    private static readonly Lazy<bool> Startup = new(() =>
        MediaFoundationInterop.MFStartup(MediaFoundationInterop.MfVersion, MediaFoundationInterop.MfStartupFull) >= 0);

    private IMFTransform? _transform;
    private IMFMediaType? _outputType;
    private int _codedWidth;
    private int _codedHeight;
    private int _width;
    private int _height;
    private int _stride;
    private bool _planar;
    private bool _disposed;

    /// <summary>Optional stage timing sink; the owning session sets this so conversion cost is attributed, not guessed.</summary>
    public PipelineProfiler? Profiler { get; set; }

    /// <summary>
    /// Set by replay at accelerated speeds. The MFT has already decoded the access unit; returning false here only
    /// skips the colour conversion and allocation for pictures that would be coalesced away by presentation.
    /// </summary>
    public Func<TimeSpan, bool>? ShouldPresent { get; set; }

    public string Name => "Media Foundation H.264";

    public bool HardwareAccelerated => false;

    public int Width => _width;

    public int Height => _height;

    public IReadOnlyList<VideoFrame> Decode(ReadOnlySpan<byte> annexBAccessUnit, TimeSpan timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (annexBsearchEmpty(annexBAccessUnit))
        {
            return [];
        }

        UpdateVisibleSizeFromSps(annexBAccessUnit);

        if (!Startup.Value)
        {
            throw new VideoDecoderException("Media Foundation could not be initialised on this system.");
        }

        EnsureTransform();

        var input = CreateSample(annexBAccessUnit, timestamp);

        try
        {
            var hr = _transform!.ProcessInput(0, input, 0);

            if (hr < 0)
            {
                throw new VideoDecoderException($"The decoder rejected an access unit (0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
            }
        }
        finally
        {
            // Deterministic release: leaving per-frame samples to the finalizer kept COM buffers alive longer than
            // the decoder needed them.
            Marshal.ReleaseComObject(input);
        }

        return Drain(timestamp);
    }

    private static bool annexBsearchEmpty(ReadOnlySpan<byte> data) => data.Length < 5;

    public void Reset()
    {
        ReleaseTransform();

        // A new stream brings its own SPS; the previous visible size must not survive it.
        _width = 0;
        _height = 0;
    }

    private List<VideoFrame> Drain(TimeSpan timestamp)
    {
        var frames = new List<VideoFrame>(1);
        var info = default(MftOutputStreamInfo);

        while (true)
        {
            MediaFoundationInterop.ThrowIfFailed(_transform!.GetOutputStreamInfo(0, out info), "GetOutputStreamInfo");

            var buffer = new MftOutputDataBuffer { StreamId = 0 };

            if ((info.Flags & MftOutputFlags.ProvideSamples) == 0)
            {
                if (info.Size <= 0 && _width > 0)
                {
                    info.Size = (uint)(_width * _height * 2);
                }

                MediaFoundationInterop.ThrowIfFailed(
                    MediaFoundationInterop.MFCreateSample(out var sample),
                    "MFCreateSample");
                MediaFoundationInterop.ThrowIfFailed(
                    MediaFoundationInterop.MFCreateMemoryBuffer((int)Math.Max(info.Size, 1024), out var mediaBuffer),
                    "MFCreateMemoryBuffer");

                sample.AddBuffer(mediaBuffer);
                buffer.Sample = sample;
            }

            var hr = _transform.ProcessOutput(0, 1, ref buffer, out _);

            if (hr == unchecked((int)MediaFoundationInterop.MfETransformNeedMoreInput))
            {
                return frames;
            }

            if (hr == unchecked((int)MediaFoundationInterop.MfETransformStreamChange))
            {
                if (!TrySetOutputType())
                {
                    throw new VideoDecoderException("The decoder changed stream format and offered no supported output type.");
                }

                continue;
            }

            if (hr < 0)
            {
                throw new VideoDecoderException($"ProcessOutput failed (0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
            }

            if (buffer.Sample is not { } output)
            {
                return frames;
            }

            try
            {
                var frame = ReadFrame(output, timestamp);
                if (frame is not null)
                {
                    frames.Add(frame);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(output);
            }
        }
    }

    private VideoFrame? ReadFrame(IMFSample sample, TimeSpan fallbackTimestamp)
    {
        var timestamp = fallbackTimestamp;
        if (sample.GetSampleTime(out var sampleTime) >= 0 && sampleTime > 0)
        {
            timestamp = TimeSpan.FromTicks(sampleTime);
        }

        // Presentation is decided before the raw NV12 buffer is copied into managed memory. At 16× that copy is the
        // dominant per-frame cost for pictures the UI would immediately coalesce away.
        if (ShouldPresent is { } shouldPresent && !shouldPresent(timestamp))
        {
            return null;
        }

        MediaFoundationInterop.ThrowIfFailed(sample.ConvertToContiguousBuffer(out var buffer), "ConvertToContiguousBuffer");

        var hr = buffer.Lock(out var pointer, out _, out var length);
        if (hr < 0 || pointer == IntPtr.Zero || length <= 0)
        {
            buffer.Unlock();
            return null;
        }

        try
        {
            var data = new byte[length];
            Marshal.Copy(pointer, data, 0, length);

            var stride = Math.Abs(_stride) == 0 ? _codedWidth : Math.Abs(_stride);
            var convertStart = System.Diagnostics.Stopwatch.GetTimestamp();

            VideoFrame? frame = _planar
                ? YuvConverter.Nv12ToBgra(
                    data,
                    _codedWidth,
                    _codedHeight,
                    stride,
                    (_codedWidth + 1) / 2,
                    timestamp,
                    planar: true,
                    visibleWidth: _width,
                    visibleHeight: _height)
                : YuvConverter.Nv12ToBgra(
                    data,
                    _codedWidth,
                    _codedHeight,
                    stride,
                    stride,
                    timestamp,
                    visibleWidth: _width,
                    visibleHeight: _height);

            Profiler?.AddConvert(System.Diagnostics.Stopwatch.GetTimestamp() - convertStart);
            return frame;
        }
        finally
        {
            buffer.Unlock();
            Marshal.ReleaseComObject(buffer);
        }
    }

    private void EnsureTransform()
    {
        if (_transform is not null && _outputType is not null)
        {
            return;
        }

        ReleaseTransform();

        var clsid = MediaFoundationInterop.ClsidH264Decoder;
        var iid = typeof(IMFTransform).GUID;

        MediaFoundationInterop.ThrowIfFailed(
            MediaFoundationInterop.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var instance),
            "CoCreateInstance(H.264 decoder)");

        _transform = (IMFTransform)instance;

        MediaFoundationInterop.ThrowIfFailed(
            MediaFoundationInterop.MFCreateMediaType(out var inputType),
            "MFCreateMediaType(input)");

        try
        {
            var majorKey = MediaFoundationInterop.MF_MT_MAJOR_TYPE;
            var subtypeKey = MediaFoundationInterop.MF_MT_SUBTYPE;
            var major = MediaFoundationInterop.MFMediaType_Video;
            var subtype = MediaFoundationInterop.MFVideoFormat_H264;

            MediaFoundationInterop.ThrowIfFailed(inputType.SetGUID(ref majorKey, ref major), "SetGUID(major)");
            MediaFoundationInterop.ThrowIfFailed(inputType.SetGUID(ref subtypeKey, ref subtype), "SetGUID(subtype)");

            var setInput = _transform.SetInputType(0, inputType, 0);
            MediaFoundationInterop.ThrowIfFailed(setInput, "SetInputType(H264)");
        }
        finally
        {
            Marshal.ReleaseComObject(inputType);
        }

        if (!TrySetOutputType())
        {
            throw new VideoDecoderException("The H.264 decoder offered no NV12/I420 output type.");
        }
    }

    /// <summary>
    /// Picks an 8-bit 4:2:0 output. NV12 is preferred; I420 is converted separately. 10-bit and DXVA surfaces are
    /// rejected rather than silently mis-rendered.
    /// </summary>
    private bool TrySetOutputType()
    {
        for (var index = 0; ; index++)
        {
            var hr = _transform!.GetOutputAvailableType(0, index, out var type);
            if (hr < 0)
            {
                return false;
            }

            try
            {
                var subtypeKey = MediaFoundationInterop.MF_MT_SUBTYPE;
                if (type.GetGUID(ref subtypeKey, out var subtype) < 0)
                {
                    continue;
                }

                var nv12 = MediaFoundationInterop.MFVideoFormat_NV12;
                var i420 = MediaFoundationInterop.MFVideoFormat_I420;
                var yv12 = MediaFoundationInterop.MFVideoFormat_YV12;

                if (subtype != nv12 && subtype != i420 && subtype != yv12)
                {
                    continue;
                }

                var (width, height) = MediaFoundationInterop.UnpackSize(type, MediaFoundationInterop.MF_MT_FRAME_SIZE);
                if (width <= 0 || height <= 0)
                {
                    continue;
                }

                if (_transform.SetOutputType(0, type, 0) < 0)
                {
                    continue;
                }

                _outputType = type;
                _codedWidth = width;
                _codedHeight = height;

                // Until an SPS says otherwise, the decoder's padded size is the only size known.
                if (_width == 0 || _height == 0)
                {
                    _width = width;
                    _height = height;
                }
                _planar = subtype != nv12;
                var strideKey = MediaFoundationInterop.MF_MT_DEFAULT_STRIDE;
                _stride = type.GetUINT32(ref strideKey, out var stride) >= 0 && stride != 0
                    ? stride
                    : width;

                return true;
            }
            finally
            {
                if (!ReferenceEquals(type, _outputType))
                {
                    Marshal.ReleaseComObject(type);
                }
            }
        }
    }

    private static IMFSample CreateSample(ReadOnlySpan<byte> data, TimeSpan timestamp)
    {
        MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateSample(out var sample), "MFCreateSample");
        MediaFoundationInterop.ThrowIfFailed(
            MediaFoundationInterop.MFCreateMemoryBuffer(data.Length, out var buffer),
            "MFCreateMemoryBuffer");

        if (buffer.Lock(out var pointer, out _, out _) < 0)
        {
            Marshal.ReleaseComObject(buffer);
            throw new VideoDecoderException("Could not lock the input buffer.");
        }

        try
        {
            var copy = data.ToArray();
            Marshal.Copy(copy, 0, pointer, copy.Length);
        }
        finally
        {
            buffer.Unlock();
        }

        MediaFoundationInterop.ThrowIfFailed(buffer.SetCurrentLength(data.Length), "SetCurrentLength");
        MediaFoundationInterop.ThrowIfFailed(sample.AddBuffer(buffer), "AddBuffer");
        MediaFoundationInterop.ThrowIfFailed(sample.SetSampleTime(timestamp.Ticks), "SetSampleTime");

        Marshal.ReleaseComObject(buffer);
        return sample;
    }

    /// <summary>Finds an SPS in the access unit and records the real picture size.</summary>
    private void UpdateVisibleSizeFromSps(ReadOnlySpan<byte> accessUnit)
    {
        var index = 0;

        while (index + 4 < accessUnit.Length)
        {
            if (accessUnit[index] != 0 || accessUnit[index + 1] != 0)
            {
                index++;
                continue;
            }

            var start = accessUnit[index + 2] == 1 ? index + 3 : accessUnit[index + 2] == 0 && accessUnit[index + 3] == 1 ? index + 4 : -1;
            if (start < 0 || start >= accessUnit.Length)
            {
                index++;
                continue;
            }

            var end = accessUnit.Length;
            for (var next = start; next + 2 < accessUnit.Length; next++)
            {
                if (accessUnit[next] == 0 && accessUnit[next + 1] == 0 && (accessUnit[next + 2] == 1 || (accessUnit[next + 2] == 0 && next + 3 < accessUnit.Length && accessUnit[next + 3] == 1)))
                {
                    end = next;
                    break;
                }
            }

            if ((accessUnit[start] & 0x1F) == 7 && H264Sps.TryReadDimensions(accessUnit[start..end], out var width, out var height))
            {
                _width = width;
                _height = height;
                return;
            }

            index = end;
        }
    }

    private void ReleaseTransform()
    {
        if (_outputType is not null)
        {
            Marshal.ReleaseComObject(_outputType);
            _outputType = null;
        }

        if (_transform is not null)
        {
            Marshal.ReleaseComObject(_transform);
            _transform = null;
        }

        _codedWidth = 0;
        _codedHeight = 0;

        // The visible size is parsed from the SPS and must survive a transform rebuild. Clearing it here made the
        // decoder fall back to Media Foundation's default 1920×1080 for every stream whose SPS arrived before the
        // transform existed — i.e. every substream.
        _stride = 0;
        _planar = false;
        _stride = 0;
        _planar = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseTransform();
    }
}

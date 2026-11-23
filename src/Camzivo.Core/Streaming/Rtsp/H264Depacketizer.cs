namespace Camzivo.Core.Streaming.Rtsp;

/// <summary>
/// Reassembles H.264 RTP payloads (RFC 6184) into Annex-B access units.
///
/// Handles the three shapes cameras actually send: single NAL units, STAP-A aggregates, and FU-A fragments. An
/// access unit is emitted on the RTP marker bit, which is the only reliable boundary in packetized H.264.
/// Parameter sets from the SDP are prepended once so a decoder can start before the stream repeats them.
/// </summary>
public sealed class H264Depacketizer
{
    private readonly List<byte> _accessUnit = new(64 * 1024);
    private readonly byte[] _parameterSetsPrefix;
    private byte[]? _fragment;
    private int _fragmentLength;
    private bool _parameterSetsEmitted;
    private long _droppedFragments;

    public H264Depacketizer(IReadOnlyList<byte[]>? parameterSets = null)
    {
        var prefix = new List<byte>();
        if (parameterSets is not null)
        {
            foreach (var set in parameterSets)
            {
                if (set.Length > 0)
                {
                    prefix.AddRange(StartCode);
                    prefix.AddRange(set);
                }
            }
        }

        _parameterSetsPrefix = prefix.ToArray();
    }

    public static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    /// <summary>Access units discarded because a fragment sequence broke (packet loss), for telemetry.</summary>
    public long DroppedAccessUnits => _droppedFragments;

    /// <summary>
    /// Adds one payload. Returns true when <paramref name="accessUnit"/> holds a complete Annex-B access unit.
    /// </summary>
    public bool TryAdd(ReadOnlySpan<byte> payload, bool marker, out byte[]? accessUnit)
    {
        accessUnit = null;

        if (payload.Length == 0)
        {
            return false;
        }

        var nalType = payload[0] & 0x1F;

        switch (nalType)
        {
            case 1 or 5 or 6 or 7 or 8 or 9:
                AppendNal(payload);
                break;

            case 24:
                AppendAggregate(payload);
                break;

            case 28:
                if (!AppendFragment(payload))
                {
                    return false;
                }

                break;

            default:
                // 25-27 (MTAP) and 29-31 are not produced by cameras in practice; ignore rather than corrupt.
                return false;
        }

        if (!marker)
        {
            return false;
        }

        if (_accessUnit.Count == 0)
        {
            return false;
        }

        if (!_parameterSetsEmitted && _parameterSetsPrefix.Length > 0)
        {
            _accessUnit.InsertRange(0, _parameterSetsPrefix);
            _parameterSetsEmitted = true;
        }

        accessUnit = _accessUnit.ToArray();
        _accessUnit.Clear();
        return true;
    }

    private void AppendNal(ReadOnlySpan<byte> nal)
    {
        _accessUnit.AddRange(StartCode);
        _accessUnit.AddRange(nal);

        if ((nal[0] & 0x1F) == 7)
        {
            _parameterSetsEmitted = true;
        }
    }

    private void AppendAggregate(ReadOnlySpan<byte> payload)
    {
        var offset = 1;

        while (offset + 2 <= payload.Length)
        {
            var length = (payload[offset] << 8) | payload[offset + 1];
            offset += 2;

            if (length <= 0 || offset + length > payload.Length)
            {
                _droppedFragments++;
                return;
            }

            var nal = payload.Slice(offset, length);
            _accessUnit.AddRange(StartCode);
            _accessUnit.AddRange(nal);

            if ((nal[0] & 0x1F) == 7)
            {
                _parameterSetsEmitted = true;
            }

            offset += length;
        }
    }

    private bool AppendFragment(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
        {
            return false;
        }

        var header = payload[1];
        var start = (header & 0x80) != 0;
        var end = (header & 0x40) != 0;
        var nalType = header & 0x1F;
        var body = payload[2..];

        if (start)
        {
            _fragment = new byte[body.Length + 1];
            _fragment[0] = (byte)((payload[0] & 0xE0) | nalType);
            body.CopyTo(_fragment.AsSpan(1));
            _fragmentLength = _fragment.Length;
            return false;
        }

        if (_fragment is null)
        {
            // Continuation without a start: the first fragment was lost. Count it and wait for the next unit.
            _droppedFragments++;
            return false;
        }

        if (_fragmentLength + body.Length > _fragment.Length)
        {
            Array.Resize(ref _fragment, _fragmentLength + body.Length);
        }

        body.CopyTo(_fragment.AsSpan(_fragmentLength));
        _fragmentLength += body.Length;

        if (!end)
        {
            return false;
        }

        _accessUnit.AddRange(StartCode);
        _accessUnit.AddRange(_fragment.AsSpan(0, _fragmentLength));

        if ((nalType == 7))
        {
            _parameterSetsEmitted = true;
        }

        _fragment = null;
        _fragmentLength = 0;
        return true;
    }

    public void Reset()
    {
        _accessUnit.Clear();
        _fragment = null;
        _fragmentLength = 0;
        _parameterSetsEmitted = false;
    }
}

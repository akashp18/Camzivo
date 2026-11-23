using Camzivo.Core.Layout;
using Xunit;

namespace Camzivo.Core.Tests;

public class WallLayoutCodecTests
{
    private static readonly Guid A = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid B = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void RoundTripsSlotsInOrder()
    {
        Guid?[] slots = [A, null, B];

        Assert.True(WallLayoutCodec.TryDecode(WallLayoutCodec.Encode(slots), out var decoded));
        Assert.Equal(slots, decoded);
    }

    [Fact]
    public void EmptyAndAllEmptyWallsRoundTrip()
    {
        Assert.True(WallLayoutCodec.TryDecode(WallLayoutCodec.Encode([]), out var none));
        Assert.Empty(none);

        Assert.True(WallLayoutCodec.TryDecode(WallLayoutCodec.Encode([null, null]), out var empties));
        Assert.Equal([null, null], empties);
    }

    [Fact]
    public void UnknownVersionsAndGarbageAreRefused()
    {
        Assert.False(WallLayoutCodec.TryDecode(null, out _));
        Assert.False(WallLayoutCodec.TryDecode(string.Empty, out _));
        Assert.False(WallLayoutCodec.TryDecode("not json", out _));
        Assert.False(WallLayoutCodec.TryDecode("{\"version\":99,\"slots\":[]}", out _));
        Assert.False(WallLayoutCodec.TryDecode("{\"version\":1,\"slots\":{}}", out _));
    }

    [Fact]
    public void HandEditedEntriesDecayToEmptySlots()
    {
        const string text = "{\"version\":1,\"slots\":[\"AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA\",\"nonsense\",12,null]}";

        Assert.True(WallLayoutCodec.TryDecode(text, out var slots));
        Assert.Equal(4, slots.Length);
        Assert.Equal(A, slots[0]);
        Assert.Null(slots[1]);
        Assert.Null(slots[2]);
        Assert.Null(slots[3]);
    }

    [Fact]
    public void TheSameCameraIsNeverRestoredTwice()
    {
        var text = $"{{\"version\":1,\"slots\":[\"{A:D}\",\"{A:D}\"]}}";

        Assert.True(WallLayoutCodec.TryDecode(text, out var slots));
        Assert.Equal(A, slots[0]);
        Assert.Null(slots[1]);
    }

    [Fact]
    public void OversizedInputIsRefusedInBothDirections()
    {
        var slots = Enumerable.Range(0, WallLayoutCodec.MaxSlots + 1).Select(_ => (Guid?)null).ToList();

        Assert.Throws<ArgumentOutOfRangeException>(() => WallLayoutCodec.Encode(slots));

        var oversized = "{\"version\":1,\"slots\":[" +
                        string.Join(',', Enumerable.Repeat("null", WallLayoutCodec.MaxSlots + 1)) + "]}";
        Assert.False(WallLayoutCodec.TryDecode(oversized, out _));
    }
}

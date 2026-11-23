using Camzivo.Core.Devices;
using Camzivo.Core.Devices.Onvif;
using Xunit;

namespace Camzivo.Core.Tests.Devices;

/// <summary>
/// Turning a recorder's profile list into channels. Fixtures mirror how Dahua recorders name profiles; they prove the
/// rule, not device compatibility.
/// </summary>
public sealed class NvrChannelPlannerTests
{
    private static OnvifVideoProfile Profile(string token, string name, int width, int height, double rate = 25) =>
        new(token, name, "H264", width, height, rate);

    private static Uri UriFor(string token) => new($"rtsp://10.0.0.9:554/cam/realmonitor?channel={token}&subtype=0");

    /// <summary>The address a Dahua recorder actually hands back for a channel and stream type.</summary>
    private static Uri Dahua(int channel, int subtype) =>
        new($"rtsp://10.0.0.9:554/cam/realmonitor?channel={channel}&subtype={subtype}&unicast=true&proto=Onvif");

    [Fact]
    public void TakesChannelsFromTheRecordersOwnStreamAddresses()
    {
        // The shape this recorder really publishes: a profile per channel per stream type, no <Resolution>, and a
        // stream URL that states both the channel and the stream type. Reading the URL is exact; the earlier
        // name-based guess produced phantom channels whose sub stream pointed at channels 42-46.
        var profiles = new[]
        {
            new OnvifVideoProfile("MediaProfile00000", "MediaProfile_Channel1", "H264", 0, 0, null, false, "s1", "e1"),
            new OnvifVideoProfile("MediaProfile00001", "MediaProfile_Channel1_SubStream1", "H264", 0, 0, null, false, "s1", "e2"),
            new OnvifVideoProfile("MediaProfile00100", "MediaProfile_Channel2", "H264", 0, 0, null, false, "s2", "e3"),
            new OnvifVideoProfile("MediaProfile00101", "MediaProfile_Channel2_SubStream1", "H264", 0, 0, null, false, "s2", "e4"),
            new OnvifVideoProfile("MediaProfile01102", "MediaProfile_Channel16_SubStream2", "H264", 0, 0, null, false, "s16", "e5"),
        };

        Uri Url(string token) => token switch
        {
            "MediaProfile00000" => Dahua(1, 0),
            "MediaProfile00001" => Dahua(1, 1),
            "MediaProfile00100" => Dahua(2, 0),
            "MediaProfile00101" => Dahua(2, 1),
            _ => Dahua(16, 2),
        };

        var channels = NvrChannelPlanner.Plan(profiles, Url);

        Assert.Equal([1, 2, 16], channels.Select(channel => channel.Channel));
        Assert.Contains("channel=1&subtype=0", channels[0].MainUri, StringComparison.Ordinal);
        Assert.Contains("channel=1&subtype=1", channels[0].SubUri, StringComparison.Ordinal);
        Assert.Contains("channel=2&subtype=0", channels[1].MainUri, StringComparison.Ordinal);
        Assert.Contains("channel=16&subtype=2", channels[2].MainUri, StringComparison.Ordinal);
        Assert.Null(channels[2].SubUri);
    }

    [Fact]
    public void PairsMainAndSubProfilesOfTheSameChannel()
    {
        // The URLs are the ones this recorder would return; the profile names are what a Dahua NVR shows.
        var profiles = new[]
        {
            Profile("101", "Channel 1 Main", 1920, 1080),
            Profile("102", "Channel 1 Sub", 640, 360),
            Profile("201", "Channel 2 Main", 1280, 720),
            Profile("202", "Channel 2 Sub", 352, 288),
        };

        var channels = NvrChannelPlanner.Plan(profiles, token => token switch
        {
            "101" => Dahua(1, 0),
            "102" => Dahua(1, 1),
            "201" => Dahua(2, 0),
            _ => Dahua(2, 1),
        });

        Assert.Equal(2, channels.Count);
        Assert.All(channels, channel => Assert.NotNull(channel.SubUri));
        Assert.Equal(Dahua(1, 0).AbsoluteUri, channels[0].MainUri);
        Assert.Equal(Dahua(1, 1).AbsoluteUri, channels[0].SubUri);
        Assert.Equal("Channel 2", channels[1].Name);
    }

    [Fact]
    public void KeepsEveryChannelWhenTheRecorderOmitsTheResolution()
    {
        // What a Dahua NVR actually returns: one profile per channel, an encoder token, no <Resolution>.
        var profiles = new[]
        {
            new OnvifVideoProfile("000", "Channel1 Main", "H264", 0, 0, null, false, "src1", "enc000"),
            new OnvifVideoProfile("001", "Channel2 Main", "H264", 0, 0, null, false, "src2", "enc001"),
            new OnvifVideoProfile("002", "Channel3 Main", "H264", 0, 0, null, false, "src3", "enc002"),
            new OnvifVideoProfile("audio", "Audio", null, 0, 0, null, true, null, null),
        };

        var channels = NvrChannelPlanner.Plan(profiles, token => Dahua(int.Parse(token, System.Globalization.CultureInfo.InvariantCulture) + 1, 0));

        Assert.Equal([1, 2, 3], channels.Select(channel => channel.Channel));
        Assert.All(channels, channel => Assert.False(string.IsNullOrWhiteSpace(channel.MainUri)));
    }

    [Fact]
    public void ReadsChannelNumbersFromTokensWhenNamesAreUnhelpful()
    {
        var profiles = new[]
        {
            Profile("Profile_003", "Profile_003", 1920, 1080),
            Profile("Profile_004", "Profile_004", 1920, 1080),
        };

        var channels = NvrChannelPlanner.Plan(profiles, token => UriFor(token));

        Assert.Equal([3, 4], channels.Select(channel => channel.Channel));
    }

    [Theory]
    [InlineData("Channel 7 Main", 7)]
    [InlineData("CH08", 8)]
    [InlineData("Camera 12", 12)]
    [InlineData("Cam-3-sub", 3)]
    [InlineData("Profile_016", 16)]
    [InlineData("subStream", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReadsChannelNumbersOnlyWhenTheyArePresent(string? text, int? expected)
    {
        Assert.Equal(expected, NvrChannelPlanner.ReadChannelNumber(text));
    }

    [Fact]
    public void UnnumberedProfilesAreNumberedInDeviceOrderAfterExplicitOnes()
    {
        var profiles = new[]
        {
            Profile("alpha", "Main", 1920, 1080),
            Profile("beta", "Sub", 640, 360),
            Profile("gamma", "Channel 5", 1280, 720),
        };

        var channels = NvrChannelPlanner.Plan(profiles, token => UriFor(token));

        Assert.Equal(3, channels.Count);
        Assert.Equal([5, 6, 7], channels.Select(channel => channel.Channel));
    }

    [Fact]
    public void PairsUnnumberedAlternatingMainAndSubProfiles()
    {
        var profiles = new[]
        {
            Profile("main-1", "MainStream", 1920, 1080),
            Profile("sub-1", "SubStream", 640, 360),
            Profile("main-2", "MainStream", 1280, 720),
            Profile("sub-2", "SubStream", 352, 288),
        };

        var channels = NvrChannelPlanner.Plan(profiles, token => UriFor(token));

        Assert.Equal(2, channels.Count);
        Assert.Equal([1, 2], channels.Select(channel => channel.Channel));
        Assert.All(channels, channel => Assert.NotNull(channel.SubUri));
    }

    [Fact]
    public void AudioOnlyProfilesAreNotChannels()
    {
        var profiles = new[]
        {
            Profile("audio", "audioOnly", 0, 0),
            Profile("101", "Channel 1 Main", 1920, 1080),
        };

        var channels = NvrChannelPlanner.Plan(profiles, _ => Dahua(1, 0));

        var channel = Assert.Single(channels);
        Assert.Equal(1, channel.Channel);
    }

    [Fact]
    public void AProfileWithoutAStreamAddressStillBecomesAChannel()
    {
        var channels = NvrChannelPlanner.Plan(
            [Profile("101", "Channel 1 Main", 1920, 1080)],
            _ => null);

        Assert.Null(Assert.Single(channels).MainUri);
    }

    [Fact]
    public void NoUsableProfilesMeansNoChannels()
    {
        Assert.Empty(NvrChannelPlanner.Plan([], _ => null));
    }
}

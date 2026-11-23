using System.Globalization;
using Camzivo.Core.Streaming.Replay;
using Xunit;

namespace Camzivo.Core.Tests.Streaming;

public sealed class PlaybackRateTests
{
    [Fact]
    public void Supported_rates_are_the_six_advertised_speeds()
    {
        Assert.Equal([1d, 2d, 4d, 6d, 8d, 16d], PlaybackRates.Supported);
        Assert.All(PlaybackRates.Supported, rate => Assert.True(PlaybackRates.IsSupported(rate)));
        Assert.False(PlaybackRates.IsSupported(3));
        Assert.False(PlaybackRates.IsSupported(0.5));
    }

    [Fact]
    public void Normalize_rounds_to_a_supported_speed_and_defaults_to_normal()
    {
        Assert.Equal(4, PlaybackRates.Normalize(3.9));
        Assert.Equal(16, PlaybackRates.Normalize(15));
        Assert.Equal(1, PlaybackRates.Normalize(0));
        Assert.Equal(1, PlaybackRates.Normalize(double.NaN));
    }

    [Fact]
    public void Scale_header_uses_invariant_decimal_separator()
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            var options = new ReplayPlaybackOptions(DateTimeOffset.UnixEpoch, 16);
            Assert.Equal("Scale: 16.000", options.ScaleHeader);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Meter_measures_source_rate_over_wall_clock()
    {
        var meter = new PlaybackRateMeter();

        Assert.Equal(0, meter.Sample(0, 0));
        Assert.Equal(0, meter.Sample(0.6, 0.6));
        Assert.Equal(1, meter.Sample(1, 1), 3);

        // Re-anchored after the first window: two source seconds in one wall second is 2×.
        Assert.Equal(2, meter.Sample(3, 2), 3);
    }

    [Fact]
    public void Meter_reports_a_recorder_that_ignores_scale_as_normal_speed()
    {
        var meter = new PlaybackRateMeter();
        meter.Sample(0, 0);

        var observed = meter.Sample(4, 4);

        Assert.Equal(1, observed, 3);
        Assert.False(PlaybackRates.IsDelivered(16, observed));
        Assert.True(PlaybackRates.IsDelivered(1, observed));
    }

    [Fact]
    public void Meter_rebases_instead_of_reporting_negative_speed_after_timestamp_reset()
    {
        var meter = new PlaybackRateMeter();
        meter.Sample(0, 0);
        Assert.Equal(10, meter.Sample(10, 1), 3);

        Assert.Equal(0, meter.Sample(1, 2));
        Assert.Equal(1, meter.Sample(2, 3), 3);
    }

    [Theory]
    [InlineData(1, 0.80, false)]
    [InlineData(1, 0.95, true)]
    [InlineData(4, 3.1, true)]
    [InlineData(4, 1.0, false)]
    [InlineData(16, 15.8, true)]
    [InlineData(16, 1.0, false)]
    public void Delivered_compares_measured_speed_with_a_documented_tolerance(double requested, double observed, bool expected)
    {
        Assert.Equal(expected, PlaybackRates.IsDelivered(requested, observed));
    }
}

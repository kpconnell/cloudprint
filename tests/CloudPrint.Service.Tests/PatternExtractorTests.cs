using CloudPrint.Service.Devices.Parsing;

namespace CloudPrint.Service.Tests;

public class PatternExtractorTests
{
    [Fact]
    public void No_pattern_forwards_raw_only()
    {
        var extractor = new PatternExtractor(null);

        var reading = extractor.TryParse("anything here");

        Assert.NotNull(reading);
        Assert.Null(reading!.Value);
        Assert.True(reading.Stable);
        Assert.Equal("anything here", reading.Raw);
    }

    [Fact]
    public void Pattern_extracts_value_and_unit()
    {
        var extractor = new PatternExtractor(@"^(?<value>\d+(?:\.\d+)?)\s*(?<unit>[A-Za-z]+)$");

        var reading = extractor.TryParse("42.5 kg");

        Assert.NotNull(reading);
        Assert.Equal(42.5m, reading!.Value);
        Assert.Equal("kg", reading.Unit);
        Assert.True(reading.Stable);
    }

    [Fact]
    public void Pattern_no_match_returns_null()
    {
        var extractor = new PatternExtractor(@"^\d+$");

        Assert.Null(extractor.TryParse("abc"));
    }

    [Fact]
    public void Stable_group_controls_stability()
    {
        var extractor = new PatternExtractor(@"^(?<stable>ST|US),(?<value>\d+)$");

        Assert.True(extractor.TryParse("ST,5")!.Stable);
        Assert.False(extractor.TryParse("US,5")!.Stable);
    }

    [Fact]
    public void Empty_returns_null()
    {
        Assert.Null(new PatternExtractor(null).TryParse(""));
    }
}

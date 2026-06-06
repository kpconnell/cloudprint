using System.Globalization;
using CloudPrint.Service.Devices.Parsing;

namespace CloudPrint.Service.Tests;

public class SerialScaleParserTests
{
    private readonly SerialScaleParser _parser = new();

    [Theory]
    [InlineData("ST,+00456.89  g", "456.89", "g", true)]
    [InlineData("US,+00012.00 g", "12.00", "g", false)]
    [InlineData("ST,-1.5 kg", "-1.5", "kg", true)]
    public void Parses_continuous_format(string line, string expected, string unit, bool stable)
    {
        var reading = _parser.TryParse(line);

        Assert.NotNull(reading);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), reading!.Value);
        Assert.Equal(unit, reading.Unit);
        Assert.Equal(stable, reading.Stable);
    }

    [Fact]
    public void Parses_mt_sics_stable()
    {
        var reading = _parser.TryParse("S S 100.00 g");

        Assert.NotNull(reading);
        Assert.Equal(100.00m, reading!.Value);
        Assert.True(reading.Stable);
        Assert.Equal("g", reading.Unit);
    }

    [Fact]
    public void Parses_mt_sics_dynamic_as_unstable()
    {
        var reading = _parser.TryParse("S D 5.0 g");

        Assert.NotNull(reading);
        Assert.False(reading!.Stable);
    }

    [Fact]
    public void Overload_line_sets_overload_status()
    {
        var reading = _parser.TryParse("OL,+99999.9 g");

        Assert.NotNull(reading);
        Assert.False(reading!.Stable);
        Assert.Equal("overload", reading.Status);
    }

    [Fact]
    public void Plain_number_defaults_to_stable_ok()
    {
        var reading = _parser.TryParse("+00456.89 g");

        Assert.NotNull(reading);
        Assert.Equal(456.89m, reading!.Value);
        Assert.True(reading.Stable);
        Assert.Equal("ok", reading.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("ST,no number here")]
    public void Returns_null_for_unparseable(string line)
    {
        Assert.Null(_parser.TryParse(line));
    }
}

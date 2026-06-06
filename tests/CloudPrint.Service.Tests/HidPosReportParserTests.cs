using CloudPrint.Service.Devices.Parsing;

namespace CloudPrint.Service.Tests;

public class HidPosReportParserTests
{
    private readonly HidPosReportParser _parser = new();

    // Default HID-POS layout: [reportId, status, unit, exponent, weightLo, weightHi]
    private static byte[] Report(byte status, byte unit, byte exponent, ushort weight) =>
        new byte[] { 0x03, status, unit, exponent, (byte)(weight & 0xFF), (byte)(weight >> 8) };

    [Fact]
    public void Decodes_stable_grams_with_negative_exponent()
    {
        // 20 * 10^-1 = 2.0 g, status 4 = weight stable
        var reading = _parser.TryParse(Report(0x04, 0x02, 0xFF, 0x0014), HidFieldMap.Default);

        Assert.NotNull(reading);
        Assert.Equal(2.0m, reading!.Value);
        Assert.Equal("g", reading.Unit);
        Assert.True(reading.Stable);
        Assert.Equal("ok", reading.Status);
    }

    [Fact]
    public void Decodes_positive_exponent_pounds()
    {
        var reading = _parser.TryParse(Report(0x04, 0x0C, 0x01, 5), HidFieldMap.Default); // 5 * 10 = 50 lb

        Assert.Equal(50m, reading!.Value);
        Assert.Equal("lb", reading.Unit);
    }

    [Theory]
    [InlineData(0x06, false, "overload")]
    [InlineData(0x02, true, "zero")]
    [InlineData(0x03, false, "motion")]
    [InlineData(0x05, false, "underload")]
    public void Maps_status_byte(byte status, bool stable, string statusText)
    {
        var reading = _parser.TryParse(Report(status, 0x02, 0x00, 0), HidFieldMap.Default);

        Assert.NotNull(reading);
        Assert.Equal(stable, reading!.Stable);
        Assert.Equal(statusText, reading.Status);
    }

    [Fact]
    public void Maps_ounce_unit()
    {
        var reading = _parser.TryParse(Report(0x04, 0x0B, 0x00, 10), HidFieldMap.Default);

        Assert.Equal("oz", reading!.Unit);
        Assert.Equal(10m, reading.Value);
    }

    [Fact]
    public void Short_report_returns_null()
    {
        Assert.Null(_parser.TryParse(new byte[] { 0x03 }, HidFieldMap.Default));
    }

    [Fact]
    public void Empty_report_returns_null()
    {
        Assert.Null(_parser.TryParse(Array.Empty<byte>(), HidFieldMap.Default));
    }

    [Fact]
    public void Report_id_mismatch_returns_null()
    {
        var map = HidFieldMap.Default with { ReportId = 0x05 };

        Assert.Null(_parser.TryParse(Report(0x04, 0x02, 0x00, 5), map)); // report[0] == 0x03
    }

    [Fact]
    public void Override_offsets_handle_device_without_report_id()
    {
        // status@0, unit@1, exponent@2, weight@3 (no leading report id)
        var map = HidFieldMap.Default with
        {
            StatusOffset = 0,
            UnitOffset = 1,
            ExponentOffset = 2,
            WeightOffset = 3,
            WeightSize = 2
        };
        var report = new byte[] { 0x04, 0x02, 0x00, 0x07, 0x00 };

        var reading = _parser.TryParse(report, map);

        Assert.Equal(7m, reading!.Value);
        Assert.Equal("g", reading.Unit);
        Assert.True(reading.Stable);
    }
}

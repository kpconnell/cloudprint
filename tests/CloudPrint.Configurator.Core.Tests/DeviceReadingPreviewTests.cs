using CloudPrint.Configurator.Core.Devices;

namespace CloudPrint.Configurator.Core.Tests;

public class DeviceReadingPreviewTests
{
    [Fact]
    public void Parses_stable_weight_reading()
    {
        const string line = "{\"value\":12.34,\"unit\":\"lb\",\"stable\":true,\"status\":\"ok\",\"raw\":\"S S 12.34 lb\"}";

        Assert.True(DeviceReadingPreview.TryParse(line, out var r));
        Assert.Equal(12.34m, r.Value);
        Assert.Equal("lb", r.Unit);
        Assert.True(r.Stable);
        Assert.Equal("ok", r.Status);
        Assert.Contains("12.34 lb", r.Describe());
        Assert.Contains("stable", r.Describe());
    }

    [Fact]
    public void Handles_null_value_and_motion_status()
    {
        const string line = "{\"value\":null,\"status\":\"motion\",\"raw\":\"S D 0.0\"}";

        Assert.True(DeviceReadingPreview.TryParse(line, out var r));
        Assert.Null(r.Value);
        Assert.False(r.Stable);
        Assert.Equal("motion", r.Status);
    }

    [Fact]
    public void Defaults_status_to_ok_when_absent()
    {
        Assert.True(DeviceReadingPreview.TryParse("{\"value\":1}", out var r));
        Assert.Equal("ok", r.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void Rejects_blank_or_invalid_lines(string line)
    {
        Assert.False(DeviceReadingPreview.TryParse(line, out _));
    }
}

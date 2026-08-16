using CloudPrint.Service.Devices.Framing;

namespace CloudPrint.Service.Tests;

public class ControlEscapesTests
{
    [Theory]
    [InlineData("W", "W")]
    [InlineData(@"\x02M\x03", "\x02M\x03")]
    [InlineData("<STX>M<ETX>", "\x02M\x03")]
    [InlineData("<stx>T<etx><CR><LF>", "\x02T\x03\r\n")]
    [InlineData(@"\r\n", "\r\n")]
    [InlineData(@"\u0005", "\x05")]
    [InlineData("<ENQ>", "\x05")]
    [InlineData(@"\e", "\x1B")]
    [InlineData(@"C:\path", @"C:\path")]              // unknown escape kept literally
    [InlineData("a < b > c", "a < b > c")]           // angle brackets that aren't mnemonics survive
    [InlineData(@"\\x02", @"\x02")]                   // escaped backslash
    public void Unescapes(string input, string expected) => Assert.Equal(expected, ControlEscapes.Unescape(input));

    [Fact]
    public void Describe_renders_control_chars_as_mnemonics()
    {
        Assert.Equal("<STX>M<ETX><CR><LF>", ControlEscapes.Describe("\x02M\x03\r\n"));
        Assert.Equal("W", ControlEscapes.Describe("W"));
        Assert.Equal("<ENQ>", ControlEscapes.Describe("\x05"));
    }
}

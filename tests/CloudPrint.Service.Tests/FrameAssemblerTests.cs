using System.Text;
using CloudPrint.Service.Devices.Framing;

namespace CloudPrint.Service.Tests;

public class FrameAssemblerTests
{
    private static List<string> Drain(FrameAssembler a)
    {
        var frames = new List<string>();
        while (a.TryDequeue(out var f)) frames.Add(Encoding.Latin1.GetString(f));
        return frames;
    }

    private static FramingOptions Opts(FrameMode mode, string term = "\r\n", string start = "", string end = "", int idleMs = 150, int max = 4096) =>
        new(mode, Encoding.Latin1.GetBytes(term), Encoding.Latin1.GetBytes(start), Encoding.Latin1.GetBytes(end), TimeSpan.FromMilliseconds(idleMs), max);

    [Fact]
    public void Line_mode_splits_on_terminator_and_strips_it()
    {
        var a = new FrameAssembler(Opts(FrameMode.Line));
        a.Push("ST,+00012.34 kg\r\nUS,+00"u8);
        Assert.Equal(new[] { "ST,+00012.34 kg" }, Drain(a));
        a.Push("012.35 kg\r\n"u8);
        Assert.Equal(new[] { "US,+00012.35 kg" }, Drain(a));
        a.Flush(); // line mode keeps partial data on idle
        Assert.Empty(Drain(a));
    }

    [Fact]
    public void Line_mode_cr_terminator_handles_toledo_reply()
    {
        var a = new FrameAssembler(Opts(FrameMode.Line, term: "\r"));
        a.Push("\u000210.55\r\u0002?a\r"u8);
        Assert.Equal(new[] { "\u000210.55", "\u0002?a" }, Drain(a));
    }

    [Fact]
    public void Delimited_mode_keeps_delimiters_and_discards_bytes_between_frames()
    {
        var a = new FrameAssembler(Opts(FrameMode.Delimited, start: "\x02", end: "\x03"));
        // Cubiscan ACK then a NAK that ends <ETX><CR> without LF, then junk, then another ACK split across pushes.
        a.Push("\x02TA00\x03\r\n\x02MN\x03\r"u8);
        Assert.Equal(new[] { "\x02TA00\x03", "\x02MN\x03" }, Drain(a));
        a.Push("garbage\x02MAH000000,L009.8,W007.2,H003.5,E,K0"u8);
        Assert.Empty(Drain(a));
        a.Push("00.00,D000.00,E,F0138,D\x03\r\n"u8);
        Assert.Equal(new[] { "\x02MAH000000,L009.8,W007.2,H003.5,E,K000.00,D000.00,E,F0138,D\x03" }, Drain(a));
    }

    [Fact]
    public void Delimited_mode_without_start_uses_end_only()
    {
        var a = new FrameAssembler(Opts(FrameMode.Delimited, end: "\x03"));
        a.Push("abc\u0003def\u0003"u8);
        Assert.Equal(new[] { "abc\u0003", "def\u0003" }, Drain(a));
    }

    [Fact]
    public void Idle_mode_emits_on_flush_only()
    {
        var a = new FrameAssembler(Opts(FrameMode.Idle));
        a.Push("\x02,    1234     0\r"u8);
        Assert.Empty(Drain(a));
        a.Push(new byte[] { 0x9A }); // trailing checksum byte with no terminator knowledge
        a.Flush();
        Assert.Equal(new[] { "\x02,    1234     0\r\x9A" }, Drain(a));
        a.Flush();
        Assert.Empty(Drain(a));
    }

    [Fact]
    public void Max_frame_bytes_bounds_runaway_frames()
    {
        var a = new FrameAssembler(Opts(FrameMode.Line, max: 8));
        a.Push("0123456789ABCDEF"u8);
        Assert.Equal(new[] { "01234567", "89ABCDEF" }, Drain(a));
    }

    [Fact]
    public void Reset_discards_partial_state()
    {
        var a = new FrameAssembler(Opts(FrameMode.Delimited, start: "\x02", end: "\x03"));
        a.Push("\x02partial"u8);
        a.Reset();
        a.Push("\x02ok\x03"u8);
        Assert.Equal(new[] { "\x02ok\x03" }, Drain(a));
    }
}

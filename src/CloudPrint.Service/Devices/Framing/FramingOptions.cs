namespace CloudPrint.Service.Devices.Framing;

public enum FrameMode
{
    /// <summary>Frames end with a terminator sequence (CRLF, CR, LF, or a literal); the terminator is stripped.</summary>
    Line,
    /// <summary>Frames run from an optional start sequence (e.g. STX) to an end sequence (e.g. ETX); delimiters are kept.
    /// Bytes outside start..end are discarded when a start sequence is configured.</summary>
    Delimited,
    /// <summary>No delimiter knowledge: whatever arrives is emitted as one frame once the line goes quiet for
    /// <see cref="FramingOptions.IdleGap"/> (or the frame hits <see cref="FramingOptions.MaxFrameBytes"/>). The discovery mode.</summary>
    Idle
}

/// <summary>How a byte stream is cut into frames. Pure data; resolved once per device from config.</summary>
public sealed record FramingOptions(
    FrameMode Mode,
    byte[] Terminator,
    byte[] Start,
    byte[] End,
    TimeSpan IdleGap,
    int MaxFrameBytes)
{
    public static FramingOptions LineCrLf { get; } = new(FrameMode.Line, "\r\n"u8.ToArray(), Array.Empty<byte>(), Array.Empty<byte>(), TimeSpan.FromMilliseconds(150), 4096);
}

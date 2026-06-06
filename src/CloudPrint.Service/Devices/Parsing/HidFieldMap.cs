namespace CloudPrint.Service.Devices.Parsing;

/// <summary>
/// Byte layout of a HID POS scale input report. The default is the de-facto HID-POS data report
/// (report id at byte 0, then status, unit, signed scaling exponent, little-endian weight) used by
/// virtually all HID scales. Per-device overrides handle non-conformant devices, so the parser is
/// configurable rather than hardcoded.
/// </summary>
public record HidFieldMap
{
    /// <summary>Expected report id (byte 0), or null to not check.</summary>
    public byte? ReportId { get; init; }

    public int StatusOffset { get; init; } = 1;
    public int UnitOffset { get; init; } = 2;
    public int ExponentOffset { get; init; } = 3;
    public int WeightOffset { get; init; } = 4;
    public int WeightSize { get; init; } = 2;

    public static HidFieldMap Default { get; } = new();
}

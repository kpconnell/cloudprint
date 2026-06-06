namespace CloudPrint.Service.Devices.Parsing;

/// <summary>
/// Decodes a HID POS scale input report (HID usage page 0x8D) into a <see cref="DeviceReading"/>
/// using a <see cref="HidFieldMap"/>. Pure and hardware-free: the field map (default, descriptor-
/// derived, or per-device override) is supplied by the reader. Weight = rawWeight × 10^exponent,
/// with status/unit decoded from the HID-POS enumerations.
/// </summary>
public class HidPosReportParser
{
    public DeviceReading? TryParse(byte[] report, HidFieldMap map)
    {
        if (report is null || report.Length == 0)
            return null;

        var highestOffset = Math.Max(
            Math.Max(map.StatusOffset, map.UnitOffset),
            Math.Max(map.ExponentOffset, map.WeightOffset + map.WeightSize - 1));

        if (highestOffset >= report.Length)
            return null;

        if (map.ReportId is { } expectedId && report[0] != expectedId)
            return null;

        var status = report[map.StatusOffset];
        var unitCode = report[map.UnitOffset];
        var exponent = unchecked((sbyte)report[map.ExponentOffset]);

        long raw = 0;
        for (var i = 0; i < map.WeightSize; i++)
            raw |= (long)report[map.WeightOffset + i] << (8 * i); // little-endian

        var value = (decimal)raw * Pow10(exponent);
        var (stable, statusText) = MapStatus(status);

        return new DeviceReading
        {
            Value = value,
            Unit = MapUnit(unitCode),
            Stable = stable,
            Status = statusText,
            Raw = Convert.ToHexString(report)
        };
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        if (exponent >= 0)
            for (var i = 0; i < exponent; i++) result *= 10m;
        else
            for (var i = 0; i < -exponent; i++) result /= 10m;
        return result;
    }

    // HID POS "Scale Status" enumeration (usage page 0x8D).
    private static (bool stable, string status) MapStatus(byte status) => status switch
    {
        1 => (false, "error"),      // Fault
        2 => (true, "zero"),        // Stable at center of zero
        3 => (false, "motion"),     // In motion
        4 => (true, "ok"),          // Weight stable
        5 => (false, "underload"),  // Under zero
        6 => (false, "overload"),   // Over weight limit
        7 => (false, "error"),      // Requires calibration
        8 => (false, "error"),      // Requires re-zeroing
        _ => (false, "error")
    };

    // HID POS "Weight Unit" enumeration (usage page 0x8D), mapped to short unit codes.
    private static string MapUnit(byte unit) => unit switch
    {
        1 => "mg",
        2 => "g",
        3 => "kg",
        4 => "ct",
        5 => "tael",
        6 => "gr",
        7 => "dwt",
        8 => "t",
        9 => "ton",
        10 => "ozt",
        11 => "oz",
        12 => "lb",
        _ => $"unit{unit}"
    };
}

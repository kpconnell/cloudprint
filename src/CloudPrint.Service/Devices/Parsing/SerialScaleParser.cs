using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudPrint.Service.Devices.Parsing;

/// <summary>
/// Tolerant parser for common ASCII scale protocols:
///   - A&amp;D / Ohaus continuous:  "ST,+00456.89  g"   (ST=stable, US/SD/QT=unstable, OL=overload)
///   - Mettler Toledo MT-SICS:     "S S 100.00 g"      (second token S=stable, D=dynamic)
///   - Plain numeric:              "+00456.89 g"        (no status prefix — treated as stable)
/// Extracts sign/number/unit and infers a stability + status from any leading marker.
/// </summary>
public partial class SerialScaleParser : ISerialLineParser
{
    [GeneratedRegex(@"(?<sign>[-+])?\s*(?<num>\d+(?:\.\d+)?)\s*(?<unit>kg|lb|oz|mg|ct|gr|g|t)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex WeightRegex();

    [GeneratedRegex(@"^S\s+S\b", RegexOptions.IgnoreCase)]
    private static partial Regex MtSicsStable();

    [GeneratedRegex(@"^S\s+D\b", RegexOptions.IgnoreCase)]
    private static partial Regex MtSicsDynamic();

    public DeviceReading? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();
        var match = WeightRegex().Match(trimmed);
        if (!match.Success)
            return null;

        if (!decimal.TryParse(match.Groups["num"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
            return null;

        var value = match.Groups["sign"].Value == "-" ? -num : num;
        var (stable, status) = DetectStatus(trimmed);

        return new DeviceReading
        {
            Value = value,
            Unit = match.Groups["unit"].Value.ToLowerInvariant(),
            Stable = stable,
            Status = status,
            Raw = trimmed
        };
    }

    private static (bool stable, string status) DetectStatus(string s)
    {
        var u = s.ToUpperInvariant();

        if (u.StartsWith("OL") || u.Contains("OVER"))
            return (false, "overload");
        if (u.StartsWith("UL") || u.Contains("UNDER"))
            return (false, "underload");
        if (u.StartsWith("ST") || u.Contains("STABLE") || MtSicsStable().IsMatch(u))
            return (true, "ok");
        if (u.StartsWith("US") || u.StartsWith("SD") || u.StartsWith("QT") || u.Contains("UNSTABLE") || MtSicsDynamic().IsMatch(u))
            return (false, "motion");

        // No explicit marker — many simple scales just print the number. Treat as a stable reading.
        return (true, "ok");
    }
}

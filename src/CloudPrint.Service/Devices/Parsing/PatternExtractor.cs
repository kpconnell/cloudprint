using System.Globalization;
using System.Text.RegularExpressions;

namespace CloudPrint.Service.Devices.Parsing;

/// <summary>
/// Generic passthrough parser for arbitrary serial devices. With a configured regex it extracts
/// named groups <c>value</c>, <c>unit</c>, and <c>stable</c>; without one it forwards the raw line
/// (value=null, stable=true). Lets a new device work via config alone.
/// </summary>
public class PatternExtractor : ISerialLineParser
{
    private readonly Regex? _regex;

    public PatternExtractor(string? pattern) =>
        _regex = string.IsNullOrWhiteSpace(pattern) ? null : new Regex(pattern, RegexOptions.Compiled);

    public DeviceReading? TryParse(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        var raw = line.Trim();
        if (raw.Length == 0)
            return null;

        if (_regex is null)
            return new DeviceReading { Raw = raw, Status = "ok", Stable = true };

        var match = _regex.Match(raw);
        if (!match.Success)
            return null;

        var reading = new DeviceReading { Raw = raw, Status = "ok" };

        var valueGroup = match.Groups["value"];
        if (valueGroup.Success &&
            decimal.TryParse(valueGroup.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            reading.Value = value;

        var unitGroup = match.Groups["unit"];
        if (unitGroup.Success)
            reading.Unit = unitGroup.Value;

        var stableGroup = match.Groups["stable"];
        reading.Stable = !stableGroup.Success || ParseStable(stableGroup.Value);

        return reading;
    }

    private static bool ParseStable(string value) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "stable" or "st" or "s" => true,
        _ => false
    };
}

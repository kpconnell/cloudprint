using System.Text.Json;

namespace CloudPrint.Configurator.Core.Devices;

/// <summary>
/// One live reading parsed from the service's <c>preview-device</c> output (one JSON line per reading).
/// Only the fields the configurator displays are kept.
/// </summary>
public sealed record DeviceReadingPreview(decimal? Value, string? Unit, bool Stable, string Status, string? Raw)
{
    private static readonly DeviceReadingPreview Empty = new(null, null, false, "ok", null);

    /// <summary>Parses one JSON line emitted by <c>preview-device</c>. False for blank/non-object/invalid lines.</summary>
    public static bool TryParse(string jsonLine, out DeviceReadingPreview reading)
    {
        reading = Empty;
        if (string.IsNullOrWhiteSpace(jsonLine))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            reading = new DeviceReadingPreview(
                Value: TryDecimal(root, "value"),
                Unit: TryString(root, "unit"),
                Stable: root.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True,
                Status: TryString(root, "status") ?? "ok",
                Raw: TryString(root, "raw"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A short human-friendly description for the live "reading now: …" label.</summary>
    public string Describe()
    {
        if (Value is { } v)
            return $"{v} {Unit}".Trim() + (Stable ? "  (stable)" : "  (motion)");
        return string.IsNullOrWhiteSpace(Raw) ? Status : Raw!;
    }

    private static decimal? TryDecimal(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)
            ? d
            : null;

    private static string? TryString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}

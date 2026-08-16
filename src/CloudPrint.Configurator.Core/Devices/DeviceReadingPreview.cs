using System.Text.Json;

namespace CloudPrint.Configurator.Core.Devices;

/// <summary>
/// One live reading parsed from the service's <c>preview-device</c> output (one JSON line per reading).
/// Only the fields the configurator displays are kept. Lifecycle events (status connected/disconnected/
/// stale/...) carry discovery metadata instead of a value.
/// </summary>
public sealed record DeviceReadingPreview(decimal? Value, string? Unit, bool Stable, string Status, string? Raw,
    string? RawHex = null, IReadOnlyDictionary<string, string>? Metadata = null)
{
    private static readonly DeviceReadingPreview Empty = new(null, null, false, "ok", null);

    public bool IsEvent => Status is "connected" or "disconnected" or "stale" or "command-sent" or "command-failed";

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

            Dictionary<string, string>? metadata = null;
            if (root.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                metadata = new Dictionary<string, string>();
                foreach (var kv in m.EnumerateObject())
                    if (kv.Value.ValueKind == JsonValueKind.String)
                        metadata[kv.Name] = kv.Value.GetString() ?? string.Empty;
            }

            reading = new DeviceReadingPreview(
                Value: TryDecimal(root, "value"),
                Unit: TryString(root, "unit"),
                Stable: root.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True,
                Status: TryString(root, "status") ?? "ok",
                Raw: TryString(root, "raw"),
                RawHex: TryString(root, "rawHex"),
                Metadata: metadata);
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
        if (IsEvent)
        {
            var m = Metadata;
            string? detail = null;
            if (m is not null)
            {
                var bits = new List<string>();
                if (m.TryGetValue("product", out var product) && !string.IsNullOrWhiteSpace(product)) bits.Add(product);
                if (m.TryGetValue("friendlyName", out var friendly) && !string.IsNullOrWhiteSpace(friendly)) bits.Add(friendly);
                if (m.TryGetValue("endpoint", out var endpoint) && !string.IsNullOrWhiteSpace(endpoint)) bits.Add(endpoint);
                if (m.TryGetValue("isScale", out var isScale) && isScale == "true") bits.Add("[HID scale]");
                if (m.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error)) bits.Add(error);
                if (bits.Count > 0) detail = string.Join(" · ", bits);
            }
            return detail is null ? Status : $"{Status}: {detail}";
        }

        if (Value is { } v)
            return $"{v} {Unit}".Trim() + (Stable ? "  (stable)" : "  (motion)");
        if (!string.IsNullOrWhiteSpace(Raw))
            return Raw! + (string.IsNullOrEmpty(RawHex) ? "" : $"  [{RawHex}]");
        return string.IsNullOrEmpty(RawHex) ? Status : $"[{RawHex}]";
    }

    private static decimal? TryDecimal(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)
            ? d
            : null;

    private static string? TryString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}

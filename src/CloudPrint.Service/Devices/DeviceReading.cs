using System.Text.Json.Serialization;

namespace CloudPrint.Service.Devices;

/// <summary>
/// A normalized telemetry reading from a locally connected device (scale, etc.).
/// The outbound analogue of <see cref="Transport.PrintJobMessage"/> — serialized to JSON
/// and published over a transport. Weight uses <see cref="decimal"/> to avoid float drift.
/// </summary>
public class DeviceReading
{
    [JsonPropertyName("readingId")]
    public string ReadingId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Logical workstation identifier (config; defaults to the machine name).</summary>
    [JsonPropertyName("station")]
    public string Station { get; set; } = string.Empty;

    /// <summary>Machine name the reading was captured on (Environment.MachineName).</summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    /// <summary>Configured device name.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>serial-scale | hid-scale | serial-raw | hid-raw | simulated</summary>
    [JsonPropertyName("deviceType")]
    public string DeviceType { get; set; } = string.Empty;

    /// <summary>Physical origin of the reading (connection type, port, VID/PID).</summary>
    [JsonPropertyName("source")]
    public ReadingSource Source { get; set; } = new();

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Primary scalar measurement (weight). Null for raw/error frames.</summary>
    [JsonPropertyName("value")]
    public decimal? Value { get; set; }

    /// <summary>Unit of <see cref="Value"/>: g, kg, lb, oz, etc.</summary>
    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }

    /// <summary>
    /// Measurement: ok | motion | overload | underload | zero | error.
    /// Lifecycle events (value is null): connected | disconnected | stale | command-sent | command-failed.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    /// <summary>True for lifecycle/discovery events (connected, disconnected, stale, command-sent...) as opposed to measurements.</summary>
    [JsonIgnore]
    public bool IsEvent => Status is "connected" or "disconnected" or "stale" or "command-sent" or "command-failed";

    /// <summary>Dimensioner output. Null for scales (reserved for future device types).</summary>
    [JsonPropertyName("dimensions")]
    public Dimensions? Dimensions { get; set; }

    /// <summary>The exact ASCII line or hex report the device emitted, for passthrough and debugging.</summary>
    [JsonPropertyName("raw")]
    public string? Raw { get; set; }

    /// <summary>The exact frame bytes as hex (lossless, including control characters and bytes the text decoding cannot represent).</summary>
    [JsonPropertyName("rawHex")]
    public string? RawHex { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>Where a reading physically came from.</summary>
public class ReadingSource
{
    [JsonPropertyName("connection")]
    public string Connection { get; set; } = string.Empty; // serial | hid | simulated

    [JsonPropertyName("port")]
    public string? Port { get; set; } // "COM3" for serial

    [JsonPropertyName("vid")]
    public int? Vid { get; set; } // HID vendor id

    [JsonPropertyName("pid")]
    public int? Pid { get; set; } // HID product id

    [JsonPropertyName("product")]
    public string? Product { get; set; } // HID product string, when available

    [JsonPropertyName("host")]
    public string? Host { get; set; } // tcp host

    [JsonPropertyName("tcpPort")]
    public int? TcpPort { get; set; } // tcp port
}

/// <summary>Dimensioner output. Defined now; populated when a dimensioner reader is added.</summary>
public class Dimensions
{
    [JsonPropertyName("length")]
    public decimal Length { get; set; }

    [JsonPropertyName("width")]
    public decimal Width { get; set; }

    [JsonPropertyName("height")]
    public decimal Height { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "mm";

    [JsonPropertyName("volume")]
    public decimal? Volume { get; set; }
}

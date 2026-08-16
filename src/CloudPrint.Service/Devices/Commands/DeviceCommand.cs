using System.Text.Json.Serialization;

namespace CloudPrint.Service.Devices.Commands;

/// <summary>
/// A cloud→device command as it arrives on the wire (SQS message body). Either <see cref="Command"/>
/// (text with the usual escapes: <c>&lt;STX&gt;M&lt;ETX&gt;</c>, <c>\x02</c>) — the device's command terminator is
/// appended unless <see cref="Terminator"/> overrides it — or <see cref="BytesBase64"/> for exact bytes
/// (HID output reports, binary protocols).
/// </summary>
public sealed class DeviceCommandMessage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Configured device name (matches <c>Devices[].Name</c> / the reading's <c>deviceId</c>).</summary>
    [JsonPropertyName("device")]
    public string? Device { get; set; }

    /// <summary>Alias for <see cref="Device"/> so producers can mirror the reading's field name.</summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("bytesBase64")]
    public string? BytesBase64 { get; set; }

    /// <summary>null = device default; "none" = nothing appended; otherwise an escaped literal ("\r", "&lt;CR&gt;&lt;LF&gt;").</summary>
    [JsonPropertyName("terminator")]
    public string? Terminator { get; set; }

    /// <summary>Readings arriving within this window after the send are tagged with the command id (default 5000 ms).</summary>
    [JsonPropertyName("replyWindowMs")]
    public int? ReplyWindowMs { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonIgnore]
    public string TargetDevice => (Device ?? DeviceId ?? string.Empty).Trim();
}

/// <summary>A command resolved against its target device: exact bytes to write plus bookkeeping.</summary>
public sealed record DeviceCommand(
    string Id,
    byte[] Payload,
    TimeSpan ReplyWindow,
    IReadOnlyDictionary<string, string>? Metadata,
    string Description);

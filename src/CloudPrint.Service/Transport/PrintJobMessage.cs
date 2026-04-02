using System.Text.Json.Serialization;

namespace CloudPrint.Service.Transport;

public class PrintJobMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("fileUrl")]
    public string FileUrl { get; set; } = string.Empty;

    [JsonPropertyName("printerName")]
    public string PrinterName { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("copies")]
    public int Copies { get; set; } = 1;

    /// <summary>
    /// Inline print content. For text-based content types (ZPL, text/plain) this is the raw string.
    /// For binary content types (images) this must be base64-encoded.
    /// When set, <see cref="FileUrl"/> is not required.
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

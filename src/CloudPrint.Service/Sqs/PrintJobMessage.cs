using System.Text.Json.Serialization;

namespace CloudPrint.Service.Sqs;

public class PrintJobMessage
{
    [JsonPropertyName("fileUrl")]
    public string FileUrl { get; set; } = string.Empty;

    [JsonPropertyName("printerName")]
    public string PrinterName { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("copies")]
    public int Copies { get; set; } = 1;

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

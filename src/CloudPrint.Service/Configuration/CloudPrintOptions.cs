namespace CloudPrint.Service.Configuration;

public class CloudPrintOptions
{
    public const string SectionName = "CloudPrint";

    // Transport: "sqs" or "http"
    public string Transport { get; set; } = "sqs";

    // SQS — credentials and region are shared across all lanes
    public string Region { get; set; } = "us-east-1";
    public string AwsAccessKeyId { get; set; } = string.Empty;
    public string AwsSecretAccessKey { get; set; } = string.Empty;
    public int VisibilityTimeoutSeconds { get; set; } = 300;

    // SQS — per-printer lanes. Empty in legacy single-printer configs (auto-promoted at startup).
    public List<PrinterLane> Printers { get; set; } = new();

    // HTTP API settings (always single-printer; uses top-level PrinterName below)
    public string ApiUrl { get; set; } = string.Empty;
    public string AckUrl { get; set; } = string.Empty;
    public string ApiHeaderName { get; set; } = "X-Api-Key";
    public string ApiHeaderValue { get; set; } = string.Empty;
    public int HttpPollTimeoutSeconds { get; set; } = 30;

    // Legacy single-printer fields. HTTP transport uses these directly.
    // SQS uses these only when Printers[] is empty (auto-promoted to a single lane on startup).
    public string QueueUrl { get; set; } = string.Empty;
    public string PrinterName { get; set; } = string.Empty;

    // PDF printing — global defaults; lanes may override individually.
    public int PdfRenderDpi { get; set; } = 300;
    public string PdfFitMode { get; set; } = "Margins";

    // Debug
    public bool DumpPayloads { get; set; } = false;
    public string DumpPath { get; set; } = @"C:\ProgramData\CloudPrint\dumps";

    /// <summary>
    /// Resolves SQS lanes from configuration with PDF defaults applied.
    /// Returns the explicit Printers[] list, or auto-promotes a legacy single-printer
    /// config (top-level QueueUrl + PrinterName) into a one-lane list.
    /// Returns empty when SQS is not configured.
    /// </summary>
    public IReadOnlyList<ResolvedLane> ResolvedSqsLanes()
    {
        if (Printers.Count > 0)
            return Printers.Select(Resolve).ToList();

        if (!string.IsNullOrWhiteSpace(QueueUrl) && !string.IsNullOrWhiteSpace(PrinterName))
            return new[] { Resolve(new PrinterLane { PrinterName = PrinterName, QueueUrl = QueueUrl }) };

        return Array.Empty<ResolvedLane>();
    }

    private ResolvedLane Resolve(PrinterLane lane) => new(
        PrinterName: lane.PrinterName,
        QueueUrl: lane.QueueUrl,
        PdfRenderDpi: lane.PdfRenderDpi is > 0 ? lane.PdfRenderDpi.Value : PdfRenderDpi,
        PdfFitMode: !string.IsNullOrWhiteSpace(lane.PdfFitMode) ? lane.PdfFitMode! : PdfFitMode);
}

public class PrinterLane
{
    public string PrinterName { get; set; } = string.Empty;
    public string QueueUrl { get; set; } = string.Empty;
    public int? PdfRenderDpi { get; set; }
    public string? PdfFitMode { get; set; }
}

public record ResolvedLane(string PrinterName, string QueueUrl, int PdfRenderDpi, string PdfFitMode);

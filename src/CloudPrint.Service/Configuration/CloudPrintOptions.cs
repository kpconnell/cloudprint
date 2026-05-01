namespace CloudPrint.Service.Configuration;

public class CloudPrintOptions
{
    public const string SectionName = "CloudPrint";

    // Transport: "sqs" or "http"
    public string Transport { get; set; } = "sqs";

    // SQS settings
    public string QueueUrl { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string AwsAccessKeyId { get; set; } = string.Empty;
    public string AwsSecretAccessKey { get; set; } = string.Empty;
    public int VisibilityTimeoutSeconds { get; set; } = 300;

    // HTTP API settings
    public string ApiUrl { get; set; } = string.Empty;
    public string AckUrl { get; set; } = string.Empty;
    public string ApiHeaderName { get; set; } = "X-Api-Key";
    public string ApiHeaderValue { get; set; } = string.Empty;
    public int HttpPollTimeoutSeconds { get; set; } = 30;

    // Shared
    public string PrinterName { get; set; } = string.Empty;

    // PDF printing
    // PdfRenderDpi: rasterization DPI (203 typical for thermal label printers, 300 for office)
    // PdfFitMode: "Margins" (fit within driver-reported margins) or "PhysicalPage" (edge-to-edge)
    public int PdfRenderDpi { get; set; } = 300;
    public string PdfFitMode { get; set; } = "Margins";

    // Debug
    public bool DumpPayloads { get; set; } = false;
    public string DumpPath { get; set; } = @"C:\ProgramData\CloudPrint\dumps";
}

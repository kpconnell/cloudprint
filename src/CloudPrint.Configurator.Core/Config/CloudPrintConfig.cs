namespace CloudPrint.Configurator.Core.Config;

/// <summary>
/// Serializable model of the "CloudPrint" section of appsettings.json.
/// Deliberately decoupled from CloudPrint.Service so the configurator depends only on the
/// JSON contract, not the service assembly. Drift against the service schema is caught by
/// a test that round-trips the committed samples/appsettings.sample.json through this model.
/// Property names are PascalCase to match the config keys the service binds.
/// </summary>
public sealed class CloudPrintConfig
{
    // Transport: "sqs" or "http"
    public string Transport { get; set; } = "sqs";

    // SQS — credentials and region are shared across all printer lanes.
    public string? Region { get; set; }
    public string? AwsAccessKeyId { get; set; }
    public string? AwsSecretAccessKey { get; set; }
    public int? VisibilityTimeoutSeconds { get; set; }

    // SQS — per-printer lanes.
    public List<PrinterLaneModel> Printers { get; set; } = new();

    // HTTP API settings (single-printer; uses PrinterName below).
    public string? ApiUrl { get; set; }
    public string? AckUrl { get; set; }
    public string? ApiHeaderName { get; set; }
    public string? ApiHeaderValue { get; set; }
    public int? HttpPollTimeoutSeconds { get; set; }

    // Legacy single-printer fields. HTTP transport uses PrinterName directly.
    public string? QueueUrl { get; set; }
    public string? PrinterName { get; set; }

    // PDF printing — global defaults; printer lanes may override individually.
    public int? PdfRenderDpi { get; set; }
    public string? PdfFitMode { get; set; }
    public bool? PdfMonochrome { get; set; }

    // Debug payload dumping.
    public bool DumpPayloads { get; set; }
    public string? DumpPath { get; set; }

    // Device telemetry (outbound). Empty Devices = feature off.
    public string? Station { get; set; }
    public int? DevicePollIntervalMs { get; set; }
    public bool? DeviceStableOnly { get; set; }
    public List<DeviceModel> Devices { get; set; } = new();
}

/// <summary>One inbound printer queue lane (SQS transport).</summary>
public sealed class PrinterLaneModel
{
    public string PrinterName { get; set; } = string.Empty;
    public string QueueUrl { get; set; } = string.Empty;
    public int? PdfRenderDpi { get; set; }
    public string? PdfFitMode { get; set; }
    public bool? PdfMonochrome { get; set; }
}

/// <summary>One outbound telemetry device. Mirrors CloudPrint.Service.Configuration.DeviceConfig.</summary>
public sealed class DeviceModel
{
    public string Name { get; set; } = string.Empty;          // unique id, used as DeviceId and log tag
    public string Type { get; set; } = string.Empty;          // serial-scale | hid-scale | serial-raw | hid-raw
    public string? Protocol { get; set; }                     // serial parser selector (default mt-sics)
    public string? Station { get; set; }                      // per-device station override

    // Serial / USB-CDC
    public string? ComPort { get; set; }
    public int? BaudRate { get; set; }
    public string? Parity { get; set; }                       // None | Even | Odd
    public int? DataBits { get; set; }
    public int? StopBits { get; set; }
    public string? LineEnding { get; set; }                   // crlf | lf | cr | literal
    public string? Encoding { get; set; }                     // ascii | utf8
    public string? RequestCommand { get; set; }
    public List<string>? InitCommands { get; set; }

    // HID
    public int? Vid { get; set; }
    public int? Pid { get; set; }
    public int? HidReportId { get; set; }
    public int? HidStatusOffset { get; set; }
    public int? HidUnitOffset { get; set; }
    public int? HidExponentOffset { get; set; }
    public int? HidWeightOffset { get; set; }
    public int? HidWeightSize { get; set; }

    // Generic passthrough
    public string? Pattern { get; set; }

    // Behaviour
    public string? PollMode { get; set; }                     // stream | request | interval
    public int? PollIntervalMs { get; set; }
    public bool? StableOnly { get; set; }

    public DeviceOutputModel? Output { get; set; }
}

/// <summary>Per-device outbound transport target.</summary>
public sealed class DeviceOutputModel
{
    public string Transport { get; set; } = "sqs";            // sqs | http
    public string? QueueUrl { get; set; }
    public string? WebhookUrl { get; set; }
    public string? HeaderName { get; set; }
    public string? HeaderValue { get; set; }
}

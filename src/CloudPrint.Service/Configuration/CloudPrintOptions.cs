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

    // Device telemetry (outbound). Empty Devices = feature off; existing installs are unaffected.
    public string Station { get; set; } = string.Empty;       // logical workstation id; blank => machine name
    public int DevicePollIntervalMs { get; set; } = 500;      // global default
    public bool DeviceStableOnly { get; set; } = true;        // global default
    public List<DeviceConfig> Devices { get; set; } = new();

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

    /// <summary>
    /// Resolves configured devices with global defaults applied. Entries without a Name are skipped.
    /// Empty when no devices are configured (the feature is off).
    /// </summary>
    public IReadOnlyList<ResolvedDevice> ResolvedDevices() =>
        Devices.Where(d => !string.IsNullOrWhiteSpace(d.Name)).Select(ResolveDevice).ToList();

    private string EffectiveStation =>
        !string.IsNullOrWhiteSpace(Station) ? Station : Environment.MachineName;

    private ResolvedDevice ResolveDevice(DeviceConfig d) => new(
        Name: d.Name,
        Type: string.IsNullOrWhiteSpace(d.Type) ? "serial-scale" : d.Type.Trim().ToLowerInvariant(),
        Protocol: string.IsNullOrWhiteSpace(d.Protocol) ? "mt-sics" : d.Protocol!.Trim().ToLowerInvariant(),
        Station: !string.IsNullOrWhiteSpace(d.Station) ? d.Station! : EffectiveStation,
        ComPort: d.ComPort,
        BaudRate: d.BaudRate is > 0 ? d.BaudRate.Value : 9600,
        Parity: string.IsNullOrWhiteSpace(d.Parity) ? "None" : d.Parity!,
        DataBits: d.DataBits is > 0 ? d.DataBits.Value : 8,
        StopBits: d.StopBits is > 0 ? d.StopBits.Value : 1,
        LineEnding: string.IsNullOrWhiteSpace(d.LineEnding) ? "crlf" : d.LineEnding!.Trim().ToLowerInvariant(),
        Encoding: string.IsNullOrWhiteSpace(d.Encoding) ? "ascii" : d.Encoding!.Trim().ToLowerInvariant(),
        RequestCommand: d.RequestCommand,
        InitCommands: d.InitCommands ?? new List<string>(),
        Vid: d.Vid,
        Pid: d.Pid,
        Hid: new HidOverrides(d.HidReportId, d.HidStatusOffset, d.HidUnitOffset, d.HidExponentOffset, d.HidWeightOffset, d.HidWeightSize),
        Pattern: d.Pattern,
        PollMode: string.IsNullOrWhiteSpace(d.PollMode) ? "stream" : d.PollMode!.Trim().ToLowerInvariant(),
        PollIntervalMs: d.PollIntervalMs is > 0 ? d.PollIntervalMs.Value : DevicePollIntervalMs,
        StableOnly: d.StableOnly ?? DeviceStableOnly,
        Output: ResolveOutput(d.Output));

    private static ResolvedOutput ResolveOutput(DeviceOutputConfig? o) => new(
        Transport: string.IsNullOrWhiteSpace(o?.Transport) ? "sqs" : o!.Transport.Trim().ToLowerInvariant(),
        QueueUrl: o?.QueueUrl,
        WebhookUrl: o?.WebhookUrl,
        HeaderName: o?.HeaderName,
        HeaderValue: o?.HeaderValue);
}

public class PrinterLane
{
    public string PrinterName { get; set; } = string.Empty;
    public string QueueUrl { get; set; } = string.Empty;
    public int? PdfRenderDpi { get; set; }
    public string? PdfFitMode { get; set; }
}

public record ResolvedLane(string PrinterName, string QueueUrl, int PdfRenderDpi, string PdfFitMode);

/// <summary>Raw device configuration as bound from appsettings.json.</summary>
public class DeviceConfig
{
    public string Name { get; set; } = string.Empty;        // unique id, used as DeviceId and log tag
    public string Type { get; set; } = string.Empty;        // serial-scale | hid-scale | serial-raw | hid-raw
    public string? Protocol { get; set; }                   // serial parser selector (default mt-sics)
    public string? Station { get; set; }                    // per-device station override

    // Serial / USB-CDC
    public string? ComPort { get; set; }                    // e.g. "COM3"
    public int? BaudRate { get; set; }                      // default 9600
    public string? Parity { get; set; }                     // None | Even | Odd
    public int? DataBits { get; set; }                      // default 8
    public int? StopBits { get; set; }                      // default 1
    public string? LineEnding { get; set; }                 // crlf | lf | cr | literal (default crlf)
    public string? Encoding { get; set; }                   // ascii | utf8 (default ascii)
    public string? RequestCommand { get; set; }             // poll command for request/interval mode
    public List<string>? InitCommands { get; set; }         // commands sent on connect (e.g. zero/tare)

    // HID
    public int? Vid { get; set; }
    public int? Pid { get; set; }
    public int? HidReportId { get; set; }                   // optional manual field-map overrides for
    public int? HidStatusOffset { get; set; }               // non-conformant HID scales
    public int? HidUnitOffset { get; set; }
    public int? HidExponentOffset { get; set; }
    public int? HidWeightOffset { get; set; }
    public int? HidWeightSize { get; set; }

    // Generic passthrough
    public string? Pattern { get; set; }                    // regex with named groups value/unit/stable

    // Behaviour
    public string? PollMode { get; set; }                   // stream | request | interval (default stream)
    public int? PollIntervalMs { get; set; }
    public bool? StableOnly { get; set; }

    public DeviceOutputConfig? Output { get; set; }
}

/// <summary>Per-device outbound transport target.</summary>
public class DeviceOutputConfig
{
    public string Transport { get; set; } = "sqs";          // sqs | http
    public string? QueueUrl { get; set; }
    public string? WebhookUrl { get; set; }
    public string? HeaderName { get; set; }                 // e.g. X-Api-Key (mirrors ApiHeaderName)
    public string? HeaderValue { get; set; }
}

public record ResolvedDevice(
    string Name,
    string Type,
    string Protocol,
    string Station,
    string? ComPort,
    int BaudRate,
    string Parity,
    int DataBits,
    int StopBits,
    string LineEnding,
    string Encoding,
    string? RequestCommand,
    IReadOnlyList<string> InitCommands,
    int? Vid,
    int? Pid,
    HidOverrides Hid,
    string? Pattern,
    string PollMode,
    int PollIntervalMs,
    bool StableOnly,
    ResolvedOutput Output);

/// <summary>Optional per-device HID field-map overrides (byte offsets). Null = use the default layout.</summary>
public record HidOverrides(
    int? ReportId,
    int? StatusOffset,
    int? UnitOffset,
    int? ExponentOffset,
    int? WeightOffset,
    int? WeightSize);

public record ResolvedOutput(string Transport, string? QueueUrl, string? WebhookUrl, string? HeaderName, string? HeaderValue);

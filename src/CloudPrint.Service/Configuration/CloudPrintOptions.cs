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
    // PdfRenderDpi should match the printer's native resolution (e.g. 203 for
    // direct thermal) so the rasterized page maps 1:1 onto device dots.
    public int PdfRenderDpi { get; set; } = 300;
    public string PdfFitMode { get; set; } = "PhysicalPage";
    public bool PdfMonochrome { get; set; } = false;
    // Stock loaded in the printer ("4x6", "2x2", "Letter", "A4", or "WxH" inches).
    // Empty = the Windows queue's driver default paper.
    public string PdfPaperSize { get; set; } = string.Empty;

    // Debug
    public bool DumpPayloads { get; set; } = false;
    public string DumpPath { get; set; } = @"C:\ProgramData\CloudPrint\dumps";

    // Device telemetry (outbound). Empty Devices = feature off; existing installs are unaffected.
    public string Station { get; set; } = string.Empty;       // logical workstation id; blank => machine name
    public int DevicePollIntervalMs { get; set; } = 500;      // global default
    public bool DeviceStableOnly { get; set; } = true;        // global default
    public int DeviceHeartbeatSeconds { get; set; } = 0;      // re-publish an unchanged reading every N s (0 = off)
    public int DeviceStaleAfterSeconds { get; set; } = 0;     // publish a "stale" event after N s without data (0 = off)
    public string DeviceCommandQueueUrl { get; set; } = string.Empty; // SQS queue of cloud→device commands (empty = off)
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
        PdfFitMode: !string.IsNullOrWhiteSpace(lane.PdfFitMode) ? lane.PdfFitMode! : PdfFitMode,
        PdfMonochrome: lane.PdfMonochrome ?? PdfMonochrome,
        PdfPaperSize: !string.IsNullOrWhiteSpace(lane.PdfPaperSize) ? lane.PdfPaperSize! : PdfPaperSize);

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
        Host: d.Host?.Trim(),
        Port: d.Port ?? 0,
        ConnectTimeout: TimeSpan.FromMilliseconds(d.ConnectTimeoutMs is > 0 ? d.ConnectTimeoutMs.Value : 5000),
        ComPort: d.ComPort,
        BaudRate: d.BaudRate is > 0 ? d.BaudRate.Value : 9600,
        Parity: string.IsNullOrWhiteSpace(d.Parity) ? "None" : d.Parity!,
        DataBits: d.DataBits is > 0 ? d.DataBits.Value : 8,
        StopBits: d.StopBits is > 0 ? d.StopBits.Value : 1,
        DtrEnable: d.DtrEnable ?? false,
        RtsEnable: d.RtsEnable ?? false,
        LineEnding: string.IsNullOrWhiteSpace(d.LineEnding) ? "crlf" : d.LineEnding!.Trim().ToLowerInvariant(),
        Encoding: string.IsNullOrWhiteSpace(d.Encoding) ? "ascii" : d.Encoding!.Trim().ToLowerInvariant(),
        RequestCommand: d.RequestCommand,
        InitCommands: d.InitCommands ?? new List<string>(),
        CommandTerminator: d.CommandTerminator,
        Framing: ResolveFraming(d),
        ReadTimeout: TimeSpan.FromMilliseconds(d.ReadTimeoutMs is > 0 ? d.ReadTimeoutMs.Value : 2000),
        Vid: d.Vid,
        Pid: d.Pid,
        Hid: new HidOverrides(d.HidReportId, d.HidStatusOffset, d.HidUnitOffset, d.HidExponentOffset, d.HidWeightOffset, d.HidWeightSize),
        Pattern: d.Pattern,
        PollMode: string.IsNullOrWhiteSpace(d.PollMode) ? "stream" : d.PollMode!.Trim().ToLowerInvariant(),
        PollIntervalMs: d.PollIntervalMs is > 0 ? d.PollIntervalMs.Value : DevicePollIntervalMs,
        StableOnly: d.StableOnly ?? DeviceStableOnly,
        Heartbeat: SecondsOrOff(d.HeartbeatSeconds ?? DeviceHeartbeatSeconds),
        StaleAfter: SecondsOrOff(d.StaleAfterSeconds ?? DeviceStaleAfterSeconds),
        Output: ResolveOutput(d.Output));

    private static TimeSpan? SecondsOrOff(int seconds) => seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>
    /// Resolves the framing options for serial/tcp devices. Line mode keeps the historical LineEnding
    /// semantics (crlf/lf/cr or an escaped literal); delimited and idle modes are the new byte-level controls.
    /// </summary>
    private static CloudPrint.Service.Devices.Framing.FramingOptions ResolveFraming(DeviceConfig d)
    {
        var mode = (d.FrameMode ?? "line").Trim().ToLowerInvariant() switch
        {
            "delimited" or "stxetx" => CloudPrint.Service.Devices.Framing.FrameMode.Delimited,
            "idle" or "gap" or "discovery" => CloudPrint.Service.Devices.Framing.FrameMode.Idle,
            _ => CloudPrint.Service.Devices.Framing.FrameMode.Line
        };
        var terminator = LineEndingBytes(d.LineEnding);
        var start = Bytes(CloudPrint.Service.Devices.Framing.ControlEscapes.Unescape(d.FrameStart));
        var end = Bytes(CloudPrint.Service.Devices.Framing.ControlEscapes.Unescape(d.FrameEnd));
        if (mode == CloudPrint.Service.Devices.Framing.FrameMode.Delimited && end.Length == 0)
            end = terminator; // delimited without an explicit end: fall back to the line ending
        return new CloudPrint.Service.Devices.Framing.FramingOptions(
            mode, terminator, start, end,
            TimeSpan.FromMilliseconds(d.IdleGapMs is > 0 ? d.IdleGapMs.Value : 150),
            d.MaxFrameBytes is > 0 ? d.MaxFrameBytes.Value : 4096);
    }

    /// <summary>crlf | lf | cr | escaped literal → bytes. Shared by framing and the default command terminator.</summary>
    public static string LineEndingText(string? lineEnding) => (lineEnding ?? "crlf").Trim().ToLowerInvariant() switch
    {
        "" or "crlf" => "\r\n",
        "lf" => "\n",
        "cr" => "\r",
        "none" => "",
        _ => CloudPrint.Service.Devices.Framing.ControlEscapes.Unescape(lineEnding!.Trim())
    };

    public static byte[] LineEndingBytes(string? lineEnding) => Bytes(LineEndingText(lineEnding));

    private static byte[] Bytes(string s) => System.Text.Encoding.Latin1.GetBytes(s);

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
    public bool? PdfMonochrome { get; set; }
    public string? PdfPaperSize { get; set; }
}

public record ResolvedLane(
    string PrinterName, string QueueUrl, int PdfRenderDpi, string PdfFitMode,
    bool PdfMonochrome = false, string PdfPaperSize = "");

/// <summary>Raw device configuration as bound from appsettings.json.</summary>
public class DeviceConfig
{
    public string Name { get; set; } = string.Empty;        // unique id, used as DeviceId and log tag
    public string Type { get; set; } = string.Empty;        // serial-scale | serial-raw | hid-scale | hid-raw | tcp-scale | tcp-raw
    public string? Protocol { get; set; }                   // serial parser selector (default mt-sics)
    public string? Station { get; set; }                    // per-device station override

    // TCP client (device is the server, e.g. Cubiscan :1050, iDimension)
    public string? Host { get; set; }
    public int? Port { get; set; }
    public int? ConnectTimeoutMs { get; set; }              // default 5000

    // Serial / USB-CDC
    public string? ComPort { get; set; }                    // e.g. "COM3"
    public int? BaudRate { get; set; }                      // default 9600
    public string? Parity { get; set; }                     // None | Even | Odd
    public int? DataBits { get; set; }                      // default 8
    public int? StopBits { get; set; }                      // default 1
    public bool? DtrEnable { get; set; }                    // assert DTR on open (default false)
    public bool? RtsEnable { get; set; }                    // assert RTS on open (default false)
    public string? LineEnding { get; set; }                 // crlf | lf | cr | literal/escaped (default crlf)
    public string? Encoding { get; set; }                   // ascii | utf8 | latin1 (default ascii)
    public string? RequestCommand { get; set; }             // poll command for request/interval mode (escapes: \x02, <STX>)
    public List<string>? InitCommands { get; set; }         // commands sent on connect (e.g. zero/tare)
    public string? CommandTerminator { get; set; }          // appended to every command: null = LineEnding, "none" = nothing, else escaped literal

    // Framing (serial + tcp): how the byte stream is cut into frames
    public string? FrameMode { get; set; }                  // line (default) | delimited | idle
    public string? FrameStart { get; set; }                 // delimited: optional start sequence, e.g. "<STX>"
    public string? FrameEnd { get; set; }                   // delimited: end sequence, e.g. "<ETX>"
    public int? IdleGapMs { get; set; }                     // idle: silence that closes a frame (default 150)
    public int? MaxFrameBytes { get; set; }                 // safety cap (default 4096)
    public int? ReadTimeoutMs { get; set; }                 // how long one read cycle waits for data (default 2000)

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
    public int? HeartbeatSeconds { get; set; }              // per-device override of DeviceHeartbeatSeconds
    public int? StaleAfterSeconds { get; set; }             // per-device override of DeviceStaleAfterSeconds

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
    string? Host,
    int Port,
    TimeSpan ConnectTimeout,
    string? ComPort,
    int BaudRate,
    string Parity,
    int DataBits,
    int StopBits,
    bool DtrEnable,
    bool RtsEnable,
    string LineEnding,
    string Encoding,
    string? RequestCommand,
    IReadOnlyList<string> InitCommands,
    string? CommandTerminator,
    CloudPrint.Service.Devices.Framing.FramingOptions Framing,
    TimeSpan ReadTimeout,
    int? Vid,
    int? Pid,
    HidOverrides Hid,
    string? Pattern,
    string PollMode,
    int PollIntervalMs,
    bool StableOnly,
    TimeSpan? Heartbeat,
    TimeSpan? StaleAfter,
    ResolvedOutput Output)
{
    /// <summary>The bytes appended to every command written to the device (request, init, cloud commands).</summary>
    public string EffectiveCommandTerminator =>
        CommandTerminator is null ? CloudPrintOptions.LineEndingText(LineEnding)
        : CommandTerminator.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty
        : CloudPrint.Service.Devices.Framing.ControlEscapes.Unescape(CommandTerminator);

    public bool IsTcp => Type.StartsWith("tcp-", StringComparison.OrdinalIgnoreCase);
    public bool IsSerial => Type.StartsWith("serial-", StringComparison.OrdinalIgnoreCase);
    public bool IsHid => Type.StartsWith("hid-", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Optional per-device HID field-map overrides (byte offsets). Null = use the default layout.</summary>
public record HidOverrides(
    int? ReportId,
    int? StatusOffset,
    int? UnitOffset,
    int? ExponentOffset,
    int? WeightOffset,
    int? WeightSize);

public record ResolvedOutput(string Transport, string? QueueUrl, string? WebhookUrl, string? HeaderName, string? HeaderValue);

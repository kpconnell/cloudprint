namespace CloudPrint.Configurator.Core.Config;

/// <summary>
/// Canonical option values and defaults for the configurator UI. Kept in Core (not the WinForms
/// shell) so dropdown contents and defaults are unit-testable and match the service's accepted values.
/// </summary>
public static class ConfigDefaults
{
    // Transports
    public const string TransportSqs = "sqs";
    public const string TransportHttp = "http";
    public static readonly IReadOnlyList<string> Transports = new[] { TransportSqs, TransportHttp };

    // Device types
    public const string DeviceSerialScale = "serial-scale";
    public const string DeviceHidScale = "hid-scale";
    public const string DeviceSerialRaw = "serial-raw";
    public const string DeviceHidRaw = "hid-raw";
    public const string DeviceTcpScale = "tcp-scale";
    public const string DeviceTcpRaw = "tcp-raw";
    public static readonly IReadOnlyList<string> DeviceTypes =
        new[] { DeviceSerialRaw, DeviceSerialScale, DeviceHidRaw, DeviceHidScale, DeviceTcpRaw, DeviceTcpScale };

    public static bool IsSerial(string type) => type is DeviceSerialScale or DeviceSerialRaw;
    public static bool IsHid(string type) => type is DeviceHidScale or DeviceHidRaw;
    public static bool IsTcp(string type) => type is DeviceTcpScale or DeviceTcpRaw;
    /// <summary>Serial and TCP share the byte-stream settings (framing, request/init commands, terminator, encoding).</summary>
    public static bool IsStream(string type) => IsSerial(type) || IsTcp(type);

    // Serial line endings, parities, encodings, framing
    public static readonly IReadOnlyList<string> LineEndings = new[] { "crlf", "lf", "cr", "literal" };
    public static readonly IReadOnlyList<string> Parities = new[] { "None", "Even", "Odd" };
    public static readonly IReadOnlyList<string> Encodings = new[] { "ascii", "utf8", "latin1" };
    public static readonly IReadOnlyList<string> FrameModes = new[] { "line", "delimited", "idle" };
    public const string DefaultFrameMode = "line";
    public const int DefaultIdleGapMs = 150;
    public const int DefaultTcpPort = 1050;              // Cubiscan's default; iDimension is user-set
    public const string CommandTerminatorDefaultLabel = "(same as line ending)";
    public const string CommandTerminatorNone = "none";
    public static readonly IReadOnlyList<int> BaudRates = new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
    public static readonly IReadOnlyList<int> DataBitsOptions = new[] { 7, 8 };
    public static readonly IReadOnlyList<int> StopBitsOptions = new[] { 1, 2 };

    // Scale protocols (serial parser selectors). "generic" + the named protocols the service ships.
    public static readonly IReadOnlyList<string> Protocols = new[] { "mt-sics", "generic" };

    // Poll modes
    public static readonly IReadOnlyList<string> PollModes = new[] { "stream", "request", "interval" };

    // PDF fit modes
    public const string FitMargins = "Margins";
    public const string FitPhysicalPage = "PhysicalPage";
    public static readonly IReadOnlyList<string> FitModes = new[] { FitMargins, FitPhysicalPage };

    // Common printer resolutions offered in the editor; the field stays editable
    // for anything else. Matching the printer's native DPI avoids a quality-killing
    // resample between rasterization and the driver.
    public static readonly IReadOnlyList<int> CommonPdfRenderDpis = new[] { 203, 300, 600 };
    public const int MinPdfRenderDpi = 72;
    public const int MaxPdfRenderDpi = 1200;

    // Paper sizes: what's loaded in the printer. Empty string = the Windows queue's
    // driver default. The label shown in the UI for the empty value is PaperDriverDefaultLabel.
    public const string PaperDriverDefault = "";
    public const string PaperDriverDefaultLabel = "Driver default";
    public static readonly IReadOnlyList<string> PaperSizes = new[] { "2x2", "4x6", "Letter", "A4" };

    // Defaults (mirror CloudPrint.Service.Configuration.CloudPrintOptions)
    public const int DefaultPdfRenderDpi = 300;
    public const string DefaultPdfFitMode = FitPhysicalPage;
    public const bool DefaultPdfMonochrome = false;
    public const string DefaultPdfPaperSize = PaperDriverDefault;
    public const int DefaultVisibilityTimeoutSeconds = 300;
    public const int DefaultHttpPollTimeoutSeconds = 30;
    public const int DefaultBaudRate = 9600;
    public const int DefaultDataBits = 8;
    public const int DefaultStopBits = 1;
    public const string DefaultParity = "None";
    public const string DefaultLineEnding = "crlf";
    public const string DefaultEncoding = "ascii";
    public const string DefaultProtocol = "mt-sics";
    public const string DefaultPollMode = "stream";
    public const int DefaultDevicePollIntervalMs = 500;
    public const bool DefaultDeviceStableOnly = true;
    public const int DefaultDeviceHeartbeatSeconds = 0;
    public const int DefaultDeviceStaleAfterSeconds = 0;
    /// <summary>New serial/TCP devices start as raw passthrough: the frame reaches the cloud verbatim and nothing is guessed.</summary>
    public const string DefaultDeviceType = DeviceSerialRaw;
}

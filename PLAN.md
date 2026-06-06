# CloudPrint - Implementation Plan

> **Note:** This is the original print-only implementation plan and is kept for history; parts have drifted from the current code. CloudPrint is now a **bidirectional cloud ⇆ local-device bridge**: it both receives print jobs (cloud → device) and forwards device telemetry (device → cloud). See [README.md](README.md) for current behavior and the [Device Telemetry](#device-telemetry-subsystem) section below for the outbound subsystem.

## Overview

A Windows Service that bridges the cloud and locally connected devices in both directions:
- **Printing:** long-polls AWS SQS (or an HTTP API) for print jobs and routes them to local printers.
- **Device telemetry:** reads locally connected devices (USB/serial scales) and publishes readings outbound over SQS or an HTTP webhook.

Both share transports and credentials; device telemetry is opt-in via `Devices[]`.

## Device Telemetry Subsystem

Mirrors the inbound print pipeline in reverse — `IDeviceReader.ReadAsync → DeviceReading → IReadingPublisher.PublishAsync` — with one `DeviceForwardingService` (BackgroundService) per configured device, registered alongside the SQS print lanes in `Program.cs`.

```
src/CloudPrint.Service/
├── Devices/
│   ├── DeviceReading.cs            # normalized reading model (station/host/source identity)
│   ├── IDeviceReader.cs            # per-device reader seam (connect/read/reconnect)
│   ├── DeviceForwardingService.cs  # per-device BackgroundService (read → publish)
│   ├── DeviceReaderRegistry.cs     # device type → factory (extension point)
│   ├── ReadingDeduplicator.cs      # suppress repeated identical readings
│   ├── ReconnectBackoff.cs         # capped reconnect backoff
│   ├── Parsing/                    # SerialScaleParser, HidPosReportParser, PatternExtractor
│   └── Readers/                    # SerialDeviceReader, HidDeviceReader (#if WINDOWS), SimulatedDeviceReader
└── Publishing/
    ├── IReadingPublisher.cs        # outbound transport seam
    ├── SqsReadingPublisher.cs      # SQS SendMessage
    ├── HttpReadingPublisher.cs     # HTTPS webhook POST (SSRF-validated)
    └── DryRunReadingPublisher.cs   # logs instead of publishing (dry-run / non-Windows)
```

Device types: `serial-scale` (System.IO.Ports), `hid-scale` (HidSharp, descriptor-tolerant + per-device offset overrides), and `serial-raw`/`hid-raw` generic passthrough. Adding a new device type = one reader + one factory + one DI registration. Serial/HID readers are Windows-only (`#if WINDOWS`); a `SimulatedDeviceReader` runs on non-Windows and in dry-run so the full pipeline is testable without hardware.

### Device Reading Schema

```json
{
  "readingId": "8a1f...",
  "station": "shipping-pc-01",
  "host": "SHIPPING-PC-01",
  "deviceId": "scale-shipping",
  "deviceType": "serial-scale",
  "source": { "connection": "serial", "port": "COM3" },
  "timestamp": "2026-06-06T17:55:05Z",
  "value": 2.5,
  "unit": "kg",
  "stable": true,
  "status": "ok",
  "raw": "ST,+00002.50 kg"
}
```

---

## (Historical) Print-only plan

A Windows Service that long-polls AWS SQS queues for print jobs and routes them to local printers. One queue per machine, one service per machine.

## Architecture

```
cloudprint/
├── src/
│   └── CloudPrint.Service/           # .NET 8 Worker Service
│       ├── Program.cs                 # Host setup, Serilog, DI
│       ├── Worker.cs                  # SQS long-poll loop
│       ├── Printing/
│       │   ├── IRawPrinter.cs         # Raw byte passthrough (ZPL, etc.)
│       │   ├── RawPrinter.cs          # Win32 raw print implementation
│       │   ├── IDocumentPrinter.cs    # PDF/image via PrintDocument API
│       │   ├── DocumentPrinter.cs     # System.Drawing print implementation
│       │   └── PrintRouter.cs         # Routes job to correct printer by contentType
│       ├── Sqs/
│       │   ├── SqsPollingService.cs   # Long-poll loop, message lifecycle
│       │   └── PrintJobMessage.cs     # Message schema / deserialization
│       ├── FileHandling/
│       │   └── FileDownloader.cs      # Download file from signed/public URL
│       └── Configuration/
│           └── CloudPrintOptions.cs   # Strongly-typed config (queue, AWS, etc.)
├── scripts/
│   └── install.ps1                    # Interactive installer + service registration
├── .github/
│   └── workflows/
│       └── release.yml                # Build, publish, create GitHub Release on tag
├── PLAN.md
├── README.md
└── LICENSE
```

## SQS Message Schema

```json
{
  "fileUrl": "https://s3.amazonaws.com/bucket/label.zpl",
  "printerName": "Zebra_ZP500",
  "contentType": "application/vnd.zebra.zpl",
  "copies": 1,
  "metadata": {}
}
```

### Supported Content Types

| contentType | Handling |
|---|---|
| `application/vnd.zebra.zpl` | Raw passthrough to printer |
| `text/plain` | Raw passthrough to printer |
| `application/pdf` | Print via system PDF rendering |
| `image/png`, `image/jpeg` | Print via System.Drawing.PrintDocument |

## Implementation Phases

### Phase 1: Project Scaffolding
- [ ] Initialize .NET 8 Worker Service project
- [ ] Add NuGet packages: AWSSDK.SQS, Serilog, Serilog.Sinks.File, Serilog.Sinks.Console
- [ ] Set up Program.cs with host builder, Serilog, DI registration
- [ ] Define CloudPrintOptions and appsettings.json structure
- [ ] Configure Windows Service hosting (Microsoft.Extensions.Hosting.WindowsServices)

### Phase 2: SQS Integration
- [ ] Implement SqsPollingService as BackgroundService
- [ ] Long-poll with WaitTimeSeconds=20 (SQS max)
- [ ] Deserialize messages into PrintJobMessage
- [ ] Handle message lifecycle: receive -> process -> delete on success
- [ ] On failure: log error, let visibility timeout expire (message returns to queue, eventually DLQ)
- [ ] Graceful shutdown via CancellationToken

### Phase 3: File Download
- [ ] Implement FileDownloader using HttpClient
- [ ] Download file from signed/public URL to temp location
- [ ] Clean up temp files after print attempt
- [ ] Handle download failures with appropriate logging

### Phase 4: Printing
- [ ] Implement RawPrinter using Win32 Winspool APIs (P/Invoke)
  - OpenPrinter, StartDocPrinter, StartPagePrinter, WritePrinter, EndPagePrinter, EndDocPrinter, ClosePrinter
  - Used for ZPL and plain text passthrough
- [ ] Implement DocumentPrinter using System.Drawing.Printing.PrintDocument
  - PDF rendering via system print pipeline
  - Image printing with default page settings
- [ ] Implement PrintRouter to dispatch by contentType
- [ ] Validate printer name exists locally before attempting print
  - If printer not found: log error, leave message for DLQ

### Phase 5: Install Script (install.ps1)
- [ ] Accept `-Uninstall` switch for removal
- [ ] Download latest release zip from GitHub Releases API
- [ ] Extract to Program Files\CloudPrint
- [ ] Prompt for AWS Access Key, Secret Key, Region
- [ ] Enumerate and display local printers for verification
- [ ] Create SQS queue if it doesn't exist (named `cloudprint-{HOSTNAME}`)
- [ ] Write appsettings.json with configured values
- [ ] Register Windows Service via `New-Service`
- [ ] Start the service
- [ ] On reinstall: stop existing service, overwrite files, re-prompt, restart

### Phase 6: GitHub Actions Release Pipeline
- [ ] Trigger on tag push matching `v*`
- [ ] Build as self-contained single-file for `win-x64`
- [ ] Bundle service binary + install.ps1 into zip
- [ ] Create GitHub Release with zip artifact
- [ ] Include a standalone `install.ps1` as a separate release asset that bootstraps the full install

### Phase 7: Polish
- [ ] Serilog rolling file sink configuration
- [ ] Structured logging throughout
- [ ] README with install one-liner, config reference, message schema docs
- [ ] Handle multiple copies (loop print N times)

## Key Design Decisions

1. **Raw print for ZPL**: No rendering/interpretation. The service is a passthrough — ZPL transformation happens upstream.
2. **Message stays in queue on failure**: Don't delete messages that fail to print. Visibility timeout handles retry, DLQ catches persistent failures.
3. **One queue per machine**: Queue named `cloudprint-{HOSTNAME}`. Service creates the queue on install.
4. **Reinstall to reconfigure**: No separate config UI for MVP. Re-run install.ps1 to change settings.
5. **Self-contained publish**: No .NET runtime dependency on target machines.
6. **One-liner install**: `irm .../install.ps1 | iex` for near-one-click experience.

## Configuration (appsettings.json)

```json
{
  "CloudPrint": {
    "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789/cloudprint-HOSTNAME",
    "Region": "us-east-1",
    "AwsAccessKeyId": "AKIA...",
    "AwsSecretAccessKey": "...",
    "MaxConcurrentJobs": 1,
    "PollingIntervalSeconds": 0
  },
  "Serilog": {
    "WriteTo": [
      { "Name": "File", "Args": { "path": "logs/cloudprint-.log", "rollingInterval": "Day" } }
    ]
  }
}
```

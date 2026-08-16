# Settings Reference

CloudPrint is configured by a single `appsettings.json` with two sections: `CloudPrint` (everything) and `Serilog` (logging). This page is the key-by-key dictionary; the [README](../README.md) explains the concepts behind them.

## Where the file lives

| Context | Path |
|---|---|
| Installed service | `C:\Program Files\CloudPrint\appsettings.json` (next to `CloudPrint.Service.exe`) |
| Development (`dotnet run`) | `src/CloudPrint.Service/appsettings.json`, plus `appsettings.Development.json` overrides when `DOTNET_ENVIRONMENT=Development` |

The installed file's ACL is restricted to **Administrators + SYSTEM** (it contains AWS credentials), so an elevated shell/editor is required to read or change it.

## How settings are loaded

- Standard .NET configuration chain: `appsettings.json` → `appsettings.{Environment}.json` → environment variables (`CloudPrint__PdfRenderDpi=203` — double underscore per level) → command-line args. Env vars are mainly useful in development; for the installed service, edit the JSON.
- Keys are **case-insensitive**; **unknown keys are silently ignored** (the samples' `_README` key relies on this — but it also means a misspelled optional key is not an error, just ignored, so double-check spelling).
- Settings are read **once at service startup**. There is no hot reload — **restart the service** (`Restart-Service CloudPrint`) after any edit.
- The **configurator owns this file**: on every save it rewrites the whole document, including a regenerated `Serilog` section (log level derived from `DumpPayloads`). Hand edits survive until the next configurator save; hand edits to `Serilog` in particular will be overwritten.

Fail-fast behavior at startup (check the log if the service won't start):

- Unknown `Transport` → refuses to start.
- `Transport: "sqs"` with no `Printers[]` and no legacy `QueueUrl`+`PrinterName` → refuses to start.
- `Transport: "http"` with no `PrinterName` → refuses to start.

On successful startup the service logs its resolved config, e.g. `CloudPrint configured for 2 SQS lane(s): Zebra_ZP500 (203 DPI, PhysicalPage, paper=4x6, B/W), …` — use this to confirm an edit took effect.

## `CloudPrint` section

### Core

| Key | Type | Default | Description |
|---|---|---|---|
| `Transport` | string | `sqs` | Inbound print transport: `sqs` or `http` (case-insensitive). Device telemetry is independent of this. |

### AWS / SQS transport

| Key | Type | Default | Description |
|---|---|---|---|
| `Region` | string | `us-east-1` | AWS region for all SQS access (print lanes and device outputs share it). |
| `AwsAccessKeyId` | string | `""` | IAM access key (scoped to `cloudprint-*` queues; see README IAM setup). |
| `AwsSecretAccessKey` | string | `""` | IAM secret key. |
| `VisibilityTimeoutSeconds` | int | `300` | SQS visibility timeout while a job is being printed. Global — not overridable per lane. |
| `Printers` | array | `[]` | One entry per printer lane; see below. |
| `QueueUrl`, `PrinterName` | string | `""` | **Legacy single-printer form**: when `Printers` is empty, a non-empty pair auto-promotes to a single lane at startup. |

Each `Printers[]` entry:

| Key | Type | Required | Description |
|---|---|---|---|
| `PrinterName` | string | yes | Exact Windows printer queue name. |
| `QueueUrl` | string | yes | SQS queue URL bound to that printer. |
| `PdfRenderDpi` | int | no | Per-lane override; unset (or ≤ 0) falls back to the top-level value. |
| `PdfFitMode` | string | no | Per-lane override; unset/empty falls back to the top-level value. |
| `PdfMonochrome` | bool | no | Per-lane override; **omit** to inherit — an explicit `false` sticks. |
| `PdfPaperSize` | string | no | Per-lane override; unset/empty falls back to the top-level value. |

### HTTP transport

Single-printer only; uses the top-level `PrinterName`.

| Key | Type | Default | Description |
|---|---|---|---|
| `ApiUrl` | string | `""` | Long-poll GET endpoint for jobs (`?timeout=N` is appended). |
| `AckUrl` | string | `""` | Base URL for PATCH acknowledgements (`/{jobId}` is appended). |
| `ApiHeaderName` | string | `X-Api-Key` | Auth header name sent on every request. |
| `ApiHeaderValue` | string | `""` | Auth header value; header omitted when name or value is blank. |
| `HttpPollTimeoutSeconds` | int | `30` | Server-side long-poll timeout; the client HTTP timeout is this + 10 s. |

### PDF printing (global defaults; lanes may override)

| Key | Type | Default | Description |
|---|---|---|---|
| `PdfRenderDpi` | int | `300` | Rasterization DPI. Match the printer's native resolution — `203` for direct thermal, `300` office, `600` high fidelity. |
| `PdfFitMode` | string | `PhysicalPage` | `PhysicalPage` prints edge-to-edge ignoring driver margins (thermal/label printers); `Margins` fits within the driver-reported printable area (office printers). Case-insensitive. **Any other value currently behaves as `Margins` without an error** — spell it exactly. |
| `PdfMonochrome` | bool | `false` | Render 1-bit black/white (crisper barcodes on thermal printers). |
| `PdfPaperSize` | string | `""` | Stock loaded in the printer: `4x6`, `2x2`, `Letter`, `A4`, or any `WxH` in inches. Empty = the Windows queue's driver-default paper. |

### Debugging

| Key | Type | Default | Description |
|---|---|---|---|
| `DumpPayloads` | bool | `false` | Dump each job's JSON message + resolved file to `DumpPath`. The configurator also switches the Serilog level to `Debug` when enabling this. |
| `DumpPath` | string | `C:\ProgramData\CloudPrint\dumps` | Dump directory (created on demand). |

### Device telemetry (outbound) and device commands (inbound)

Off unless `Devices[]` is non-empty; runs regardless of `Transport`. Full semantics, drivers, the reading JSON format and the command message format are in the [README Device Telemetry section](../README.md#device-telemetry); a complete multi-device example is [`samples/appsettings.sample.json`](../samples/appsettings.sample.json).

| Key | Type | Default | Description |
|---|---|---|---|
| `Station` | string | `""` | Logical workstation id stamped on readings; blank → machine name. |
| `DevicePollIntervalMs` | int | `500` | Default poll cadence for `request`/`interval` devices. |
| `DeviceStableOnly` | bool | `true` | Default: publish only readings the device marks stable (raw frames count as stable). |
| `DeviceHeartbeatSeconds` | int | `0` | Default: re-publish an unchanged reading every N s (0 = off). |
| `DeviceStaleAfterSeconds` | int | `0` | Default: publish a `stale` event after N s without data (0 = off). |
| `DeviceCommandQueueUrl` | string | `""` | SQS queue of cloud → device commands for this station (empty = off). Created by the configurator as `cloudprint-{station}-device-commands`. |
| `Devices` | array | `[]` | One entry per device; see below. |

Each `Devices[]` entry (defaults in parentheses):

| Key | Applies to | Description |
|---|---|---|
| `Name` | all | **Required.** Unique id; used as `deviceId`, command target and log tag. Entries without a name are skipped. |
| `Type` | all | `serial-raw`, `serial-scale`, `hid-raw`, `hid-scale`, `tcp-raw`, `tcp-scale` (`serial-scale` when omitted; the configurator defaults new devices to `serial-raw`). |
| `Protocol` | *-scale | Parser selector: `mt-sics` (default) or `generic`. Currently both select the same tolerant parser. |
| `Station` | all | Per-device override of the top-level `Station`. |
| `ComPort` | serial | `COM3`; or `auto` / `auto:SERIAL` to resolve by `Vid`/`Pid` (and adapter serial) at open time. |
| `BaudRate`, `Parity`, `DataBits`, `StopBits` | serial | Port settings (`9600`, `None`, `8`, `1`). |
| `DtrEnable`, `RtsEnable` | serial | Assert DTR / RTS on open (`false`). |
| `Host`, `Port`, `ConnectTimeoutMs` | tcp | Device address; the device is the TCP server (`5000` ms connect timeout). |
| `FrameMode` | serial, tcp | `line` (default), `delimited`, `idle`. |
| `LineEnding` | serial, tcp | `crlf` (default), `lf`, `cr`, or an escaped literal (`<ETX><CR>`, `\x03\r`). Line-mode terminator and default command terminator. |
| `FrameStart`, `FrameEnd` | serial, tcp | Delimited mode boundaries (escaped, e.g. `<STX>` / `<ETX>`); `FrameEnd` falls back to `LineEnding`. Delimiters are kept in the frame. |
| `IdleGapMs` | serial, tcp | Idle mode: silence that closes a frame (`150`). |
| `MaxFrameBytes` | serial, tcp | Safety cap on frame size (`4096`); oversized frames are emitted, not dropped. |
| `ReadTimeoutMs` | serial, tcp, hid | How long one read cycle waits for data (`2000`). |
| `Encoding` | serial, tcp | `ascii` (default), `utf8`, `latin1`. |
| `RequestCommand` | serial, tcp | Command sent each poll in `request`/`interval` mode. Escapes: `<STX>`, `<ETX>`, `<CR>`, `<LF>`, `<ENQ>`, `\xHH`, `\uHHHH`, `\r`, `\n`, `\e`. |
| `InitCommands` | serial, tcp | Commands sent once on connect (same escapes). |
| `CommandTerminator` | serial, tcp | Appended to every command: omitted = `LineEnding`; `none` = nothing; else escaped literal. |
| `Vid`, `Pid` | hid, serial `auto` | USB ids (decimal). Discover with `cloudprint list-devices` / `--json`. |
| `HidReportId`, `Hid*Offset`, `HidWeightSize` | hid | Manual report-layout overrides for non-conformant HID scales. |
| `Pattern` | *-raw | Regex with named groups `value`/`unit`/`stable`; omit to forward raw frames. |
| `PollMode` | all | `stream` (default), `request`, `interval`. |
| `PollIntervalMs` | all | Per-device override of `DevicePollIntervalMs`. |
| `StableOnly` | all | Per-device override of `DeviceStableOnly`; omit to inherit. |
| `HeartbeatSeconds`, `StaleAfterSeconds` | all | Per-device overrides of the top-level defaults; `0` = off. |
| `Output.Transport` | all | `sqs` (default) or `http`, chosen per device. |
| `Output.QueueUrl` | sqs | Target SQS queue. |
| `Output.WebhookUrl`, `Output.HeaderName`, `Output.HeaderValue` | http | HTTPS webhook (SSRF-validated) + optional auth header. |

## `Serilog` section

Generated by the configurator — don't hand-tune unless you accept it being regenerated on the next configurator save.

- `MinimumLevel.Default`: `Information` normally, `Debug` when `DumpPayloads` is on.
- File sink: `C:\ProgramData\CloudPrint\logs\cloudprint-.log`, rolling daily (`cloudprint-YYYYMMDD.log`), 30-day retention.

## Changing settings from PowerShell

All of these need an **elevated** PowerShell (the file ACL and `Restart-Service` both require it). Every change requires the restart to take effect. Note that `ConvertTo-Json` reformats the file and strips any comments; `-Depth 10` is required or nested arrays get flattened to strings.

Show the current config:

```powershell
(Get-Content "$env:ProgramFiles\CloudPrint\appsettings.json" -Raw | ConvertFrom-Json).CloudPrint
```

Change a top-level setting and restart:

```powershell
$p="$env:ProgramFiles\CloudPrint\appsettings.json"; $j=Get-Content $p -Raw|ConvertFrom-Json; $j.CloudPrint.PdfRenderDpi=203; $j|ConvertTo-Json -Depth 10|Set-Content $p -Encoding UTF8; Restart-Service CloudPrint
```

Change a setting on one printer lane:

```powershell
$p="$env:ProgramFiles\CloudPrint\appsettings.json"; $j=Get-Content $p -Raw|ConvertFrom-Json; ($j.CloudPrint.Printers | Where-Object PrinterName -eq 'Zebra_ZP500').PdfFitMode='PhysicalPage'; $j|ConvertTo-Json -Depth 10|Set-Content $p -Encoding UTF8; Restart-Service CloudPrint
```

Set a key that may not exist in the file yet (plain `$j.CloudPrint.X = ...` throws if the property is absent; `Add-Member -Force` handles both cases):

```powershell
$p="$env:ProgramFiles\CloudPrint\appsettings.json"; $j=Get-Content $p -Raw|ConvertFrom-Json; $j.CloudPrint|Add-Member -NotePropertyName DumpPayloads -NotePropertyValue $true -Force; $j|ConvertTo-Json -Depth 10|Set-Content $p -Encoding UTF8; Restart-Service CloudPrint
```

Verify the change landed (the startup line echoes the resolved lanes/devices):

```powershell
Get-Content "$env:ProgramData\CloudPrint\logs\cloudprint-$(Get-Date -Format yyyyMMdd).log" -Tail 30
```

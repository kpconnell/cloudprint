# CloudPrint

A Windows Service that bridges the cloud and the devices physically connected to a workstation — in **both** directions:

- **Printing (cloud → device):** receives print jobs from the cloud and routes them to local printers (shipping labels, receipts, documents).
- **Device telemetry (device → cloud):** reads data from locally connected devices (USB/serial scales) and publishes it outbound, so back-end systems (WMS/shipping) get live readings without anyone keying them in.

Both directions share the same transports and credentials, and either capability can run on its own. Device telemetry is opt-in (configure `Devices[]`) and runs independently of the print transport.

**Transports**
- **AWS SQS** — polls SQS queues for print jobs (auto-provisioned per machine/printer; multiple printers per machine supported) and publishes device readings via `SendMessage`.
- **HTTP API** — long-polls your own API for print jobs (single printer; bring your own server); device readings can POST to an HTTPS webhook.

## Quick Install

Download [`CloudPrint-Setup.exe`](https://github.com/kpconnell/cloudprint/releases/latest/download/CloudPrint-Setup.exe) from the latest release and run it (it prompts for elevation).

The installer:
1. Extracts the service binary to `C:\Program Files\CloudPrint` and registers an Add/Remove Programs entry
2. Opens the configurator, where you choose the transport (SQS or HTTP) and enter credentials (AWS keys or API URL + key)
3. Lets you add one or more printers — and, optionally, USB/serial devices (scales) for telemetry — with live detection and test buttons
4. Per printer, optionally customize PDF render DPI / fit mode (defaults are fine for most printers)
5. On **Install**, auto-creates any missing SQS queues, writes the locked-down config, and registers + starts the Windows Service

To reconfigure later, run `CloudPrint.exe` from `C:\Program Files\CloudPrint` (or re-run the setup exe) — it loads the existing configuration so you can edit and re-install. Missing queues are created; queues for removed printers/devices are left in place (delete them in the AWS console if you no longer want them).

## How It Works

1. CloudPrint long-polls for jobs. **SQS**: one polling loop per configured printer, each bound to its own queue. **HTTP**: a single long-poll loop against your API.
2. The print content is resolved — either downloaded from the `fileUrl` or read directly from the inline `content` field
3. The file is validated (magic bytes check against claimed content type)
4. The file is sent to the printer the queue (or HTTP transport) is bound to
5. On success, the job is acknowledged (deleted from SQS, or PATCH'd as completed via HTTP)
6. On failure, the job is retried (SQS visibility timeout / HTTP server-side retry)

## Job Format

Jobs are JSON with the same shape regardless of transport:

```json
{
  "fileUrl": "https://s3.amazonaws.com/my-bucket/label.zpl",
  "contentType": "application/vnd.zebra.zpl",
  "copies": 1,
  "metadata": {}
}
```

### Fields

| Field | Required | Description |
|---|---|---|
| `fileUrl` | One of `fileUrl` or `content` required | HTTPS URL to the file (signed or public) |
| `content` | One of `fileUrl` or `content` required | Inline print content (see below) |
| `printerName` | No | **HTTP transport only** — overrides the configured printer. **Ignored on SQS** (the queue → printer binding is the contract; a mismatch is logged as a warning). |
| `contentType` | Yes | MIME type determining how the file is printed |
| `copies` | No | Number of copies (default: 1) |
| `metadata` | No | Arbitrary key-value pairs for your own tracking |

For SQS, send this as the message body. For HTTP, your API returns this as the response body (with an additional `id` field).

### Inline Content

For small print jobs that fit within SQS message size limits (256 KB), you can embed the content directly in the `content` field instead of hosting a file and providing a `fileUrl`.

- **Text-based content types** (`application/vnd.zebra.zpl`, `text/plain`): pass the content as a plain string
- **Binary content types** (images): pass the content as a base64-encoded string

If both `content` and `fileUrl` are provided, `content` takes priority and no download is performed.

**ZPL example:**
```json
{
  "contentType": "application/vnd.zebra.zpl",
  "content": "^XA^FO50,50^ADN,36,20^FDHello World^FS^XZ"
}
```

**Plain text example:**
```json
{
  "contentType": "text/plain",
  "content": "Order #12345\nShip to: 123 Main St\n"
}
```

**Base64 image example:**
```json
{
  "contentType": "image/png",
  "content": "iVBORw0KGgoAAAANSUhEUgAA..."
}
```

All validation (magic bytes, ZPL command blocking) applies equally to inline content.

### Supported Content Types

| Content Type | Handling | Validated By |
|---|---|---|
| `application/vnd.zebra.zpl` | Raw passthrough (ZPL printers) | ZPL header (`^XA`, `~`, `CT~~`) |
| `text/plain` | Raw passthrough | Any content accepted |
| `image/png` | Printed as image | PNG magic bytes |
| `image/jpeg` | Printed as image | JPEG magic bytes |
| `image/bmp` | Printed as image | BMP magic bytes |
| `image/gif` | Printed as image | GIF magic bytes |
| `image/tiff` | Printed as image | TIFF magic bytes |
| `application/pdf` | Rasterized via PDFium and printed | PDF magic bytes (`%PDF`) |

### PDF Print Settings

PDFs are rasterized to a bitmap, then sent through the Windows printing pipeline. Two settings tune this:

| Setting | Default | Notes |
|---|---|---|
| `PdfRenderDpi` | `300` | Rasterization resolution. `203` for thermal label printers, `300` for office, `600` for high-fidelity reports. Higher DPI = more memory per page. |
| `PdfFitMode` | `PhysicalPage` | `PhysicalPage` prints edge-to-edge ignoring margins (thermal/label printers). `Margins` fits within the driver-reported page margins (office printers). |

**Per-printer overrides (SQS):** Each entry in `Printers[]` may set its own `PdfRenderDpi` / `PdfFitMode`. When a lane omits a value, it falls back to the top-level default. This lets a thermal label printer (`203` / `PhysicalPage`) and an office laser (`300` / `Margins`) coexist on the same machine. The configurator exposes these per printer.

To change settings later, re-run the configurator, or edit `appsettings.json` and restart the service. See [docs/SETTINGS.md](docs/SETTINGS.md) for the full settings reference and PowerShell one-liners for editing the installed config.

## Transports

CloudPrint supports two transport modes, selected during install.

### AWS SQS

The default transport. The installer auto-creates an SQS queue pair per machine/printer. Multiple printers per machine are supported — each printer gets its own queue, with a per-lane `PdfRenderDpi`/`PdfFitMode` override (top-level values are the fallback). On reconfigure, missing queues are created; existing queues are reused.

```json
{
  "CloudPrint": {
    "Transport": "sqs",
    "Region": "us-east-2",
    "AwsAccessKeyId": "AKIA...",
    "AwsSecretAccessKey": "...",
    "VisibilityTimeoutSeconds": 300,
    "PdfRenderDpi": 300,
    "PdfFitMode": "Margins",
    "Printers": [
      {
        "PrinterName": "Zebra_ZP500",
        "QueueUrl": "https://sqs.us-east-2.amazonaws.com/123456789/cloudprint-warehouse-pc1-zebra-zp500",
        "PdfRenderDpi": 203,
        "PdfFitMode": "PhysicalPage"
      },
      {
        "PrinterName": "HP_LaserJet_Pro",
        "QueueUrl": "https://sqs.us-east-2.amazonaws.com/123456789/cloudprint-warehouse-pc1-hp-laserjet-pro"
      }
    ]
  }
}
```

Legacy single-printer configs (top-level `QueueUrl` + `PrinterName`, no `Printers` array) continue to work without reinstalling — they auto-promote to a single lane at startup.

When the job-level `printerName` field is set on a job pulled from a multi-printer queue, it is ignored with a warning: the queue → printer binding is the contract.

#### Discovering printers from the cloud side

Each queue is tagged on creation:

| Tag | Value |
|---|---|
| `Application` | `cloudprint` |
| `Hostname` | `warehouse-pc1` (sanitized hostname) |
| `PrinterName` | `zebra-zp500` (sanitized printer name) |

Senders enumerate available printers with `ListQueues(QueueNamePrefix=cloudprint-)` followed by `ListQueueTags` per queue URL. Tag values are sanitized the same way as queue names (lowercase alphanumeric and dashes) — SQS rejects most punctuation in tag values, and printer names like `HP LaserJet (Copy 1)` would otherwise fail queue creation.

#### Queue Naming

Each machine/printer gets its own queue pair:

| Queue | Example | Purpose |
|---|---|---|
| Main | `cloudprint-{hostname}-{printer}` | Print jobs |
| DLQ | `cloudprint-{hostname}-{printer}-dlq` | Failed jobs (after 5 retries) |

The hostname and printer name are lowercased, runs of non-alphanumeric characters collapse to a single hyphen, and leading/trailing hyphens are trimmed. For example, a machine `WAREHOUSE-PC1` with printer `Zebra ZP500` produces:
- `cloudprint-warehouse-pc1-zebra-zp500`
- `cloudprint-warehouse-pc1-zebra-zp500-dlq`

Queue names are capped at 76 characters so the `-dlq` suffix on the dead-letter queue stays within SQS's 80-character limit. Each printer on a machine gets its own queue pair.

#### IAM Setup

CloudPrint needs an IAM user with narrowly scoped permissions. The credentials can only access `cloudprint-*` SQS queues and nothing else.

**1. Create the IAM Policy**

In the [IAM Console](https://console.aws.amazon.com/iam/), go to **Policies** → **Create policy** → **JSON** tab:

```json
{
    "Version": "2012-10-17",
    "Statement": [
        {
            "Sid": "CloudPrintSQSAccess",
            "Effect": "Allow",
            "Action": [
                "sqs:CreateQueue",
                "sqs:DeleteQueue",
                "sqs:TagQueue",
                "sqs:SetQueueAttributes",
                "sqs:GetQueueAttributes",
                "sqs:GetQueueUrl",
                "sqs:ReceiveMessage",
                "sqs:DeleteMessage",
                "sqs:SendMessage"
            ],
            "Resource": "arn:aws:sqs:*:*:cloudprint-*"
        },
        {
            "Sid": "CloudPrintQueueDiscovery",
            "Effect": "Allow",
            "Action": "sqs:ListQueues",
            "Resource": "*"
        },
        {
            "Sid": "CloudPrintCredentialVerification",
            "Effect": "Allow",
            "Action": "sts:GetCallerIdentity",
            "Resource": "*"
        }
    ]
}
```

Name the policy `CloudPrintSQSAccess`.

| Action | Why |
|--------|-----|
| `sqs:CreateQueue` | Installer auto-creates main queue + dead-letter queue per printer |
| `sqs:DeleteQueue` | Lets the configurator remove `cloudprint-*` queues it provisioned |
| `sqs:TagQueue` | Stamps each queue with `Application`/`Hostname`/`PrinterName` tags for discovery |
| `sqs:SetQueueAttributes` | Sets the redrive policy (DLQ) on existing queues |
| `sqs:GetQueueAttributes` | Reads the DLQ ARN to wire up the redrive policy |
| `sqs:GetQueueUrl` | Looks up the queue URL when it already exists |
| `sqs:ListQueues` | Finds existing `cloudprint-{hostname}-*` queues on reconfigure (no resource scoping for list ops) |
| `sqs:ReceiveMessage` | Long-polls the queue for print jobs |
| `sqs:DeleteMessage` | Removes a message after successful printing |
| `sqs:SendMessage` | Publishes device telemetry readings to an output queue (device telemetry only) |
| `sts:GetCallerIdentity` | Validates credentials during installation |

> Note: `ListQueueTags` is not in this policy — that permission belongs to whatever **sends** print jobs (so it can discover what printers are available), not the CloudPrint service itself.

**2. Create the IAM User**

1. **Users** → **Create user** → name it `cloudprint-service`
2. Do **not** enable console access
3. Attach the `CloudPrintSQSAccess` policy
4. **Security credentials** → **Create access key** → select **Application running outside AWS**
5. Copy the **Access Key ID** and **Secret Access Key**

Provide these during the CloudPrint installer. You do not need to create SQS queues manually — the installer handles that.

<details>
<summary>AWS CLI alternative</summary>

```bash
# Create the policy
aws iam create-policy \
  --policy-name CloudPrintSQSAccess \
  --policy-document file://docs/cloudprint-iam-policy.json

# Create the user (no console access)
aws iam create-user --user-name cloudprint-service

# Attach the policy (replace ACCOUNT_ID)
aws iam attach-user-policy \
  --user-name cloudprint-service \
  --policy-arn arn:aws:iam::ACCOUNT_ID:policy/CloudPrintSQSAccess

# Create access keys (secret is only shown once)
aws iam create-access-key --user-name cloudprint-service
```
</details>

For credential rotation and multi-machine setups, see the full [AWS Credentials Guide](docs/aws-credentials.md).

### HTTP API

For in-house APIs that serve print jobs directly. CloudPrint long-polls your API for jobs and reports results via PATCH.

```json
{
  "CloudPrint": {
    "Transport": "http",
    "ApiUrl": "https://api.example.com/print-jobs/next",
    "AckUrl": "https://api.example.com/print-jobs",
    "ApiHeaderName": "X-Api-Key",
    "ApiHeaderValue": "your-api-key",
    "HttpPollTimeoutSeconds": 30,
    "PrinterName": "Zebra_ZP500"
  }
}
```

#### HTTP API Spec (for server implementors)

**Fetch next job:**

```
GET {ApiUrl}?timeout={seconds}
Headers: {ApiHeaderName}: {ApiHeaderValue}
```

Server behavior:
- Hold the connection open for up to `timeout` seconds (default 30)
- If a job becomes available, return it immediately with **200**
- If no job is available after the timeout, return **204 No Content**
- When returning a job, move it from `ready` → `sent` (locked for processing)
- If not acknowledged within a server-side timeout, return it to `ready`

**200 Response (file URL):**
```json
{
  "id": "job-123",
  "fileUrl": "https://s3.amazonaws.com/bucket/label.zpl",
  "contentType": "application/vnd.zebra.zpl",
  "copies": 1
}
```

**200 Response (inline content):**
```json
{
  "id": "job-124",
  "contentType": "application/vnd.zebra.zpl",
  "content": "^XA^FO50,50^FDOrder 12345^FS^XZ",
  "copies": 1
}
```

**204 Response:** empty body

**401 Response:** invalid API key

**Acknowledge job:**

```
PATCH {AckUrl}/{jobId}
Headers: {ApiHeaderName}: {ApiHeaderValue}
Content-Type: application/json
```

On success:
```json
{ "status": "completed" }
```

On failure:
```json
{ "status": "failed", "error": "File validation failed: ..." }
```

**Job lifecycle (server-side):**

```
ready → sent → completed
              → failed
         ↓ (timeout, no ack)
        ready (retry)
```

**Client behavior:**

```
loop:
    response = GET {ApiUrl}?timeout=30
    if 204: re-poll immediately (server already waited)
    if 200: download → validate → print → PATCH ack → re-poll
    if error: wait 5s, retry
```

No client-side poll interval is needed — the long-poll timeout IS the wait.

## Device Telemetry

Beyond printing, CloudPrint can read from devices physically or network-connected to the workstation and publish what they say to the cloud, and carry commands from the cloud back to the device. It is a **transport bridge**: every frame a device emits reaches the cloud verbatim (text + hex); parsing is an optional convenience for a few common scale formats. This is opt-in: it activates only when `Devices[]` is non-empty, and runs regardless of the print `Transport`.

Each configured device runs its own background loop: connect → read → publish, with automatic reconnect (capped backoff), de-duplication of repeated identical readings, and lifecycle events (`connected` with discovery metadata, `disconnected`, `stale`) so the cloud can see what it is talking to.

### Configuration

Add a `Devices` array under `CloudPrint`. See [`samples/appsettings.sample.json`](samples/appsettings.sample.json) for a complete, multi-device example (HID scale, serial scale, raw serial, Cubiscan over TCP, unknown USB-serial in discovery mode).

```json
{
  "CloudPrint": {
    "Station": "shipping-pc-01",
    "DeviceStaleAfterSeconds": 60,
    "DeviceCommandQueueUrl": "https://sqs.us-east-1.amazonaws.com/123/cloudprint-shipping-pc-01-device-commands",
    "Devices": [
      {
        "Name": "scale-shipping",
        "Type": "serial-raw",
        "ComPort": "COM3",
        "BaudRate": 9600, "Parity": "Even", "DataBits": 7,
        "LineEnding": "cr",
        "PollMode": "interval", "PollIntervalMs": 1000,
        "RequestCommand": "W",
        "Output": { "Transport": "sqs", "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123/cloudprint-shipping-pc-01-scale-shipping" }
      },
      {
        "Name": "cubiscan",
        "Type": "tcp-raw",
        "Host": "10.1.100.100", "Port": 1050,
        "FrameMode": "delimited", "FrameStart": "<STX>", "FrameEnd": "<ETX>",
        "InitCommands": [ "<STX>T<ETX>" ],
        "Output": { "Transport": "sqs", "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123/cloudprint-shipping-pc-01-cubiscan" }
      },
      {
        "Name": "scale-counter",
        "Type": "hid-scale",
        "Vid": 2919, "Pid": 21854,
        "Output": { "Transport": "http", "WebhookUrl": "https://wms.example.com/api/readings", "HeaderName": "X-Api-Key", "HeaderValue": "..." }
      }
    ]
  }
}
```

| Field | Applies to | Description |
|---|---|---|
| `Name` | all | Unique device id; used as `deviceId`, as the command target, and the log tag. Required. |
| `Type` | all | `serial-raw`, `serial-scale`, `hid-raw`, `hid-scale`, `tcp-raw`, `tcp-scale`. `*-raw` forwards every frame verbatim (start here); `*-scale` also tries to parse a weight. |
| `Station` | all | Per-device workstation id override (top-level `Station` is the default; blank → machine name). |
| `ComPort` | serial | `COM3`, or `auto` to find the port by `Vid`/`Pid` (survives COM renumbering; `auto:SERIAL` pins a specific adapter). |
| `BaudRate`, `Parity`, `DataBits`, `StopBits`, `DtrEnable`, `RtsEnable` | serial | Port settings (defaults 9600/None/8/1, DTR/RTS off). |
| `Host`, `Port`, `ConnectTimeoutMs` | tcp | The device is the TCP server (Cubiscan default port 1050; iDimension user-set). |
| `FrameMode` | serial, tcp | `line` (default; frames end with `LineEnding`, which is stripped), `delimited` (`FrameStart`..`FrameEnd`, e.g. `<STX>`..`<ETX>`, delimiters kept), or `idle` (discovery: whatever arrives is one frame after `IdleGapMs` of silence). |
| `LineEnding` | serial, tcp | `crlf` (default), `lf`, `cr`, or an escaped literal. Also the default command terminator. |
| `FrameStart`, `FrameEnd`, `IdleGapMs`, `MaxFrameBytes`, `ReadTimeoutMs` | serial, tcp | Framing details (defaults: none, none, 150, 4096, 2000). |
| `Encoding` | serial, tcp | `ascii` (default), `utf8`, or `latin1` (byte-for-byte text). `rawHex` is always lossless regardless. |
| `RequestCommand` | serial, tcp | Command sent each cycle in `request`/`interval` mode. Escapes allowed: `<STX>M<ETX>`, `\x02`, `<ENQ>`, `\r`. |
| `InitCommands` | serial, tcp | Commands sent once on connect (e.g. `Z` to zero, `<STX>T<ETX>` to ping a Cubiscan). |
| `CommandTerminator` | serial, tcp | Appended to every command: omitted = same as `LineEnding`; `none` = nothing (bare `W` for Toledo scales); or an escaped literal. |
| `Vid`, `Pid` | hid, serial `auto` | USB vendor/product id (decimal). Find them with `cloudprint list-devices`. |
| `Hid*Offset` / `HidReportId` / `HidWeightSize` | hid | Optional manual report-layout overrides for non-conformant HID scales. |
| `Pattern` | raw | Regex with named groups `value`, `unit`, `stable` for `*-raw` passthrough. Omit to forward the raw frame only. |
| `PollMode` | all | `stream` (read continuously), `request`, or `interval` (default `stream`). |
| `PollIntervalMs` | all | Poll cadence for request/interval modes (top-level `DevicePollIntervalMs` is the default). |
| `StableOnly` | all | Publish only readings the device marks stable (top-level `DeviceStableOnly` is the default; `true`). Raw frames count as stable. |
| `HeartbeatSeconds` | all | Re-publish an unchanged reading every N s so the cloud can tell "still 2.5 kg" from silence (top-level `DeviceHeartbeatSeconds`; 0 = off). |
| `StaleAfterSeconds` | all | Publish a `stale` event after N s without any data (top-level `DeviceStaleAfterSeconds`; 0 = off). |
| `Output.Transport` | all | `sqs` or `http` — chosen per device. |
| `Output.QueueUrl` | sqs | Target SQS queue (requires `sqs:SendMessage`; a `cloudprint-*` name is already in IAM scope). |
| `Output.WebhookUrl`, `Output.HeaderName`, `Output.HeaderValue` | http | HTTPS webhook (validated for SSRF) and optional auth header. |

Top-level `DeviceCommandQueueUrl` names one SQS queue per station for cloud → device commands (below).

### Reading format

Each reading is published as JSON:

```json
{
  "readingId": "8a1f...",
  "station": "shipping-pc-01",
  "host": "SHIPPING-PC-01",
  "deviceId": "cubiscan",
  "deviceType": "tcp-raw",
  "source": { "connection": "tcp", "host": "10.1.100.100", "tcpPort": 1050 },
  "timestamp": "2026-08-15T17:55:05.123Z",
  "value": null,
  "unit": null,
  "stable": true,
  "status": "ok",
  "raw": "\u0002MAH000000,L009.8,W007.2,H003.5,E,K001.25,D000.00,E,F0138,D\u0003",
  "rawHex": "024D414830303030303...03",
  "metadata": { "commandId": "c-42" }
}
```

`raw` is the frame as text (control characters JSON-escaped); `rawHex` is the exact bytes. `status` is `ok | motion | overload | underload | zero | error | unparsed` for measurements (`unparsed` = a `*-scale` parser made nothing of the frame; it is forwarded anyway), or a lifecycle event: `connected` (metadata carries HID product/manufacturer/serial/usages/report descriptor, COM friendly name/VID/PID, or TCP endpoint), `disconnected`, `stale`, `command-sent`, `command-failed`. HID readings carry `source.vid`/`source.pid`/`source.product`; readings that arrive within a command's reply window carry `metadata.commandId`.

### Cloud → device commands

Send a JSON message to the station's `DeviceCommandQueueUrl`:

```json
{ "id": "c-42", "device": "cubiscan", "command": "<STX>M<ETX>", "replyWindowMs": 8000, "metadata": { "orderId": "SO-1001" } }
```

| Field | Description |
|---|---|
| `device` (or `deviceId`) | Target `Devices[].Name`. Unknown names are logged and dropped. |
| `command` | Text with the same escapes as config; the device's command terminator is appended unless `terminator` says otherwise (`"none"`, or an escaped literal). |
| `bytesBase64` | Exact bytes instead of `command` (HID output reports, binary protocols). |
| `replyWindowMs` | Frames arriving within this window (default 5000) get `metadata.commandId` = `id`. |
| `metadata` | Copied onto the `command-sent` event so the cloud can correlate. |

The service publishes `command-sent` (with the bytes actually written) or `command-failed` (device not connected, invalid message) on the device's normal output. Commands run as they arrive — they are not queued behind a blocked read.

### Supported devices & drivers

| Type | Connection | Driver |
|---|---|---|
| `serial-raw` / `serial-scale` | RS-232 / USB virtual COM port. Most "USB" bench scales (Brecknell, Detecto, Ohaus, Mettler PS/BC in serial mode) and Cubiscans over serial/USB. | Vendor **VCP driver** where needed (FTDI and CDC-ACM install themselves; CP210x/CH340 may need the vendor installer; Prolific clones fail after Windows updates). |
| `hid-scale` / `hid-raw` | USB HID POS scale (usage page `0x8D`): Fairbanks Ultegra, Mettler PS/BC in HIDPOS mode, DYMO, Stamps.com/Endicia, Rice Lake BenchPro. | **No driver** — in-box Windows HID. Composite devices: the scale collection is preferred automatically. Must not be exclusively claimed by another app. |
| `tcp-raw` / `tcp-scale` | Device that listens on TCP: Cubiscan (`:1050`), Rice Lake iDimension (Cubiscan or QubeVu protocol), Mettler TLD250, serial-device servers. | None. |

Hot-plug is handled by reconnect-with-backoff (the service polls and re-opens the device), so unplug/replug recovers automatically. Keyboard-wedge devices (a scale that "types") cannot be read by a Windows service — switch the device to HID-POS, serial, or TCP.

### Discovery recipe (unknown device)

1. `cloudprint list-devices` — COM ports with their USB identity/friendly name and HID devices with usage pages (`[HID scale]` marks the weighing-device usage page).
2. Add the device as `serial-raw` with `FrameMode: idle` (or `hid-raw`, or `tcp-raw`), no request command; watch the `connected` event and raw frames arrive on the queue.
3. Once the framing is obvious, switch to `line`/`delimited` and add the request command; parse in the cloud, or set `Pattern`.

### Finding devices

On the target machine, list connected COM ports and HID devices to fill in the config:

```
cloudprint list-devices          # human-readable
cloudprint list-devices --json   # what the configurator uses
```

## Security

- **Credentials**: Stored in `appsettings.json` with file ACLs restricted to Administrators and SYSTEM only
- **URL validation**: Only HTTPS URLs are accepted; loopback and private/reserved addresses are blocked, including via DNS resolution (SSRF prevention). Applies to both inbound file downloads and outbound device webhooks.
- **File validation**: Downloaded files are checked against magic bytes for the claimed content type
- **File size limit**: Downloads are capped at 50MB
- **Credential passing**: The configurator passes credentials to the service binary via stdin (not visible in process listings)
- **SQS IAM scoping**: AWS credentials are scoped to `cloudprint-*` SQS queues only

## Logging

Logs are written to `C:\ProgramData\CloudPrint\logs\cloudprint-YYYYMMDD.log`. Rolling daily with 30-day retention.

## Reliability

- **Auto-restart**: Service automatically restarts on failure (5s, 10s, 30s delays)
- **Job logging**: Full job payload logged on receipt for diagnostics
- **SQS**: Dead letter queue after 5 failed attempts; 300s visibility timeout prevents double-printing
- **HTTP**: Server-side retry via ack timeout; failed jobs reported back via PATCH for server-side handling

## Uninstall

Use **Add/Remove Programs** (Apps & features → CloudPrint → Uninstall), or run:

```powershell
& "C:\Program Files\CloudPrint\CloudPrint.exe" --uninstall
```

This stops and deletes the service, removes the Add/Remove Programs entry, and deletes `C:\Program Files\CloudPrint`. Logs and dumps under `C:\ProgramData\CloudPrint` are left behind — delete that folder manually if you don't need them. SQS queues are not deleted; remove `cloudprint-{hostname}-*` queues in the AWS console.

## Requirements

- Windows 10/11 or Windows Server 2016+
- **SQS transport**: AWS account with SQS access
- **HTTP transport**: An API implementing the spec above
- No .NET runtime required (self-contained build)

## Development

```bash
# Clone
git clone https://github.com/kpconnell/cloudprint.git
cd cloudprint

# Build
dotnet build

# Test
dotnet test

# Run locally (dry-run mode on non-Windows)
dotnet run --project src/CloudPrint.Service
```

## License

MIT License - see [LICENSE](LICENSE) for details.

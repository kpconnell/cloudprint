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
| `PdfFitMode` | `Margins` | `Margins` fits within the driver-reported page margins (office printers). `PhysicalPage` prints edge-to-edge ignoring margins (thermal/label printers). |

**Per-printer overrides (SQS):** Each entry in `Printers[]` may set its own `PdfRenderDpi` / `PdfFitMode`. When a lane omits a value, it falls back to the top-level default. This lets a thermal label printer (`203` / `PhysicalPage`) and an office laser (`300` / `Margins`) coexist on the same machine. The configurator exposes these per printer.

To change settings later, re-run the configurator, or edit `appsettings.json` and restart the service.

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
| `Hostname` | `WAREHOUSE-PC1` (raw hostname) |
| `PrinterName` | `Zebra ZP500` (raw printer name) |

Senders enumerate available printers with `ListQueues(QueueNamePrefix=cloudprint-)` followed by `ListQueueTags` per queue URL. Hostname/printer tags carry the original casing and spaces, since the queue name itself sanitizes those.

#### Queue Naming

Each machine/printer gets its own queue pair:

| Queue | Example | Purpose |
|---|---|---|
| Main | `cloudprint-{hostname}-{printer}` | Print jobs |
| DLQ | `cloudprint-{hostname}-{printer}-dlq` | Failed jobs (after 5 retries) |

The hostname and printer name are lowercased with non-alphanumeric characters replaced by hyphens. For example, a machine `WAREHOUSE-PC1` with printer `Zebra ZP500` produces:
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

Beyond printing, CloudPrint can read from devices physically connected to the workstation and publish their readings to the cloud. The first-class devices are **USB/serial scales**; a generic passthrough mode forwards any serial/HID device. This is opt-in: it activates only when `Devices[]` is non-empty, and runs regardless of the print `Transport`.

Each configured device runs its own background loop: connect → read → publish, with automatic reconnect (capped backoff) and de-duplication of repeated identical readings.

### Configuration

Add a `Devices` array under `CloudPrint`. See [`samples/appsettings.sample.json`](samples/appsettings.sample.json) for a complete, multi-device example.

```json
{
  "CloudPrint": {
    "Station": "shipping-pc-01",
    "DevicePollIntervalMs": 500,
    "DeviceStableOnly": true,
    "Devices": [
      {
        "Name": "scale-shipping",
        "Type": "serial-scale",
        "Protocol": "mt-sics",
        "ComPort": "COM3",
        "BaudRate": 9600,
        "LineEnding": "crlf",
        "PollMode": "request",
        "RequestCommand": "S",
        "InitCommands": [ "Z" ],
        "Output": { "Transport": "sqs", "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123/cloudprint-shipping-pc-01-scale-shipping" }
      },
      {
        "Name": "scale-counter",
        "Type": "hid-scale",
        "Vid": 2338,
        "Pid": 24613,
        "PollMode": "stream",
        "Output": { "Transport": "http", "WebhookUrl": "https://wms.example.com/api/readings", "HeaderName": "X-Api-Key", "HeaderValue": "..." }
      }
    ]
  }
}
```

| Field | Applies to | Description |
|---|---|---|
| `Name` | all | Unique device id; used as `deviceId` and the log tag. Required. |
| `Type` | all | `serial-scale`, `hid-scale`, `serial-raw`, or `hid-raw`. |
| `Station` | all | Per-device workstation id override (top-level `Station` is the default; blank → machine name). |
| `Protocol` | serial | Serial parser selector (default `mt-sics`; tolerant of common A&D/Ohaus/MT-SICS ASCII formats). |
| `ComPort`, `BaudRate`, `Parity`, `DataBits`, `StopBits` | serial | Port settings (defaults 9600/None/8/1). |
| `LineEnding` | serial | Frame terminator: `crlf`, `lf`, `cr`, or a literal string (default `crlf`). |
| `Encoding` | serial | `ascii` (default) or `utf8`. |
| `RequestCommand` | serial | Command sent each cycle in `request`/`interval` mode (e.g. MT-SICS `S`). |
| `InitCommands` | serial | Commands sent once on connect (e.g. `Z` to zero/tare). |
| `Vid`, `Pid` | hid | USB vendor/product id (decimal). Find them with `cloudprint list-devices`. |
| `Hid*Offset` / `HidReportId` / `HidWeightSize` | hid | Optional manual report-layout overrides for non-conformant HID scales. |
| `Pattern` | raw | Regex with named groups `value`, `unit`, `stable` for `*-raw` passthrough. Omit to forward the raw frame only. |
| `PollMode` | all | `stream` (read continuously), `request`, or `interval` (default `stream`). |
| `PollIntervalMs` | all | Poll cadence for request/interval modes (top-level `DevicePollIntervalMs` is the default). |
| `StableOnly` | all | Publish only readings the device marks stable (top-level `DeviceStableOnly` is the default; `true`). |
| `Output.Transport` | all | `sqs` or `http` — chosen per device. |
| `Output.QueueUrl` | sqs | Target SQS queue (requires `sqs:SendMessage`; a `cloudprint-*` name is already in IAM scope). |
| `Output.WebhookUrl`, `Output.HeaderName`, `Output.HeaderValue` | http | HTTPS webhook (validated for SSRF) and optional auth header. |

### Reading format

Each reading is published as JSON:

```json
{
  "readingId": "8a1f...",
  "station": "shipping-pc-01",
  "host": "SHIPPING-PC-01",
  "deviceId": "scale-shipping",
  "deviceType": "serial-scale",
  "source": { "connection": "serial", "port": "COM3" },
  "timestamp": "2026-06-06T17:55:05.123Z",
  "value": 2.5,
  "unit": "kg",
  "stable": true,
  "status": "ok",
  "raw": "ST,+00002.50 kg"
}
```

`status` is one of `ok | motion | overload | underload | zero | error`. HID readings carry `source.vid`/`source.pid`/`source.product`; raw passthrough readings populate `raw` with `value` left null when no `Pattern` matches.

### Supported devices & drivers

| Type | Connection | Driver |
|---|---|---|
| `serial-scale` | RS-232 / USB-CDC virtual COM port (Mettler Toledo MT-SICS, A&D, Ohaus, CAS) | Requires the vendor **VCP driver** (FTDI/Prolific/CP210x) installed so a COM port appears. |
| `hid-scale` | USB HID POS scale (HID usage page `0x8D`) | **No driver** — uses the in-box Windows HID driver. Must not be exclusively claimed by another app (e.g. a POS/`ClaimedScale` app); if it is, CloudPrint logs the failure and retries. |
| `serial-raw` / `hid-raw` | Any serial / HID device | Same driver rules as above; forwards frames with optional regex extraction. |

Hot-plug is handled by reconnect-with-backoff (the service polls and re-opens the device), so unplug/replug recovers automatically.

### Finding devices

On the target machine, list connected COM ports and HID devices (VID/PID/product) to fill in the config:

```
cloudprint list-devices
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

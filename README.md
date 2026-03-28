# CloudPrint

A Windows Service that polls AWS SQS for print jobs and routes them to local printers. Designed for environments where cloud applications need to print to on-premise printers (shipping labels, receipts, documents).

## Quick Install

Open PowerShell **as Administrator** and run:

```powershell
irm https://github.com/kpconnell/cloudprint/releases/latest/download/install.ps1 | iex
```

The installer will:
1. Prompt for AWS credentials (Access Key ID + Secret Access Key)
2. Show a list of AWS regions to choose from
3. Verify credentials are valid
4. Show available local printers and let you pick one
5. Create an SQS queue + dead letter queue for this machine/printer
6. Install and start the Windows Service

On reinstall, existing credentials and printer selection are preserved — just press Enter to keep them.

## How It Works

1. A producer (your app) sends a message to the machine's SQS queue
2. CloudPrint long-polls the queue and picks up the message
3. The file is downloaded from the HTTPS URL in the message
4. The file is validated (magic bytes check against claimed content type)
5. The file is sent to the configured printer
6. On success, the message is deleted from the queue
7. On failure, the message remains for retry (up to 5 attempts, then moved to DLQ)

### Queue Naming

Each machine/printer gets its own queue pair:

| Queue | Example | Purpose |
|---|---|---|
| Main | `cloudprint-{hostname}-{printer}` | Print jobs |
| DLQ | `cloudprint-{hostname}-{printer}-dlq` | Failed jobs (after 5 retries) |

The hostname and printer name are lowercased with non-alphanumeric characters replaced by hyphens. For example, a machine `WAREHOUSE-PC1` with printer `Zebra ZP500` produces:
- `cloudprint-warehouse-pc1-zebra-zp500`
- `cloudprint-warehouse-pc1-zebra-zp500-dlq`

Queue names are capped at 80 characters (SQS limit).

## Message Format

Send JSON messages to the machine's SQS queue:

```json
{
  "fileUrl": "https://s3.amazonaws.com/my-bucket/label.zpl",
  "contentType": "application/vnd.zebra.zpl",
  "copies": 1,
  "metadata": {}
}
```

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

**Planned:** `application/pdf` — not yet supported. Contributions welcome.

### Fields

| Field | Required | Description |
|---|---|---|
| `fileUrl` | Yes | HTTPS URL to the file (signed or public) |
| `printerName` | No | Override the configured printer (optional) |
| `contentType` | Yes | MIME type determining how the file is printed |
| `copies` | No | Number of copies (default: 1) |
| `metadata` | No | Arbitrary key-value pairs for your own tracking |

## Security

- **Credentials**: AWS access keys are stored in `appsettings.json` with file ACLs restricted to Administrators and SYSTEM only
- **URL validation**: Only HTTPS URLs are accepted; loopback addresses are blocked (SSRF prevention)
- **File validation**: Downloaded files are checked against magic bytes for the claimed content type
- **File size limit**: Downloads are capped at 50MB
- **Credential passing**: Install script passes credentials to the service binary via stdin (not visible in process listings)
- **IAM scoping**: Credentials should be scoped to `cloudprint-*` SQS queues only — see [AWS Credentials Guide](docs/aws-credentials.md)

## Transports

CloudPrint supports two transport modes, selected during install:

### AWS SQS

The default transport. See [AWS Credentials Guide](docs/aws-credentials.md) for IAM setup.

```json
{
  "CloudPrint": {
    "Transport": "sqs",
    "QueueUrl": "https://sqs.us-east-2.amazonaws.com/123456789/cloudprint-HOSTNAME-PRINTER",
    "Region": "us-east-2",
    "AwsAccessKeyId": "AKIA...",
    "AwsSecretAccessKey": "...",
    "PrinterName": "Zebra_ZP500",
    "VisibilityTimeoutSeconds": 300
  }
}
```

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

**200 Response:**
```json
{
  "id": "job-123",
  "fileUrl": "https://s3.amazonaws.com/bucket/label.zpl",
  "printerName": "Zebra_ZP500",
  "contentType": "application/vnd.zebra.zpl",
  "copies": 1,
  "metadata": {}
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

## Logging

Logs are written to `C:\ProgramData\CloudPrint\logs\cloudprint-YYYYMMDD.log`. Rolling daily with 30-day retention.

## Reliability

- **Dead letter queue**: Messages that fail 5 times are moved to a `-dlq` queue for investigation
- **Auto-restart**: Service automatically restarts on failure (5s, 10s, 30s delays)
- **Visibility timeout**: 300 seconds default — prevents double-printing during long jobs
- **Message body logging**: Full message body logged on receipt for diagnostics

## Uninstall

```powershell
Stop-Service CloudPrint
sc.exe delete CloudPrint
Remove-Item "C:\Program Files\CloudPrint" -Recurse
```

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

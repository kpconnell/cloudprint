# CloudPrint

A Windows Service that receives print jobs from the cloud and routes them to local printers. Designed for environments where cloud applications need to print to on-premise printers (shipping labels, receipts, documents).

Supports two transport modes:
- **AWS SQS** — polls an SQS queue for jobs (auto-provisioned per machine/printer)
- **HTTP API** — long-polls your own API for jobs (bring your own server)

## Quick Install

Open PowerShell **as Administrator** and run:

```powershell
irm https://github.com/kpconnell/cloudprint/releases/latest/download/install.ps1 | iex
```

The installer will:
1. Show available local printers and let you pick one
2. Ask which transport to use (SQS or HTTP)
3. Prompt for transport-specific configuration (AWS credentials or API URL + key)
4. Install and start the Windows Service

On reinstall, existing configuration is preserved — just press Enter to keep current values.

## How It Works

1. CloudPrint long-polls for jobs (from SQS or your HTTP API, depending on transport)
2. The print content is resolved — either downloaded from the `fileUrl` or read directly from the inline `content` field
3. The file is validated (magic bytes check against claimed content type)
4. The file is sent to the configured printer
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
| `printerName` | No | Override the configured printer (optional) |
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

**Planned:** `application/pdf` — not yet supported. Contributions welcome.

## Transports

CloudPrint supports two transport modes, selected during install.

### AWS SQS

The default transport. The installer auto-creates an SQS queue pair per machine/printer.

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

#### Queue Naming

Each machine/printer gets its own queue pair:

| Queue | Example | Purpose |
|---|---|---|
| Main | `cloudprint-{hostname}-{printer}` | Print jobs |
| DLQ | `cloudprint-{hostname}-{printer}-dlq` | Failed jobs (after 5 retries) |

The hostname and printer name are lowercased with non-alphanumeric characters replaced by hyphens. For example, a machine `WAREHOUSE-PC1` with printer `Zebra ZP500` produces:
- `cloudprint-warehouse-pc1-zebra-zp500`
- `cloudprint-warehouse-pc1-zebra-zp500-dlq`

Queue names are capped at 80 characters (SQS limit).

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
                "sqs:SetQueueAttributes",
                "sqs:GetQueueAttributes",
                "sqs:GetQueueUrl",
                "sqs:ReceiveMessage",
                "sqs:DeleteMessage"
            ],
            "Resource": "arn:aws:sqs:*:*:cloudprint-*"
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
| `sqs:CreateQueue` | Installer auto-creates the main queue + dead-letter queue |
| `sqs:SetQueueAttributes` | Sets the redrive policy (DLQ) on existing queues |
| `sqs:GetQueueAttributes` | Reads the DLQ ARN to wire up the redrive policy |
| `sqs:GetQueueUrl` | Looks up the queue URL when it already exists |
| `sqs:ReceiveMessage` | Long-polls the queue for print jobs |
| `sqs:DeleteMessage` | Removes a message after successful printing |
| `sts:GetCallerIdentity` | Validates credentials during installation |

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

## Security

- **Credentials**: Stored in `appsettings.json` with file ACLs restricted to Administrators and SYSTEM only
- **URL validation**: Only HTTPS URLs are accepted; loopback addresses are blocked (SSRF prevention)
- **File validation**: Downloaded files are checked against magic bytes for the claimed content type
- **File size limit**: Downloads are capped at 50MB
- **Credential passing**: Install script passes credentials to the service binary via stdin (not visible in process listings)
- **SQS IAM scoping**: AWS credentials are scoped to `cloudprint-*` SQS queues only

## Logging

Logs are written to `C:\ProgramData\CloudPrint\logs\cloudprint-YYYYMMDD.log`. Rolling daily with 30-day retention.

## Reliability

- **Auto-restart**: Service automatically restarts on failure (5s, 10s, 30s delays)
- **Job logging**: Full job payload logged on receipt for diagnostics
- **SQS**: Dead letter queue after 5 failed attempts; 300s visibility timeout prevents double-printing
- **HTTP**: Server-side retry via ack timeout; failed jobs reported back via PATCH for server-side handling

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

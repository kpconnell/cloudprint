# CloudPrint

A Windows Service that polls AWS SQS for print jobs and routes them to local printers. Designed for environments where cloud applications need to print to on-premise printers (shipping labels, receipts, documents).

## Quick Install

Open PowerShell **as Administrator** and run:

```powershell
irm https://github.com/kpconnell/cloudprint/releases/latest/download/install.ps1 | iex
```

The installer will:
1. Check for (and install) the AWS CLI if needed
2. Prompt for AWS CLI profile name and region
3. Show available local printers
4. Create an SQS queue for this machine (if it doesn't exist)
5. Install and start the Windows Service

To reconfigure, re-run the installer.

## How It Works

1. A producer (your app) sends a message to the machine's SQS queue
2. CloudPrint long-polls the queue and picks up the message
3. The file is downloaded from the URL in the message
4. The file is sent to the specified local printer
5. On success, the message is deleted from the queue
6. On failure, the message remains in the queue for retry/DLQ

Each machine gets its own queue, named `cloudprint-{HOSTNAME}`.

## Message Format

Send JSON messages to the machine's SQS queue:

```json
{
  "fileUrl": "https://s3.amazonaws.com/my-bucket/label.zpl",
  "printerName": "Zebra_ZP500",
  "contentType": "application/vnd.zebra.zpl",
  "copies": 1,
  "metadata": {}
}
```

### Supported Content Types

| Content Type | Handling |
|---|---|
| `application/vnd.zebra.zpl` | Raw passthrough (ZPL printers) |
| `text/plain` | Raw passthrough |
| `image/png` | Printed as image |
| `image/jpeg` | Printed as image |
| `image/bmp` | Printed as image |
| `image/gif` | Printed as image |
| `image/tiff` | Printed as image |

**Planned:** `application/pdf` — not yet supported. Contributions welcome.

### Fields

| Field | Required | Description |
|---|---|---|
| `fileUrl` | Yes | Signed or public URL to the file |
| `printerName` | Yes | Name of the local printer (as shown in Windows) |
| `contentType` | Yes | MIME type determining how the file is printed |
| `copies` | No | Number of copies (default: 1) |
| `metadata` | No | Arbitrary key-value pairs for your own tracking |

## Configuration

Configuration lives in `appsettings.json` alongside the service binary (typically `C:\Program Files\CloudPrint\`).

```json
{
  "CloudPrint": {
    "QueueUrl": "https://sqs.us-east-1.amazonaws.com/123456789/cloudprint-HOSTNAME",
    "Region": "us-east-1",
    "AwsAccessKeyId": "AKIA...",
    "AwsSecretAccessKey": "..."
  }
}
```

See [AWS Credentials Guide](docs/aws-credentials.md) for how to create a tightly-scoped IAM user for CloudPrint.

## Logging

Logs are written to `logs/cloudprint-YYYYMMDD.log` in the installation directory using Serilog. Rolling daily, structured format.

## Uninstall

```powershell
irm https://github.com/kpconnell/cloudprint/releases/latest/download/install.ps1 | iex -Uninstall
```

Or manually:
```powershell
Stop-Service CloudPrint
sc.exe delete CloudPrint
Remove-Item "C:\Program Files\CloudPrint" -Recurse
```

## Requirements

- Windows 10/11 or Windows Server 2016+
- AWS account with SQS access
- No .NET runtime required (self-contained build)

## Development

```bash
# Clone
git clone https://github.com/kpconnell/cloudprint.git
cd cloudprint

# Build
dotnet build src/CloudPrint.Service

# Run locally (not as service)
dotnet run --project src/CloudPrint.Service
```

## License

MIT License - see [LICENSE](LICENSE) for details.

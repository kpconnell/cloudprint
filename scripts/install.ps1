#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or updates the CloudPrint Windows Service.
.DESCRIPTION
    Downloads the latest CloudPrint release, prompts for AWS credentials, region,
    and printer selection, creates the SQS queue, and registers the Windows Service.
.PARAMETER Uninstall
    Removes the CloudPrint service and files.
#>
param(
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'CloudPrint'
$InstallDir = "$env:ProgramFiles\CloudPrint"
$RepoOwner = 'kpconnell'
$RepoName = 'cloudprint'

$AwsRegions = @(
    @{ Num = 1;  Id = 'us-east-1';      Name = 'US East (N. Virginia)' }
    @{ Num = 2;  Id = 'us-east-2';      Name = 'US East (Ohio)' }
    @{ Num = 3;  Id = 'us-west-1';      Name = 'US West (N. California)' }
    @{ Num = 4;  Id = 'us-west-2';      Name = 'US West (Oregon)' }
    @{ Num = 5;  Id = 'ca-central-1';   Name = 'Canada (Central)' }
    @{ Num = 6;  Id = 'eu-west-1';      Name = 'Europe (Ireland)' }
    @{ Num = 7;  Id = 'eu-west-2';      Name = 'Europe (London)' }
    @{ Num = 8;  Id = 'eu-central-1';   Name = 'Europe (Frankfurt)' }
    @{ Num = 9;  Id = 'ap-southeast-1'; Name = 'Asia Pacific (Singapore)' }
    @{ Num = 10; Id = 'ap-southeast-2'; Name = 'Asia Pacific (Sydney)' }
    @{ Num = 11; Id = 'ap-northeast-1'; Name = 'Asia Pacific (Tokyo)' }
)

function Write-Step($message) {
    Write-Host "`n>> $message" -ForegroundColor Cyan
}

function Stop-ExistingService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -eq 'Running') {
            Write-Step "Stopping existing CloudPrint service..."
            Stop-Service -Name $ServiceName -Force
            Start-Sleep -Seconds 2
        }
        Write-Step "Removing existing service registration..."
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 1
    }
}

# --- Uninstall ---
if ($Uninstall) {
    Write-Step "Uninstalling CloudPrint..."
    Stop-ExistingService
    if (Test-Path $InstallDir) {
        Remove-Item $InstallDir -Recurse -Force
        Write-Host "Removed $InstallDir" -ForegroundColor Green
    }
    Write-Host "`nCloudPrint has been uninstalled." -ForegroundColor Green
    exit 0
}

Write-Host @"

   _____ _                 _ _____      _       _
  / ____| |               | |  __ \    (_)     | |
 | |    | | ___  _   _  __| | |__) | __ _ _ __ | |_
 | |    | |/ _ \| | | |/ _`` |  ___/ '__| | '_ \| __|
 | |____| | (_) | |_| | (_| | |   | |  | | | | | |_
  \_____|_|\___/ \__,_|\__,_|_|   |_|  |_|_| |_|\__|

"@ -ForegroundColor Cyan

# --- Download latest release ---
Write-Step "Downloading latest release..."
$releaseInfo = Invoke-RestMethod "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
$zipAsset = $releaseInfo.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1

if (-not $zipAsset) {
    Write-Error "No release zip found. Please check https://github.com/$RepoOwner/$RepoName/releases"
    exit 1
}

$tempZip = Join-Path $env:TEMP "cloudprint-latest.zip"
$tempExtract = Join-Path $env:TEMP "cloudprint-extract"

Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $tempZip
if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force }
Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

# --- Stop existing service if upgrading ---
Stop-ExistingService

# --- Copy files ---
Write-Step "Installing to $InstallDir..."
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
Copy-Item "$tempExtract\*" $InstallDir -Recurse -Force

$exePath = Join-Path $InstallDir 'CloudPrint.Service.exe'

# --- Load existing config if upgrading ---
$existingConfig = $null
$configPath = Join-Path $InstallDir 'appsettings.json'
if (Test-Path $configPath) {
    $existingConfig = Get-Content $configPath -Raw | ConvertFrom-Json
}

# --- AWS Credentials ---
$reconfigureCreds = $true
$defaultKeyId = if ($existingConfig) { $existingConfig.CloudPrint.AwsAccessKeyId } else { '' }
$defaultSecret = if ($existingConfig) { $existingConfig.CloudPrint.AwsSecretAccessKey } else { '' }
$defaultRegion = if ($existingConfig) { $existingConfig.CloudPrint.Region } else { '' }

if ($defaultKeyId -and $defaultSecret -and $defaultRegion) {
    $maskedKeyId = $defaultKeyId.Substring(0, [Math]::Min(8, $defaultKeyId.Length)) + '...'
    Write-Step "Current AWS Configuration"
    Write-Host ""
    Write-Host "  Access Key:  $maskedKeyId"
    Write-Host "  Region:      $defaultRegion"
    Write-Host ""
    $answer = Read-Host "  Reconfigure AWS credentials? [y/N]"
    if ($answer -match '^[Yy]') {
        $reconfigureCreds = $true
    } else {
        $reconfigureCreds = $false
        $accessKeyId = $defaultKeyId
        $secretPlain = $defaultSecret
        $region = $defaultRegion
    }
}

if ($reconfigureCreds) {
    Write-Step "AWS Credentials"
    Write-Host @"

  CloudPrint needs AWS credentials to access SQS.
  These should be scoped to SQS only — see the credentials guide:
  https://github.com/kpconnell/cloudprint/blob/main/docs/aws-credentials.md

"@

    # Access Key ID
    $maskedKeyId = if ($defaultKeyId) { $defaultKeyId.Substring(0, [Math]::Min(8, $defaultKeyId.Length)) + '...' } else { '' }
    if ($defaultKeyId) {
        $accessKeyId = Read-Host "  AWS Access Key ID [$maskedKeyId]"
        if ([string]::IsNullOrWhiteSpace($accessKeyId)) { $accessKeyId = $defaultKeyId }
    } else {
        do {
            $accessKeyId = Read-Host "  AWS Access Key ID"
        } while ([string]::IsNullOrWhiteSpace($accessKeyId))
    }

    # Secret Access Key
    $secretPrompt = if ($defaultSecret) { "  AWS Secret Access Key [keep existing]" } else { "  AWS Secret Access Key" }
    $secretAccessKey = Read-Host $secretPrompt -AsSecureString
    $secretPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secretAccessKey))

    if ([string]::IsNullOrWhiteSpace($secretPlain)) {
        if ($defaultSecret) {
            $secretPlain = $defaultSecret
            Write-Host "  (Keeping existing secret)" -ForegroundColor DarkGray
        } else {
            Write-Error "Secret Access Key is required."
            exit 1
        }
    }

    # Region selection
    Write-Step "AWS Region"

    Write-Host ""
    foreach ($r in $AwsRegions) {
        $marker = if ($r.Id -eq $defaultRegion) { ' *' } else { '' }
        Write-Host ("  {0,2}) {1,-20} {2}{3}" -f $r.Num, $r.Id, $r.Name, $marker)
    }
    Write-Host ""

    $regionInput = Read-Host "  Select region (1-$($AwsRegions.Count))$(if ($defaultRegion) { " [keep $defaultRegion]" } else { '' })"

    if ([string]::IsNullOrWhiteSpace($regionInput) -and $defaultRegion) {
        $region = $defaultRegion
    } else {
        $regionNum = 0
        if ([int]::TryParse($regionInput, [ref]$regionNum) -and $regionNum -ge 1 -and $regionNum -le $AwsRegions.Count) {
            $region = ($AwsRegions | Where-Object { $_.Num -eq $regionNum }).Id
        } else {
            Write-Error "Invalid selection. Please enter a number between 1 and $($AwsRegions.Count)."
            exit 1
        }
    }

    Write-Host "  Selected: $region" -ForegroundColor Green
}

# --- Verify credentials ---
Write-Step "Verifying AWS credentials..."

$verifyResult = & $exePath verify-creds $accessKeyId.Trim() $secretPlain $region 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Error "Invalid AWS credentials: $verifyResult"
    exit 1
}

Write-Host "  Authenticated as: $verifyResult" -ForegroundColor Green

# --- Printer selection ---
$defaultPrinter = if ($existingConfig) { $existingConfig.CloudPrint.PrinterName } else { '' }
$reconfigurePrinter = $true

$printers = @(Get-Printer | Select-Object -ExpandProperty Name)
if ($printers.Count -eq 0) {
    Write-Host ""
    Write-Host "  No printers found on this machine." -ForegroundColor Red
    Write-Host "  Add a printer in Windows Settings and re-run this installer." -ForegroundColor Yellow
    exit 1
}

if ($defaultPrinter -and ($printers -contains $defaultPrinter)) {
    Write-Step "Current Printer: $defaultPrinter"
    $answer = Read-Host "  Change printer? [y/N]"
    if ($answer -match '^[Yy]') {
        $reconfigurePrinter = $true
    } else {
        $reconfigurePrinter = $false
        $selectedPrinter = $defaultPrinter
    }
}

if ($reconfigurePrinter) {
    Write-Step "Printer Selection"
    Write-Host ""
    for ($i = 0; $i -lt $printers.Count; $i++) {
        $marker = if ($printers[$i] -eq $defaultPrinter) { ' *' } else { '' }
        Write-Host ("  {0,2}) {1}{2}" -f ($i + 1), $printers[$i], $marker)
    }
    Write-Host ""

    $printerInput = Read-Host "  Select printer (1-$($printers.Count))$(if ($defaultPrinter) { " [keep $defaultPrinter]" } else { '' })"

    if ([string]::IsNullOrWhiteSpace($printerInput) -and $defaultPrinter) {
        $selectedPrinter = $defaultPrinter
    } else {
        $printerNum = 0
        if ([int]::TryParse($printerInput, [ref]$printerNum) -and $printerNum -ge 1 -and $printerNum -le $printers.Count) {
            $selectedPrinter = $printers[$printerNum - 1]
        } else {
            Write-Error "Invalid selection. Please enter a number between 1 and $($printers.Count)."
            exit 1
        }
    }
}

Write-Host "  Selected: $selectedPrinter" -ForegroundColor Green

# --- Create SQS queue ---
$hostname = $env:COMPUTERNAME.ToLower()
$safePrinterName = ($selectedPrinter -replace '[^a-zA-Z0-9\-]', '-').ToLower().TrimEnd('-')
$queueName = "cloudprint-$hostname-$safePrinterName"

# SQS queue names max 80 chars
if ($queueName.Length -gt 80) {
    $queueName = $queueName.Substring(0, 80)
}

Write-Step "Creating SQS queue '$queueName'..."

$queueUrl = & $exePath create-queue $queueName $accessKeyId.Trim() $secretPlain $region 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Error "Failed to create SQS queue: $queueUrl`n`nCheck that your credentials have sqs:CreateQueue permission on cloudprint-* queues."
    exit 1
}

Write-Host "  Queue: $queueUrl" -ForegroundColor Green

# --- Ensure log directory ---
$logDir = "$env:ProgramData\CloudPrint\logs"
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

# --- Write config ---
Write-Step "Writing configuration..."

$config = @{
    CloudPrint = @{
        QueueUrl = $queueUrl.Trim()
        Region = $region
        AwsAccessKeyId = $accessKeyId.Trim()
        AwsSecretAccessKey = $secretPlain
        PrinterName = $selectedPrinter
        MaxConcurrentJobs = 1
        VisibilityTimeoutSeconds = 60
    }
    Serilog = @{
        MinimumLevel = @{
            Default = "Information"
            Override = @{
                Microsoft = "Warning"
                System = "Warning"
            }
        }
        WriteTo = @(
            @{ Name = "Console" }
            @{
                Name = "File"
                Args = @{
                    path = "C:\ProgramData\CloudPrint\logs\cloudprint-.log"
                    rollingInterval = "Day"
                    retainedFileCountLimit = 30
                }
            }
        )
    }
} | ConvertTo-Json -Depth 10

Set-Content -Path $configPath -Value $config -Encoding UTF8

# --- Lock down config file (contains credentials) ---
$acl = Get-Acl $configPath
$acl.SetAccessRuleProtection($true, $false)
$acl.Access | ForEach-Object { $acl.RemoveAccessRule($_) } | Out-Null
$adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "BUILTIN\Administrators", "FullControl", "Allow")
$systemRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\SYSTEM", "FullControl", "Allow")
$acl.AddAccessRule($adminRule)
$acl.AddAccessRule($systemRule)
Set-Acl -Path $configPath -AclObject $acl
Write-Host "  Config file locked to Administrators and SYSTEM only" -ForegroundColor DarkGray

# --- Register service ---
Write-Step "Registering Windows Service..."

New-Service -Name $ServiceName `
    -BinaryPathName $exePath `
    -DisplayName 'CloudPrint' `
    -Description 'Polls AWS SQS for print jobs and routes them to local printers' `
    -StartupType Automatic | Out-Null

# --- Start service ---
Write-Step "Starting CloudPrint service..."
Start-Service -Name $ServiceName

$svc = Get-Service -Name $ServiceName
Write-Host @"

  CloudPrint installed successfully!

  Status:    $($svc.Status)
  Install:   $InstallDir
  Printer:   $selectedPrinter
  Queue:     $queueName
  Region:    $region
  Logs:      C:\ProgramData\CloudPrint\logs\

  To reconfigure, run this installer again.

"@ -ForegroundColor Green

# --- Cleanup ---
Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue

using System.Text.Json;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.FileHandling;
using CloudPrint.Service.Printing;
using Microsoft.Extensions.Options;

namespace CloudPrint.Service.Transport;

public class JobProcessor : IJobProcessor
{
    private readonly CloudPrintOptions _options;
    private readonly FileDownloader _fileDownloader;
    private readonly PrintRouter _printRouter;
    private readonly ILogger<JobProcessor> _logger;

    public JobProcessor(
        IOptions<CloudPrintOptions> options,
        FileDownloader fileDownloader,
        PrintRouter printRouter,
        ILogger<JobProcessor> logger)
    {
        _options = options.Value;
        _fileDownloader = fileDownloader;
        _printRouter = printRouter;
        _logger = logger;
    }

    private static readonly HashSet<string> TextContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.zebra.zpl",
        "text/plain"
    };

    public async Task<(bool Success, string? Error)> ProcessAsync(
        string jobId, PrintJobMessage job, CancellationToken cancellationToken)
    {
        var printerName = !string.IsNullOrWhiteSpace(job.PrinterName)
            ? job.PrinterName
            : _options.PrinterName;

        bool hasContent = !string.IsNullOrEmpty(job.Content);
        bool hasFileUrl = !string.IsNullOrWhiteSpace(job.FileUrl);

        if (!hasContent && !hasFileUrl)
            return (false, "Job must specify either 'content' or 'fileUrl'");

        _logger.LogInformation(
            "Processing print job {JobId}: {ContentType} -> {PrinterName} (inline: {Inline})",
            jobId, job.ContentType, printerName, hasContent);

        string tempFile;
        if (hasContent)
        {
            var result = WriteInlineContent(job.Content!, job.ContentType);
            if (!result.Success)
                return (false, result.Error);
            tempFile = result.FilePath!;
        }
        else
        {
            tempFile = await _fileDownloader.DownloadAsync(job.FileUrl, cancellationToken);
        }

        try
        {
            if (!FileValidator.Validate(tempFile, job.ContentType, out var validationError))
            {
                _logger.LogError(
                    "File validation failed for job {JobId}: {Reason}. Source: {Source}",
                    jobId, validationError, hasContent ? "inline" : job.FileUrl);
                return (false, validationError);
            }

            if (_options.DumpPayloads)
                DumpPayload(jobId, job, tempFile);

            var copies = Math.Clamp(job.Copies, 1, 100);
            for (var i = 0; i < copies; i++)
            {
                _printRouter.Print(tempFile, printerName, job.ContentType);
            }

            _logger.LogInformation("Print job {JobId} completed successfully", jobId);
            return (true, null);
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    }

    private (bool Success, string? Error, string? FilePath) WriteInlineContent(string content, string contentType)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"cloudprint-{Guid.NewGuid()}");
        try
        {
            if (TextContentTypes.Contains(contentType))
            {
                File.WriteAllText(tempPath, content);
            }
            else
            {
                var bytes = Convert.FromBase64String(content);
                File.WriteAllBytes(tempPath, bytes);
            }
            return (true, null, tempPath);
        }
        catch (FormatException)
        {
            TryDeleteFile(tempPath);
            return (false, $"Invalid base64 in 'content' for binary content type '{contentType}'", null);
        }
    }

    private void DumpPayload(string jobId, PrintJobMessage job, string filePath)
    {
        try
        {
            var dumpDir = _options.DumpPath;
            Directory.CreateDirectory(dumpDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var safeId = jobId.Length > 20 ? jobId[..20] : jobId;
            var prefix = $"{timestamp}_{safeId}";

            // Dump the job message as JSON
            var json = JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dumpDir, $"{prefix}.json"), json);

            // Dump the resolved file content
            var ext = ContentTypeToExtension(job.ContentType);
            File.Copy(filePath, Path.Combine(dumpDir, $"{prefix}{ext}"), overwrite: true);

            _logger.LogDebug("Dumped payload for job {JobId} to {DumpDir}", jobId, dumpDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dump payload for job {JobId}", jobId);
        }
    }

    private static string ContentTypeToExtension(string contentType) => contentType.ToLowerInvariant() switch
    {
        "application/vnd.zebra.zpl" => ".zpl",
        "text/plain" => ".txt",
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/bmp" => ".bmp",
        "image/gif" => ".gif",
        "image/tiff" => ".tiff",
        "application/pdf" => ".pdf",
        _ => ".bin"
    };

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temp file: {Path}", path);
        }
    }
}

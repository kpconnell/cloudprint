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

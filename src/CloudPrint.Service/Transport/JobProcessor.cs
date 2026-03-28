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

    public async Task<(bool Success, string? Error)> ProcessAsync(
        string jobId, PrintJobMessage job, CancellationToken cancellationToken)
    {
        var printerName = !string.IsNullOrWhiteSpace(job.PrinterName)
            ? job.PrinterName
            : _options.PrinterName;

        _logger.LogInformation(
            "Processing print job {JobId}: {ContentType} -> {PrinterName}",
            jobId, job.ContentType, printerName);

        var tempFile = await _fileDownloader.DownloadAsync(job.FileUrl, cancellationToken);
        try
        {
            if (!FileValidator.Validate(tempFile, job.ContentType, out var validationError))
            {
                _logger.LogError(
                    "File validation failed for job {JobId}: {Reason}. File URL: {FileUrl}",
                    jobId, validationError, job.FileUrl);
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

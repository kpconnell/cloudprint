using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.FileHandling;
using CloudPrint.Service.Printing;
using Microsoft.Extensions.Options;

namespace CloudPrint.Service.Sqs;

public class SqsPollingService : BackgroundService
{
    private readonly IAmazonSQS _sqsClient;
    private readonly CloudPrintOptions _options;
    private readonly FileDownloader _fileDownloader;
    private readonly PrintRouter _printRouter;
    private readonly ILogger<SqsPollingService> _logger;

    public SqsPollingService(
        IAmazonSQS sqsClient,
        IOptions<CloudPrintOptions> options,
        FileDownloader fileDownloader,
        PrintRouter printRouter,
        ILogger<SqsPollingService> logger)
    {
        _sqsClient = sqsClient;
        _options = options.Value;
        _fileDownloader = fileDownloader;
        _printRouter = printRouter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CloudPrint service starting. Queue: {QueueUrl}, Printer: {PrinterName}",
            _options.QueueUrl, _options.PrinterName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var request = new ReceiveMessageRequest
                {
                    QueueUrl = _options.QueueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 20,
                    VisibilityTimeout = _options.VisibilityTimeoutSeconds
                };

                var response = await _sqsClient.ReceiveMessageAsync(request, stoppingToken);

                foreach (var message in response.Messages ?? [])
                {
                    await ProcessMessageAsync(message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling SQS queue");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("CloudPrint service stopping");
    }

    private async Task ProcessMessageAsync(Message message, CancellationToken stoppingToken)
    {
        PrintJobMessage? job = null;
        try
        {
            job = JsonSerializer.Deserialize<PrintJobMessage>(message.Body);
            if (job is null)
            {
                _logger.LogError("Failed to deserialize message {MessageId}: null result", message.MessageId);
                return;
            }

            var printerName = !string.IsNullOrWhiteSpace(job.PrinterName)
                ? job.PrinterName
                : _options.PrinterName;

            _logger.LogInformation(
                "Processing print job {MessageId}: {ContentType} -> {PrinterName}",
                message.MessageId, job.ContentType, printerName);

            var tempFile = await _fileDownloader.DownloadAsync(job.FileUrl, stoppingToken);
            try
            {
                var copies = Math.Max(1, job.Copies);
                for (var i = 0; i < copies; i++)
                {
                    _printRouter.Print(tempFile, printerName, job.ContentType);
                }

                await _sqsClient.DeleteMessageAsync(_options.QueueUrl, message.ReceiptHandle, stoppingToken);
                _logger.LogInformation("Print job {MessageId} completed successfully", message.MessageId);
            }
            finally
            {
                TryDeleteFile(tempFile);
            }
        }
        catch (PrinterNotFoundException ex)
        {
            _logger.LogError(ex, "Printer not found for job {MessageId}. Message will go to DLQ",
                message.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process print job {MessageId}. Message will return to queue",
                message.MessageId);
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

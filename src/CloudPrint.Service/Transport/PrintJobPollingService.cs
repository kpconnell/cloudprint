using CloudPrint.Service.Configuration;
using Microsoft.Extensions.Options;

namespace CloudPrint.Service.Transport;

public class PrintJobPollingService : BackgroundService
{
    private readonly IJobSource _jobSource;
    private readonly IJobProcessor _processor;
    private readonly CloudPrintOptions _options;
    private readonly ILogger<PrintJobPollingService> _logger;

    public PrintJobPollingService(
        IJobSource jobSource,
        IJobProcessor processor,
        IOptions<CloudPrintOptions> options,
        ILogger<PrintJobPollingService> logger)
    {
        _jobSource = jobSource;
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "CloudPrint service starting. Transport: {Transport}, Printer: {PrinterName}",
            _options.Transport, _options.PrinterName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var envelope = await _jobSource.ReceiveAsync(stoppingToken);
                if (envelope is null)
                    continue;

                var (success, error) = await _processor.ProcessAsync(
                    envelope.Id, envelope.Job, stoppingToken);

                // For SQS, delete the message on success (needs receipt handle)
                if (success && _jobSource is SqsJobSource sqsSource && envelope.ReceiptHandle is not null)
                {
                    await sqsSource.DeleteMessageAsync(envelope.ReceiptHandle, stoppingToken);
                }

                await _jobSource.AcknowledgeAsync(envelope.Id, success, error, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in print job polling loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("CloudPrint service stopping");
    }
}

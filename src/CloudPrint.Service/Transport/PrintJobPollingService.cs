namespace CloudPrint.Service.Transport;

public class PrintJobPollingService : BackgroundService
{
    private readonly IJobSource _jobSource;
    private readonly IJobProcessor _processor;
    private readonly string _identifier;
    private readonly ILogger<PrintJobPollingService> _logger;

    public PrintJobPollingService(
        IJobSource jobSource,
        IJobProcessor processor,
        string identifier,
        ILogger<PrintJobPollingService> logger)
    {
        _jobSource = jobSource;
        _processor = processor;
        _identifier = identifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CloudPrint polling loop starting: {Identifier}", _identifier);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var envelope = await _jobSource.ReceiveAsync(stoppingToken);
                if (envelope is null)
                    continue;

                var (success, error) = await _processor.ProcessAsync(
                    envelope.Id, envelope.Job, stoppingToken);

                await _jobSource.AcknowledgeAsync(envelope, success, error, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Identifier}] Error in print job polling loop", _identifier);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("CloudPrint polling loop stopping: {Identifier}", _identifier);
    }
}

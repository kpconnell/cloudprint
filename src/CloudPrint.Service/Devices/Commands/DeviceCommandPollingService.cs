namespace CloudPrint.Service.Devices.Commands;

/// <summary>
/// Pulls commands from the command source and hands them to the right device loop. Unknown devices are
/// logged and acknowledged (a retry would not help); source errors back off 5 s like the print loop.
/// </summary>
public sealed class DeviceCommandPollingService : BackgroundService
{
    private readonly IDeviceCommandSource _source;
    private readonly DeviceCommandRouter _router;
    private readonly ILogger<DeviceCommandPollingService> _logger;

    public DeviceCommandPollingService(IDeviceCommandSource source, DeviceCommandRouter router, ILogger<DeviceCommandPollingService> logger)
    {
        _source = source;
        _router = router;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CloudPrint device command loop starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var envelope = await _source.ReceiveAsync(stoppingToken);
                if (envelope is null)
                    continue;

                if (!_router.TryRoute(envelope.Message))
                    _logger.LogWarning("Device command {Id} targets unknown device '{Device}' (known: {Known}); dropping",
                        envelope.Id, envelope.Message.TargetDevice, string.Join(", ", _router.DeviceNames));

                await _source.AcknowledgeAsync(envelope, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in device command loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });
            }
        }

        _logger.LogInformation("CloudPrint device command loop stopping");
    }
}

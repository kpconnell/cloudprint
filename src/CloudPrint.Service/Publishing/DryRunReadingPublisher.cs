using CloudPrint.Service.Devices;

namespace CloudPrint.Service.Publishing;

/// <summary>
/// Logs readings instead of publishing them. Used in dry-run / non-Windows mode so the full
/// device pipeline can be exercised without hitting SQS or a webhook. Mirrors <see cref="Printing.DryRunPrinter"/>.
/// </summary>
public class DryRunReadingPublisher : IReadingPublisher
{
    private readonly ILogger<DryRunReadingPublisher> _logger;

    public DryRunReadingPublisher(ILogger<DryRunReadingPublisher> logger) => _logger = logger;

    public Task PublishAsync(DeviceReading reading, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "[DRY RUN] [{DeviceId}] would publish reading: {Value} {Unit} (stable={Stable}, status={Status}, station={Station})",
            reading.DeviceId, reading.Value, reading.Unit, reading.Stable, reading.Status, reading.Station);
        return Task.CompletedTask;
    }
}

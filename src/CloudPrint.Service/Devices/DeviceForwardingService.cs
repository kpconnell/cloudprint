using CloudPrint.Service.Publishing;

namespace CloudPrint.Service.Devices;

/// <summary>
/// Drives one device: connect → read → publish, with reconnect backoff and reading dedup.
/// The outbound analogue of <see cref="Transport.PrintJobPollingService"/>; reuses the same
/// resilience contract (per-resource identifier, OperationCanceled guard, 5s catch-all).
/// </summary>
public class DeviceForwardingService : BackgroundService
{
    private static readonly string MachineName = Environment.MachineName;

    private readonly IDeviceReader _reader;
    private readonly IReadingPublisher _publisher;
    private readonly string _station;
    private readonly bool _stableOnly;
    private readonly string _identifier;
    private readonly ILogger<DeviceForwardingService> _logger;
    private readonly ReadingDeduplicator _dedup = new();

    public DeviceForwardingService(
        IDeviceReader reader,
        IReadingPublisher publisher,
        string station,
        bool stableOnly,
        string identifier,
        ILogger<DeviceForwardingService> logger)
    {
        _reader = reader;
        _publisher = publisher;
        _station = station;
        _stableOnly = stableOnly;
        _identifier = identifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CloudPrint device forwarding starting: {Identifier}", _identifier);

        var backoff = new ReconnectBackoff();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_reader.IsConnected)
                {
                    await _reader.ConnectAsync(stoppingToken);
                    backoff.Reset();
                    _logger.LogInformation("[{Identifier}] connected", _identifier);
                }

                var reading = await _reader.ReadAsync(stoppingToken);
                if (reading is null)
                    continue;

                if (_stableOnly && !reading.Stable)
                {
                    _logger.LogDebug("[{Identifier}] dropping unstable reading: {Raw}", _identifier, reading.Raw);
                    continue;
                }

                Stamp(reading);

                var now = DateTimeOffset.UtcNow;
                if (_dedup.IsDuplicate(reading, now))
                    continue;

                await _publisher.PublishAsync(reading, stoppingToken);
                _dedup.Commit(reading, now);

                _logger.LogInformation(
                    "[{Identifier}] published reading {ReadingId}: {Value} {Unit} (stable={Stable}, status={Status})",
                    _identifier, reading.ReadingId, reading.Value, reading.Unit, reading.Stable, reading.Status);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (DeviceConnectionException ex)
            {
                var delay = backoff.Next();
                _logger.LogWarning(ex, "[{Identifier}] device disconnected; reconnecting in {Delay}s",
                    _identifier, delay.TotalSeconds);
                await ReleaseDeadConnection();
                await DelaySafe(delay, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Identifier}] error in device forwarding loop", _identifier);
                await DelaySafe(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("CloudPrint device forwarding stopping: {Identifier}", _identifier);

        await _reader.DisposeAsync();
    }

    /// <summary>
    /// Drops the reader's handle after a connection failure so the next iteration re-enters
    /// ConnectAsync. Readers tear down on read errors themselves, but a handle can still look
    /// open after a failure (e.g. SerialPort.IsOpen stays true after a USB unplug), which would
    /// otherwise leave the loop re-reading a dead handle forever.
    /// </summary>
    private async Task ReleaseDeadConnection()
    {
        if (!_reader.IsConnected)
            return;

        try
        {
            await _reader.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{Identifier}] error releasing device handle", _identifier);
        }
    }

    private void Stamp(DeviceReading reading)
    {
        reading.Station = _station;
        reading.Host = MachineName;
        reading.DeviceId = _reader.DeviceId;
        reading.DeviceType = _reader.DeviceType;
        reading.Source = _reader.Source;
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down — swallow so the loop exits cleanly on the next condition check.
        }
    }
}

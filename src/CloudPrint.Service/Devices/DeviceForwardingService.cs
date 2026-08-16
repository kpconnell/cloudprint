using System.Text;
using System.Threading.Channels;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices.Commands;
using CloudPrint.Service.Devices.Framing;
using CloudPrint.Service.Publishing;

namespace CloudPrint.Service.Devices;

/// <summary>
/// Drives one device: connect → read → publish, with reconnect backoff and reading dedup.
/// The outbound analogue of <see cref="Transport.PrintJobPollingService"/>; reuses the same
/// resilience contract (per-resource identifier, OperationCanceled guard, 5s catch-all).
///
/// Beyond measurements it publishes lifecycle events so the cloud can see what it is talking to:
/// <c>connected</c> (with the reader's discovery metadata), <c>disconnected</c>, <c>stale</c> (no data
/// for a while), and <c>command-sent</c>/<c>command-failed</c> for cloud→device commands, which it
/// executes as they arrive (<see cref="IDeviceCommandTarget"/>) and correlates with the readings that
/// follow inside the command's reply window.
/// </summary>
public class DeviceForwardingService : BackgroundService, IDeviceCommandTarget
{
    private static readonly string MachineName = Environment.MachineName;

    private readonly IDeviceReader _reader;
    private readonly IReadingPublisher _publisher;
    private readonly ResolvedDevice _device;
    private readonly string _identifier;
    private readonly ILogger<DeviceForwardingService> _logger;
    private readonly ReadingDeduplicator _dedup;
    private readonly Channel<DeviceCommandMessage> _commands = Channel.CreateUnbounded<DeviceCommandMessage>();
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    private DateTimeOffset _lastDataAt = DateTimeOffset.UtcNow;
    private bool _staleAnnounced;
    private bool _wasConnected;
    private string? _activeCommandId;
    private DateTimeOffset _activeCommandUntil;

    public DeviceForwardingService(
        IDeviceReader reader,
        IReadingPublisher publisher,
        ResolvedDevice device,
        ILogger<DeviceForwardingService> logger)
    {
        _reader = reader;
        _publisher = publisher;
        _device = device;
        _identifier = $"device/{device.Name}";
        _logger = logger;
        _dedup = new ReadingDeduplicator(device.Heartbeat);
    }

    public string DeviceName => _device.Name;

    public bool TryEnqueue(DeviceCommandMessage message) => _commands.Writer.TryWrite(message);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CloudPrint device forwarding starting: {Identifier}", _identifier);

        var backoff = new ReconnectBackoff();
        var commandPump = PumpCommandsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_reader.IsConnected)
                {
                    await _reader.ConnectAsync(stoppingToken);
                    backoff.Reset();
                    _wasConnected = true;
                    _lastDataAt = DateTimeOffset.UtcNow;
                    _staleAnnounced = false;
                    _logger.LogInformation("[{Identifier}] connected", _identifier);
                    await PublishEventAsync("connected", _reader.Metadata, stoppingToken, swallow: true);
                }

                var reading = await _reader.ReadAsync(stoppingToken);
                if (reading is null)
                {
                    await CheckStaleAsync(stoppingToken);
                    continue;
                }

                _lastDataAt = DateTimeOffset.UtcNow;
                _staleAnnounced = false;

                if (_device.StableOnly && !reading.Stable && !reading.IsEvent)
                {
                    _logger.LogDebug("[{Identifier}] dropping unstable reading: {Raw}", _identifier, reading.Raw);
                    continue;
                }

                Stamp(reading);
                TagWithActiveCommand(reading);

                var now = DateTimeOffset.UtcNow;
                if (!reading.IsEvent && _dedup.IsDuplicate(reading, now))
                    continue;

                await PublishAsync(reading, stoppingToken);
                if (!reading.IsEvent)
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
                if (_wasConnected)
                {
                    _wasConnected = false;
                    await PublishEventAsync("disconnected", new Dictionary<string, string> { ["error"] = ex.Message }, stoppingToken, swallow: true);
                }
                await DelaySafe(delay, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Identifier}] error in device forwarding loop", _identifier);
                await DelaySafe(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("CloudPrint device forwarding stopping: {Identifier}", _identifier);

        _commands.Writer.TryComplete();
        try { await commandPump; } catch { /* pump ends with the loop */ }
        await _reader.DisposeAsync();
    }

    // ---- cloud → device commands -------------------------------------------------------------

    /// <summary>
    /// Executes commands as they arrive, independent of the read cycle (channels are full duplex), so a
    /// "measure now" is not delayed by a blocked read. Failures publish a command-failed event and never
    /// take the read loop down.
    /// </summary>
    private async Task PumpCommandsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _commands.Reader.ReadAllAsync(stoppingToken))
            {
                var id = message.Id ?? Guid.NewGuid().ToString();
                DeviceCommand command;
                try
                {
                    command = Resolve(message, id);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    await PublishEventAsync("command-failed", new Dictionary<string, string>
                        { ["commandId"] = id, ["error"] = $"invalid command: {ex.Message}" }, stoppingToken, swallow: true);
                    continue;
                }

                try
                {
                    if (!_reader.IsConnected)
                        throw new DeviceConnectionException("device is not connected");

                    await _reader.SendAsync(command.Payload, stoppingToken);

                    _activeCommandId = command.Id;
                    _activeCommandUntil = DateTimeOffset.UtcNow + command.ReplyWindow;

                    var meta = new Dictionary<string, string>
                    {
                        ["commandId"] = command.Id,
                        ["bytesHex"] = Convert.ToHexString(command.Payload),
                        ["command"] = command.Description
                    };
                    if (command.Metadata is not null)
                        foreach (var kv in command.Metadata) meta.TryAdd(kv.Key, kv.Value);

                    _logger.LogInformation("[{Identifier}] sent command {CommandId}: {Command}", _identifier, command.Id, command.Description);
                    await PublishEventAsync("command-sent", meta, stoppingToken, swallow: true);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{Identifier}] command {CommandId} failed", _identifier, command.Id);
                    await PublishEventAsync("command-failed", new Dictionary<string, string>
                        { ["commandId"] = command.Id, ["command"] = command.Description, ["error"] = ex.Message }, stoppingToken, swallow: true);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>Turns a wire message into exact bytes: base64 as-is, or text + escapes + terminator in the device's encoding.</summary>
    internal DeviceCommand Resolve(DeviceCommandMessage message, string id)
    {
        byte[] payload;
        string description;
        if (!string.IsNullOrWhiteSpace(message.BytesBase64))
        {
            payload = Convert.FromBase64String(message.BytesBase64.Trim());
            description = $"base64:{message.BytesBase64.Trim()}";
        }
        else if (message.Command is not null)
        {
            var text = ControlEscapes.Unescape(message.Command);
            var terminator = message.Terminator is null
                ? _device.EffectiveCommandTerminator
                : message.Terminator.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty
                : ControlEscapes.Unescape(message.Terminator);
            payload = ResolveEncoding(_device.Encoding).GetBytes(text + terminator);
            description = ControlEscapes.Describe(text + terminator);
        }
        else
        {
            throw new ArgumentException("command or bytesBase64 is required");
        }

        var window = TimeSpan.FromMilliseconds(message.ReplyWindowMs is > 0 ? message.ReplyWindowMs.Value : 5000);
        return new DeviceCommand(id, payload, window, message.Metadata, description);
    }

    private void TagWithActiveCommand(DeviceReading reading)
    {
        var id = _activeCommandId;
        if (id is null) return;
        if (DateTimeOffset.UtcNow > _activeCommandUntil)
        {
            _activeCommandId = null;
            return;
        }
        reading.Metadata ??= new Dictionary<string, string>();
        reading.Metadata.TryAdd("commandId", id);
    }

    // ---- lifecycle events -----------------------------------------------------------------------

    private async Task CheckStaleAsync(CancellationToken cancellationToken)
    {
        if (_device.StaleAfter is not { } after || _staleAnnounced)
            return;
        var silence = DateTimeOffset.UtcNow - _lastDataAt;
        if (silence < after)
            return;
        _staleAnnounced = true;
        _logger.LogWarning("[{Identifier}] no data for {Silence:0}s", _identifier, silence.TotalSeconds);
        await PublishEventAsync("stale", new Dictionary<string, string> { ["silentSeconds"] = ((int)silence.TotalSeconds).ToString() }, cancellationToken, swallow: true);
    }

    private async Task PublishEventAsync(string status, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken, bool swallow = false)
    {
        var evt = new DeviceReading
        {
            Status = status,
            Stable = false,
            Value = null,
            Metadata = metadata is null ? null : new Dictionary<string, string>(metadata)
        };
        Stamp(evt);
        try
        {
            await PublishAsync(evt, cancellationToken);
            _logger.LogInformation("[{Identifier}] published event {Status}", _identifier, status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (swallow)
        {
            _logger.LogWarning(ex, "[{Identifier}] failed to publish {Status} event", _identifier, status);
        }
    }

    // Readings and events can be published from two tasks (read loop, command pump); keep the publisher single-threaded.
    private async Task PublishAsync(DeviceReading reading, CancellationToken cancellationToken)
    {
        await _publishGate.WaitAsync(cancellationToken);
        try { await _publisher.PublishAsync(reading, cancellationToken); }
        finally { _publishGate.Release(); }
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
        reading.Station = _device.Station;
        reading.Host = MachineName;
        reading.DeviceId = _reader.DeviceId;
        reading.DeviceType = _reader.DeviceType;
        reading.Source = _reader.Source ?? new ReadingSource();
    }

    private static Encoding ResolveEncoding(string encoding) => encoding switch
    {
        "utf8" or "utf-8" => Encoding.UTF8,
        "latin1" or "iso-8859-1" or "binary" => Encoding.Latin1,
        _ => Encoding.ASCII
    };

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

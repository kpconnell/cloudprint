namespace CloudPrint.Service.Devices;

/// <summary>
/// Suppresses repeated identical readings. Scales emit the same stable weight many times per
/// second; without this the queue/webhook would be flooded. State is only committed after a
/// successful publish (the caller controls that), so a failed publish is retried next loop.
/// An optional heartbeat re-publishes an unchanged value after the interval elapses.
/// </summary>
public class ReadingDeduplicator
{
    private readonly TimeSpan? _heartbeat;
    private string? _lastKey;
    private DateTimeOffset _lastCommit;

    public ReadingDeduplicator(TimeSpan? heartbeat = null) => _heartbeat = heartbeat;

    public bool IsDuplicate(DeviceReading reading, DateTimeOffset now)
    {
        if (_lastKey is null || _lastKey != Key(reading))
            return false;

        // Same value as last published. Within the heartbeat window it's a duplicate;
        // once the window elapses, allow a re-publish. No heartbeat => always a duplicate.
        return _heartbeat is not { } hb || now - _lastCommit < hb;
    }

    public void Commit(DeviceReading reading, DateTimeOffset now)
    {
        _lastKey = Key(reading);
        _lastCommit = now;
    }

    // Raw is part of the key so generic passthrough (where Value/Unit are null) still forwards
    // every distinct frame. For scales, an unchanged stable weight has identical Raw too, so
    // repeats are still suppressed.
    private static string Key(DeviceReading r) =>
        $"{r.Value}|{r.Unit}|{r.Stable}|{r.Status}|{r.Raw}";
}

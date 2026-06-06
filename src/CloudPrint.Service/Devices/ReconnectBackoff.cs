namespace CloudPrint.Service.Devices;

/// <summary>
/// Capped exponential backoff for device reconnection: 1s → 2s → 5s → 10s → 30s (then steady).
/// Reset on a successful connect.
/// </summary>
public class ReconnectBackoff
{
    private static readonly TimeSpan[] Steps =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    };

    private int _index;

    public TimeSpan Next()
    {
        var delay = Steps[Math.Min(_index, Steps.Length - 1)];
        if (_index < Steps.Length - 1)
            _index++;
        return delay;
    }

    public void Reset() => _index = 0;
}

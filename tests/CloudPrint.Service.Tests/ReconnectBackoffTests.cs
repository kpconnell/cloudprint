using CloudPrint.Service.Devices;

namespace CloudPrint.Service.Tests;

public class ReconnectBackoffTests
{
    [Fact]
    public void Progresses_then_caps_at_thirty_seconds()
    {
        var backoff = new ReconnectBackoff();

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.Next());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.Next());
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.Next());
        Assert.Equal(TimeSpan.FromSeconds(10), backoff.Next());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.Next());
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.Next()); // capped
    }

    [Fact]
    public void Reset_returns_to_start()
    {
        var backoff = new ReconnectBackoff();
        backoff.Next();
        backoff.Next();

        backoff.Reset();

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.Next());
    }
}

using CloudPrint.Service.Devices;

namespace CloudPrint.Service.Tests;

public class ReadingDeduplicatorTests
{
    private static DeviceReading R(decimal value) =>
        new() { Value = value, Unit = "kg", Stable = true, Status = "ok" };

    [Fact]
    public void First_reading_is_not_duplicate()
    {
        var dedup = new ReadingDeduplicator();

        Assert.False(dedup.IsDuplicate(R(1m), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Same_value_after_commit_is_duplicate()
    {
        var dedup = new ReadingDeduplicator();
        dedup.Commit(R(1m), DateTimeOffset.UnixEpoch);

        Assert.True(dedup.IsDuplicate(R(1m), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Different_value_is_not_duplicate()
    {
        var dedup = new ReadingDeduplicator();
        dedup.Commit(R(1m), DateTimeOffset.UnixEpoch);

        Assert.False(dedup.IsDuplicate(R(2m), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Heartbeat_allows_republish_after_window()
    {
        var dedup = new ReadingDeduplicator(TimeSpan.FromSeconds(10));
        var t0 = DateTimeOffset.UnixEpoch;
        dedup.Commit(R(1m), t0);

        Assert.True(dedup.IsDuplicate(R(1m), t0.AddSeconds(5)));   // within window
        Assert.False(dedup.IsDuplicate(R(1m), t0.AddSeconds(11))); // window elapsed
    }
}

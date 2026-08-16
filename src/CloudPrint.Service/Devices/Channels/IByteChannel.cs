namespace CloudPrint.Service.Devices.Channels;

/// <summary>
/// A raw, bidirectional byte pipe to a device (COM port, TCP socket, ...). The framing/decoding layer
/// (<see cref="Readers.FramedDeviceReader"/>) is transport-agnostic and only sees this.
/// Contract: <see cref="ReadAsync"/> returns 0 when nothing arrived within <c>timeout</c> (not an error),
/// and throws <see cref="IOException"/> once the channel is dead (unplug, peer closed, port vanished).
/// </summary>
public interface IByteChannel : IAsyncDisposable
{
    /// <summary>Human-readable endpoint ("COM3 9600 8N1", "tcp 10.1.100.100:1050") for logs and readings.</summary>
    string Description { get; }

    /// <summary>Discovery facts about the endpoint (friendly name, VID/PID, remote address...) stamped on the connected event.</summary>
    IReadOnlyDictionary<string, string> Metadata { get; }

    Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken);

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

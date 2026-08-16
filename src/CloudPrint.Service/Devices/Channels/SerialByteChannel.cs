#if WINDOWS
using System.IO.Ports;
using CloudPrint.Service.Configuration;

namespace CloudPrint.Service.Devices.Channels;

/// <summary>
/// COM-port channel (RS-232 or USB virtual COM) over System.IO.Ports. Windows opens COM ports exclusively,
/// so only one process (service or configurator preview) can hold a port at a time. Reads are blocking
/// with the port's ReadTimeout — on Windows that returns as soon as any bytes are buffered, else waits
/// the timeout — which is exactly what idle-gap framing needs.
/// </summary>
public sealed class SerialByteChannel : IByteChannel
{
    private readonly SerialPort _port;
    private int _currentTimeoutMs;

    private SerialByteChannel(SerialPort port, ResolvedDevice device, string portName, IReadOnlyDictionary<string, string> metadata)
    {
        _port = port;
        Description = $"{portName} {device.BaudRate} {device.DataBits}{device.Parity[0]}{device.StopBits}";
        Metadata = metadata;
    }

    public string Description { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public static SerialByteChannel Open(ResolvedDevice device)
    {
        var portName = SerialPortLocator.Resolve(device);
        var port = new SerialPort(portName, device.BaudRate, ParseParity(device.Parity), device.DataBits, ParseStopBits(device.StopBits))
        {
            ReadTimeout = (int)device.ReadTimeout.TotalMilliseconds,
            WriteTimeout = 2000,
            Handshake = Handshake.None,
            DtrEnable = device.DtrEnable,   // some USB-serial devices only talk once DTR/RTS are asserted; off by default
            RtsEnable = device.RtsEnable
        };

        try
        {
            port.Open();
            try { port.DiscardInBuffer(); } catch { /* not all drivers support it */ }
        }
        catch
        {
            try { port.Dispose(); } catch { /* the handle is gone either way */ }
            throw;
        }

        var meta = new Dictionary<string, string> { ["comPort"] = portName };
        foreach (var kv in SerialPortLocator.Describe(portName))
            meta[kv.Key] = kv.Value;

        return new SerialByteChannel(port, device, portName, meta);
    }

    public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // SerialPort has no cancellable async read; the timeout bounds the blocking call.
        return Task.Run(() =>
        {
            var ms = Math.Max(1, (int)timeout.TotalMilliseconds);
            if (ms != _currentTimeoutMs)
            {
                _port.ReadTimeout = ms;
                _currentTimeoutMs = ms;
            }

            var array = new byte[buffer.Length];
            try
            {
                var n = _port.Read(array, 0, array.Length);
                array.AsSpan(0, n).CopyTo(buffer.Span);
                return n;
            }
            catch (TimeoutException)
            {
                return 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // USB-serial unplug: IsOpen stays true but every read fails.
                throw new IOException($"{Description}: read failed ({ex.Message})", ex);
            }
        }, cancellationToken);
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            var arr = data.ToArray();
            _port.Write(arr, 0, arr.Length);
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or TimeoutException or ObjectDisposedException)
        {
            throw new IOException($"{Description}: write failed ({ex.Message})", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _port.Dispose(); } catch { /* disposing a surprise-removed device can throw */ }
        return ValueTask.CompletedTask;
    }

    private static Parity ParseParity(string parity) => parity.ToLowerInvariant() switch
    {
        "even" => Parity.Even,
        "odd" => Parity.Odd,
        "mark" => Parity.Mark,
        "space" => Parity.Space,
        _ => Parity.None
    };

    private static StopBits ParseStopBits(int stopBits) => stopBits switch
    {
        2 => StopBits.Two,
        _ => StopBits.One
    };
}
#endif

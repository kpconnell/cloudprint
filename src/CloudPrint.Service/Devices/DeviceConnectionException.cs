namespace CloudPrint.Service.Devices;

/// <summary>
/// Raised when a device cannot be opened or a read fails because the connection is gone.
/// The hosted service treats this as a reconnectable fault (backoff + retry), not a crash.
/// </summary>
public class DeviceConnectionException : Exception
{
    public DeviceConnectionException(string message) : base(message)
    {
    }

    public DeviceConnectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

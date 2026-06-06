using CloudPrint.Service.Devices;

namespace CloudPrint.Service.Publishing;

/// <summary>
/// Publishes a device reading outbound. The outbound transport seam — implemented by SQS and HTTP.
/// </summary>
public interface IReadingPublisher
{
    Task PublishAsync(DeviceReading reading, CancellationToken cancellationToken);
}

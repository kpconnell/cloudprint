namespace CloudPrint.Service.Devices.Commands;

public sealed class DeviceCommandEnvelope
{
    public required string Id { get; init; }
    public required DeviceCommandMessage Message { get; init; }
    public string? ReceiptHandle { get; init; }
}

/// <summary>Where cloud→device commands come from (SQS today; the inbound analogue of <see cref="Transport.IJobSource"/>).</summary>
public interface IDeviceCommandSource
{
    Task<DeviceCommandEnvelope?> ReceiveAsync(CancellationToken cancellationToken);
    Task AcknowledgeAsync(DeviceCommandEnvelope envelope, CancellationToken cancellationToken);
}

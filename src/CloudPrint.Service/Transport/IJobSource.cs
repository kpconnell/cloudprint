namespace CloudPrint.Service.Transport;

public interface IJobSource
{
    Task<JobEnvelope?> ReceiveAsync(CancellationToken cancellationToken);
    Task AcknowledgeAsync(JobEnvelope envelope, bool success, string? error, CancellationToken cancellationToken);
}

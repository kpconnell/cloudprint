namespace CloudPrint.Service.Transport;

public interface IJobSource
{
    Task<JobEnvelope?> ReceiveAsync(CancellationToken cancellationToken);
    Task AcknowledgeAsync(string jobId, bool success, string? error, CancellationToken cancellationToken);
}

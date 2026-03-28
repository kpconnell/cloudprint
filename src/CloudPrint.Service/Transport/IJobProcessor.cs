namespace CloudPrint.Service.Transport;

public interface IJobProcessor
{
    Task<(bool Success, string? Error)> ProcessAsync(
        string jobId, PrintJobMessage job, CancellationToken cancellationToken);
}

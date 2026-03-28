namespace CloudPrint.Service.Transport;

public class JobEnvelope
{
    public required string Id { get; set; }
    public required PrintJobMessage Job { get; set; }
    public string? ReceiptHandle { get; set; }
}

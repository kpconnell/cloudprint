namespace CloudPrint.Service.Configuration;

public class CloudPrintOptions
{
    public const string SectionName = "CloudPrint";

    public string QueueUrl { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string AwsAccessKeyId { get; set; } = string.Empty;
    public string AwsSecretAccessKey { get; set; } = string.Empty;
    public string PrinterName { get; set; } = string.Empty;
    public int VisibilityTimeoutSeconds { get; set; } = 300;
}

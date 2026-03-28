namespace CloudPrint.Service.Configuration;

public class CloudPrintOptions
{
    public const string SectionName = "CloudPrint";

    // Transport: "sqs" or "http"
    public string Transport { get; set; } = "sqs";

    // SQS settings
    public string QueueUrl { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string AwsAccessKeyId { get; set; } = string.Empty;
    public string AwsSecretAccessKey { get; set; } = string.Empty;
    public int VisibilityTimeoutSeconds { get; set; } = 300;

    // HTTP API settings
    public string ApiUrl { get; set; } = string.Empty;
    public string AckUrl { get; set; } = string.Empty;
    public string ApiHeaderName { get; set; } = "X-Api-Key";
    public string ApiHeaderValue { get; set; } = string.Empty;
    public int HttpPollTimeoutSeconds { get; set; } = 30;

    // Shared
    public string PrinterName { get; set; } = string.Empty;
}

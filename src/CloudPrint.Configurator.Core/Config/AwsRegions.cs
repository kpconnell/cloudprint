namespace CloudPrint.Configurator.Core.Config;

/// <summary>The AWS regions offered during configuration (mirrors scripts/install.ps1).</summary>
public sealed record AwsRegion(string Id, string Name);

public static class AwsRegions
{
    public static readonly IReadOnlyList<AwsRegion> All = new[]
    {
        new AwsRegion("us-east-1", "US East (N. Virginia)"),
        new AwsRegion("us-east-2", "US East (Ohio)"),
        new AwsRegion("us-west-1", "US West (N. California)"),
        new AwsRegion("us-west-2", "US West (Oregon)"),
        new AwsRegion("ca-central-1", "Canada (Central)"),
        new AwsRegion("eu-west-1", "Europe (Ireland)"),
        new AwsRegion("eu-west-2", "Europe (London)"),
        new AwsRegion("eu-central-1", "Europe (Frankfurt)"),
        new AwsRegion("ap-southeast-1", "Asia Pacific (Singapore)"),
        new AwsRegion("ap-southeast-2", "Asia Pacific (Sydney)"),
        new AwsRegion("ap-northeast-1", "Asia Pacific (Tokyo)"),
    };
}

using Amazon;

namespace CloudPrint.Configurator.Core.Config;

/// <summary>The AWS regions offered during configuration.</summary>
public sealed record AwsRegion(string Id, string Name);

public static class AwsRegions
{
    /// <summary>Falls back here whenever no region has been selected or saved.</summary>
    public const string DefaultId = "us-east-1";

    // Sourced from the AWS SDK's own offline region table instead of a hand-maintained list, so new
    // regions show up automatically on the next SDK bump. Limited to the standard "aws" partition —
    // China/GovCloud/ISO partitions need separate, dedicated AWS accounts, so offering them here would
    // just fail confusingly against a normal IAM key/secret. "us-east-1-regional" is an STS endpoint
    // alias, not a real region, so it's excluded too.
    public static readonly IReadOnlyList<AwsRegion> All = RegionEndpoint.EnumerableAllRegions
        .Where(r => r.PartitionName == "aws" && r.SystemName != "us-east-1-regional")
        .Select(r => new AwsRegion(r.SystemName, r.DisplayName))
        .OrderBy(r => r.Id, StringComparer.Ordinal)
        .ToList();
}

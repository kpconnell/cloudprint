using System.Text.RegularExpressions;

namespace CloudPrint.Configurator.Core.Config;

/// <summary>
/// Builds SQS queue names from station/printer/device identifiers:
/// "cloudprint-{prefix}-{sanitized-name}", strictly lowercase alphanumeric and single
/// hyphens (runs of anything else collapse to one '-'), capped so the "-dlq" suffix
/// fits SQS's 80-char limit. <see cref="Sanitize"/> is also applied to queue tag
/// values — SQS rejects most punctuation there too (e.g. parentheses in printer names).
/// </summary>
public static partial class QueueNaming
{
    public const string Prefix = "cloudprint";

    // 76 leaves room for the 4-char "-dlq" suffix on the dead-letter queue (SQS limit is 80).
    public const int MaxLength = 76;

    /// <summary>Queue name for an inbound printer lane on a given host.</summary>
    public static string ForPrinter(string hostname, string printerName) => Build(hostname, printerName);

    /// <summary>Queue name for an outbound device on a given station.</summary>
    public static string ForDevice(string station, string deviceName) => Build(station, deviceName);

    /// <summary>Queue name for the station's inbound device-command queue (cloud → devices).</summary>
    public static string ForDeviceCommands(string station) => Build(station, "device-commands");

    /// <summary>The matching dead-letter queue name.</summary>
    public static string DeadLetter(string queueName) => $"{queueName}-dlq";

    /// <summary>The list-queues prefix that matches every queue for a host/station.</summary>
    public static string PrefixFor(string hostOrStation) => $"{Prefix}-{Sanitize(hostOrStation)}-";

    /// <summary>
    /// Lowercase alphanumeric-and-dash form of an identifier: any run of other characters
    /// becomes a single '-', leading/trailing dashes are trimmed. Safe for both queue
    /// names and queue tag values.
    /// </summary>
    public static string Sanitize(string value) =>
        NonNameRuns().Replace(value.ToLowerInvariant(), "-").Trim('-');

    private static string Build(string prefix, string name)
    {
        var parts = new[] { Prefix, Sanitize(prefix), Sanitize(name) }.Where(p => p.Length > 0);
        var result = string.Join("-", parts);
        return result.Length > MaxLength ? result[..MaxLength].TrimEnd('-') : result;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonNameRuns();
}

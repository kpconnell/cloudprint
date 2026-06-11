using System.Text.RegularExpressions;

namespace CloudPrint.Configurator.Core.Config;

/// <summary>
/// Builds SQS queue names from station/printer/device identifiers:
/// "cloudprint-{prefix}-{sanitized-name}", lowercased,
/// non-alphanumeric runs collapsed to hyphens, capped so the "-dlq" suffix fits SQS's 80-char limit.
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

    /// <summary>The matching dead-letter queue name.</summary>
    public static string DeadLetter(string queueName) => $"{queueName}-dlq";

    /// <summary>The list-queues prefix that matches every queue for a host/station.</summary>
    public static string PrefixFor(string hostOrStation) => $"{Prefix}-{hostOrStation.ToLowerInvariant()}-";

    private static string Build(string prefix, string name)
    {
        var safeName = NonNameChars().Replace(name, "-").ToLowerInvariant().TrimEnd('-');
        var result = $"{Prefix}-{prefix.ToLowerInvariant()}-{safeName}";
        return result.Length > MaxLength ? result[..MaxLength] : result;
    }

    [GeneratedRegex("[^a-zA-Z0-9\\-]")]
    private static partial Regex NonNameChars();
}

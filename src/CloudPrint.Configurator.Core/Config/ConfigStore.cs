using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudPrint.Configurator.Core.Config;

/// <summary>
/// Reads and writes the service's appsettings.json. On write it emits the full file
/// ({ CloudPrint, Serilog }) with the Serilog block derived from <see cref="CloudPrintConfig.DumpPayloads"/>.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Local config file (not HTML): keep '+' and '/' in AWS secrets/URLs literal rather than \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Loads the CloudPrint section from a file, or null if the file is missing.</summary>
    public static CloudPrintConfig? Load(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : null;

    /// <summary>Parses the CloudPrint section out of a full appsettings.json document.</summary>
    public static CloudPrintConfig? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, DocOptions);
        if (!doc.RootElement.TryGetProperty("CloudPrint", out var section))
            return null;
        return section.Deserialize<CloudPrintConfig>(ReadOptions);
    }

    /// <summary>Builds the full appsettings.json text for the given config (no file IO).</summary>
    public static string BuildJson(CloudPrintConfig config)
    {
        var root = new Dictionary<string, object>
        {
            ["CloudPrint"] = config,
            ["Serilog"] = BuildSerilog(config.DumpPayloads),
        };
        return JsonSerializer.Serialize(root, WriteOptions);
    }

    /// <summary>Writes the full appsettings.json to disk (UTF-8, no BOM).</summary>
    public static void Save(string path, CloudPrintConfig config)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, BuildJson(config));
    }

    private static object BuildSerilog(bool dumpPayloads) => new
    {
        MinimumLevel = new
        {
            Default = dumpPayloads ? "Debug" : "Information",
            Override = new { Microsoft = "Warning", System = "Warning" },
        },
        WriteTo = new object[]
        {
            new { Name = "Console" },
            new
            {
                Name = "File",
                Args = new
                {
                    path = @"C:\ProgramData\CloudPrint\logs\cloudprint-.log",
                    rollingInterval = "Day",
                    retainedFileCountLimit = 30,
                },
            },
        },
    };
}

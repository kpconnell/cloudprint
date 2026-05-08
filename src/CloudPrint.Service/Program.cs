using System.Text.Json;
using Amazon;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.FileHandling;
using CloudPrint.Service.Printing;
using CloudPrint.Service.Transport;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Settings.Configuration;
using Serilog.Sinks.File;

// --- CLI commands for install script (input via stdin as JSON) ---
if (args.Length > 0)
{
    var command = args[0].ToLowerInvariant();

    if (command is "verify-creds" or "create-queue" or "list-queues" or "delete-queue")
    {
        var input = await Console.In.ReadToEndAsync();
        var cliArgs = JsonSerializer.Deserialize<CliInput>(input,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (cliArgs is null || string.IsNullOrWhiteSpace(cliArgs.AccessKey)
            || string.IsNullOrWhiteSpace(cliArgs.SecretKey) || string.IsNullOrWhiteSpace(cliArgs.Region))
        {
            Console.Error.WriteLine("Expected JSON on stdin: {\"accessKey\":\"...\",\"secretKey\":\"...\",\"region\":\"...\",...}");
            return 1;
        }

        switch (command)
        {
            case "verify-creds":
                return await VerifyCredentials(cliArgs.AccessKey, cliArgs.SecretKey, cliArgs.Region);

            case "create-queue":
                if (string.IsNullOrWhiteSpace(cliArgs.QueueName))
                {
                    Console.Error.WriteLine("queueName is required for create-queue");
                    return 1;
                }
                return await CreateQueue(cliArgs.QueueName, cliArgs.Tags, cliArgs.AccessKey, cliArgs.SecretKey, cliArgs.Region);

            case "list-queues":
                if (string.IsNullOrWhiteSpace(cliArgs.QueueName))
                {
                    Console.Error.WriteLine("queueName (used as prefix) is required for list-queues");
                    return 1;
                }
                return await ListQueues(cliArgs.QueueName, cliArgs.AccessKey, cliArgs.SecretKey, cliArgs.Region);

            case "delete-queue":
                if (string.IsNullOrWhiteSpace(cliArgs.QueueUrl))
                {
                    Console.Error.WriteLine("queueUrl is required for delete-queue");
                    return 1;
                }
                return await DeleteQueue(cliArgs.QueueUrl, cliArgs.AccessKey, cliArgs.SecretKey, cliArgs.Region);
        }
    }
}

// --- Normal service startup ---
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Explicit assembly references required for single-file publish
    var readerOptions = new ConfigurationReaderOptions(
        typeof(Serilog.ConsoleLoggerConfigurationExtensions).Assembly,
        typeof(FileLoggerConfigurationExtensions).Assembly);

    builder.Services.AddSerilog(config => config
        .ReadFrom.Configuration(builder.Configuration, readerOptions));

    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "CloudPrint";
        });
    }

    builder.Services.Configure<CloudPrintOptions>(
        builder.Configuration.GetSection(CloudPrintOptions.SectionName));

    var cloudPrintOptions = builder.Configuration
        .GetSection(CloudPrintOptions.SectionName)
        .Get<CloudPrintOptions>() ?? new CloudPrintOptions();

    builder.Services.AddHttpClient<FileDownloader>();

    var dryRun = args.Contains("--dry-run") || !OperatingSystem.IsWindows();
    if (dryRun)
    {
        Log.Information("Running in dry-run mode — print jobs will be logged, not printed");
        builder.Services.AddSingleton<DryRunPrinter>();
        builder.Services.AddSingleton<IRawPrinter>(sp => sp.GetRequiredService<DryRunPrinter>());
        builder.Services.AddSingleton<IDocumentPrinter>(sp => sp.GetRequiredService<DryRunPrinter>());
        builder.Services.AddSingleton<IPdfPrinter>(sp => sp.GetRequiredService<DryRunPrinter>());
    }
#if WINDOWS
    else
    {
        builder.Services.AddSingleton<IRawPrinter, RawPrinter>();
        builder.Services.AddSingleton<IDocumentPrinter, DocumentPrinter>();
        builder.Services.AddSingleton<IPdfPrinter, PdfPrinter>();
    }
#endif

    builder.Services.AddSingleton<PrintRouter>();

    // Register transport-specific services
    var transport = cloudPrintOptions.Transport?.ToLowerInvariant() ?? "sqs";
    switch (transport)
    {
        case "sqs":
            RegisterSqsLanes(builder, cloudPrintOptions);
            break;
        case "http":
            RegisterHttpTransport(builder, cloudPrintOptions);
            break;
        default:
            throw new InvalidOperationException($"Unknown transport: {transport}. Use 'sqs' or 'http'.");
    }

    var host = builder.Build();
    host.Run();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "CloudPrint service terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

// --- DI registration helpers ---

static void RegisterSqsLanes(HostApplicationBuilder builder, CloudPrintOptions options)
{
    var lanes = options.ResolvedSqsLanes();
    if (lanes.Count == 0)
    {
        throw new InvalidOperationException(
            "SQS transport requires either Printers[] entries or legacy QueueUrl + PrinterName at the top level.");
    }

    Log.Information("CloudPrint configured for {LaneCount} SQS lane(s): {Printers}",
        lanes.Count, string.Join(", ", lanes.Select(l => l.PrinterName)));

    builder.Services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(
        options.AwsAccessKeyId,
        options.AwsSecretAccessKey,
        RegionEndpoint.GetBySystemName(options.Region)));

    foreach (var lane in lanes)
    {
        var capturedLane = lane;
        builder.Services.AddSingleton<IHostedService>(sp =>
        {
            var sqs = sp.GetRequiredService<IAmazonSQS>();
            var fileDownloader = sp.GetRequiredService<FileDownloader>();
            var router = sp.GetRequiredService<PrintRouter>();
            var configOptions = sp.GetRequiredService<IOptions<CloudPrintOptions>>();

            var source = new SqsJobSource(
                sqs,
                capturedLane,
                options.VisibilityTimeoutSeconds,
                sp.GetRequiredService<ILogger<SqsJobSource>>());

            var processor = new JobProcessor(
                capturedLane,
                configOptions,
                fileDownloader,
                router,
                sp.GetRequiredService<ILogger<JobProcessor>>());

            return new PrintJobPollingService(
                source,
                processor,
                $"sqs/{capturedLane.PrinterName}",
                sp.GetRequiredService<ILogger<PrintJobPollingService>>());
        });
    }
}

static void RegisterHttpTransport(HostApplicationBuilder builder, CloudPrintOptions options)
{
    if (string.IsNullOrWhiteSpace(options.PrinterName))
    {
        throw new InvalidOperationException(
            "HTTP transport requires PrinterName at the top level (HTTP is single-printer only).");
    }

    Log.Information("CloudPrint configured for HTTP transport, printer: {Printer}", options.PrinterName);

    // Synthesize a single resolved lane from the top-level config so JobProcessor stays uniform.
    var httpLane = new ResolvedLane(
        PrinterName: options.PrinterName,
        QueueUrl: string.Empty,
        PdfRenderDpi: options.PdfRenderDpi,
        PdfFitMode: options.PdfFitMode);

    builder.Services.AddHttpClient<HttpApiJobSource>();
    builder.Services.AddSingleton<IJobSource>(sp => sp.GetRequiredService<HttpApiJobSource>());

    builder.Services.AddSingleton<IJobProcessor>(sp => new JobProcessor(
        httpLane,
        sp.GetRequiredService<IOptions<CloudPrintOptions>>(),
        sp.GetRequiredService<FileDownloader>(),
        sp.GetRequiredService<PrintRouter>(),
        sp.GetRequiredService<ILogger<JobProcessor>>()));

    builder.Services.AddSingleton<IHostedService>(sp => new PrintJobPollingService(
        sp.GetRequiredService<IJobSource>(),
        sp.GetRequiredService<IJobProcessor>(),
        $"http/{httpLane.PrinterName}",
        sp.GetRequiredService<ILogger<PrintJobPollingService>>()));
}

// --- CLI helper methods ---

static async Task<int> VerifyCredentials(string accessKey, string secretKey, string region)
{
    try
    {
        using var stsClient = new AmazonSecurityTokenServiceClient(
            accessKey, secretKey, RegionEndpoint.GetBySystemName(region));

        var response = await stsClient.GetCallerIdentityAsync(new GetCallerIdentityRequest());
        Console.WriteLine(response.Arn);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task<int> CreateQueue(string queueName, Dictionary<string, string>? tags, string accessKey, string secretKey, string region)
{
    try
    {
        using var sqsClient = new AmazonSQSClient(
            accessKey, secretKey, RegionEndpoint.GetBySystemName(region));

        // Create DLQ first
        var dlqName = $"{queueName}-dlq";
        var dlqResponse = await sqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = dlqName,
            Tags = tags ?? new Dictionary<string, string>()
        });

        // Get DLQ ARN
        var dlqAttributes = await sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqResponse.QueueUrl,
            AttributeNames = ["QueueArn"]
        });
        var dlqArn = dlqAttributes.Attributes["QueueArn"];

        // Create or update main queue with redrive policy
        string queueUrl;
        try
        {
            var response = await sqsClient.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName,
                Attributes = new Dictionary<string, string>
                {
                    ["RedrivePolicy"] = JsonSerializer.Serialize(new { deadLetterTargetArn = dlqArn, maxReceiveCount = 5 })
                },
                Tags = tags ?? new Dictionary<string, string>()
            });
            queueUrl = response.QueueUrl;
        }
        catch (AmazonSQSException ex) when (ex.ErrorCode == "QueueAlreadyExists")
        {
            var urlResponse = await sqsClient.GetQueueUrlAsync(queueName);
            queueUrl = urlResponse.QueueUrl;

            await sqsClient.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                Attributes = new Dictionary<string, string>
                {
                    ["RedrivePolicy"] = JsonSerializer.Serialize(new { deadLetterTargetArn = dlqArn, maxReceiveCount = 5 })
                }
            });

            // Apply tags to the existing queue (additive — doesn't remove other tags)
            if (tags is { Count: > 0 })
            {
                await sqsClient.TagQueueAsync(new TagQueueRequest
                {
                    QueueUrl = queueUrl,
                    Tags = tags
                });
            }
        }

        Console.WriteLine(queueUrl);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task<int> ListQueues(string prefix, string accessKey, string secretKey, string region)
{
    try
    {
        using var sqsClient = new AmazonSQSClient(
            accessKey, secretKey, RegionEndpoint.GetBySystemName(region));

        var response = await sqsClient.ListQueuesAsync(new ListQueuesRequest
        {
            QueueNamePrefix = prefix,
            MaxResults = 1000
        });

        if (response.QueueUrls is null) return 0;

        foreach (var url in response.QueueUrls)
        {
            Console.WriteLine(url);
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task<int> DeleteQueue(string queueUrl, string accessKey, string secretKey, string region)
{
    try
    {
        using var sqsClient = new AmazonSQSClient(
            accessKey, secretKey, RegionEndpoint.GetBySystemName(region));

        await sqsClient.DeleteQueueAsync(queueUrl);
        Console.WriteLine(queueUrl);
        return 0;
    }
    catch (AmazonSQSException ex) when (ex.ErrorCode == "AWS.SimpleQueueService.NonExistentQueue")
    {
        // Already gone — treat as success so reinstall is idempotent
        Console.WriteLine(queueUrl);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

// --- CLI input model ---
class CliInput
{
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Region { get; set; } = "";
    public string QueueName { get; set; } = "";
    public string QueueUrl { get; set; } = "";
    public Dictionary<string, string>? Tags { get; set; }
}

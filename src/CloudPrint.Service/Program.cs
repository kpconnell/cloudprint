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
using Serilog;
using Serilog.Settings.Configuration;
using Serilog.Sinks.File;

// --- CLI commands for install script (credentials via stdin as JSON) ---
if (args.Length > 0)
{
    var command = args[0].ToLowerInvariant();

    if (command == "verify-creds" || command == "create-queue")
    {
        var input = await Console.In.ReadToEndAsync();
        var cliArgs = JsonSerializer.Deserialize<CliInput>(input,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (cliArgs is null || string.IsNullOrWhiteSpace(cliArgs.AccessKey)
            || string.IsNullOrWhiteSpace(cliArgs.SecretKey) || string.IsNullOrWhiteSpace(cliArgs.Region))
        {
            Console.Error.WriteLine("Expected JSON on stdin: {\"accessKey\":\"...\",\"secretKey\":\"...\",\"region\":\"...\",\"queueName\":\"...\"}");
            return 1;
        }

        if (command == "verify-creds")
            return await VerifyCredentials(cliArgs.AccessKey, cliArgs.SecretKey, cliArgs.Region);

        if (command == "create-queue")
        {
            if (string.IsNullOrWhiteSpace(cliArgs.QueueName))
            {
                Console.Error.WriteLine("queueName is required for create-queue");
                return 1;
            }
            return await CreateQueue(cliArgs.QueueName, cliArgs.AccessKey, cliArgs.SecretKey, cliArgs.Region);
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

    // Register transport based on config
    var transport = cloudPrintOptions.Transport?.ToLowerInvariant() ?? "sqs";
    switch (transport)
    {
        case "sqs":
            builder.Services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(
                cloudPrintOptions.AwsAccessKeyId,
                cloudPrintOptions.AwsSecretAccessKey,
                RegionEndpoint.GetBySystemName(cloudPrintOptions.Region)));
            builder.Services.AddSingleton<IJobSource, SqsJobSource>();
            break;
        case "http":
            builder.Services.AddHttpClient<HttpApiJobSource>();
            builder.Services.AddSingleton<IJobSource>(sp => sp.GetRequiredService<HttpApiJobSource>());
            break;
        default:
            throw new InvalidOperationException($"Unknown transport: {transport}. Use 'sqs' or 'http'.");
    }

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
    builder.Services.AddSingleton<IJobProcessor, JobProcessor>();
    builder.Services.AddHostedService<PrintJobPollingService>();

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

static async Task<int> CreateQueue(string queueName, string accessKey, string secretKey, string region)
{
    try
    {
        using var sqsClient = new AmazonSQSClient(
            accessKey, secretKey, RegionEndpoint.GetBySystemName(region));

        // Create DLQ first
        var dlqName = $"{queueName}-dlq";
        var dlqResponse = await sqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = dlqName
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
                }
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

// --- CLI input model ---
class CliInput
{
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Region { get; set; } = "";
    public string QueueName { get; set; } = "";
}

using Amazon;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.FileHandling;
using CloudPrint.Service.Printing;
using CloudPrint.Service.Sqs;
using Serilog;
using Serilog.Settings.Configuration;
using Serilog.Sinks.File;

// --- CLI commands for install script ---
if (args.Length > 0)
{
    var command = args[0].ToLowerInvariant();

    if (command == "verify-creds")
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: CloudPrint.Service verify-creds <access-key> <secret-key> <region>");
            return 1;
        }
        return await VerifyCredentials(args[1], args[2], args[3]);
    }

    if (command == "create-queue")
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("Usage: CloudPrint.Service create-queue <queue-name> <access-key> <secret-key> <region>");
            return 1;
        }
        return await CreateQueue(args[1], args[2], args[3], args[4]);
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

    builder.Services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(
        cloudPrintOptions.AwsAccessKeyId,
        cloudPrintOptions.AwsSecretAccessKey,
        RegionEndpoint.GetBySystemName(cloudPrintOptions.Region)));

    builder.Services.AddHttpClient<FileDownloader>();

    var dryRun = args.Contains("--dry-run") || !OperatingSystem.IsWindows();
    if (dryRun)
    {
        Log.Information("Running in dry-run mode — print jobs will be logged, not printed");
        builder.Services.AddSingleton<DryRunPrinter>();
        builder.Services.AddSingleton<IRawPrinter>(sp => sp.GetRequiredService<DryRunPrinter>());
        builder.Services.AddSingleton<IDocumentPrinter>(sp => sp.GetRequiredService<DryRunPrinter>());
    }
#if WINDOWS
    else
    {
        builder.Services.AddSingleton<IRawPrinter, RawPrinter>();
        builder.Services.AddSingleton<IDocumentPrinter, DocumentPrinter>();
    }
#endif

    builder.Services.AddSingleton<PrintRouter>();
    builder.Services.AddHostedService<SqsPollingService>();

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

        var response = await sqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName
        });

        Console.WriteLine(response.QueueUrl);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

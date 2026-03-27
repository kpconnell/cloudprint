using Amazon;
using Amazon.SQS;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.FileHandling;
using CloudPrint.Service.Printing;
using CloudPrint.Service.Sqs;
using Serilog;
using Serilog.Settings.Configuration;
using Serilog.Sinks.File;

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
}
catch (Exception ex)
{
    Log.Fatal(ex, "CloudPrint service terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

using System.Reflection;
using System.Text.Json;
using Amazon;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices;
using CloudPrint.Service.Devices.Commands;
using CloudPrint.Service.Devices.Readers;
using CloudPrint.Service.FileHandling;
using CloudPrint.Service.Printing;
using CloudPrint.Service.Publishing;
using CloudPrint.Service.Transport;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Settings.Configuration;
using Serilog.Sinks.File;

// --- Device enumeration helper (no credentials needed) ---
if (args.Length > 0 && args[0].Equals("list-devices", StringComparison.OrdinalIgnoreCase))
    return ListDevices();

// --- Live device reading preview (config on stdin, no credentials needed) ---
if (args.Length > 0 && args[0].Equals("preview-device", StringComparison.OrdinalIgnoreCase))
    return await PreviewDevice();

// --- Test helpers: print a ZPL test label / send a "Hello" message to a device's output ---
if (args.Length > 0 && args[0].Equals("test-print", StringComparison.OrdinalIgnoreCase))
    return TestPrint();
if (args.Length > 0 && args[0].Equals("test-output", StringComparison.OrdinalIgnoreCase))
    return await TestOutput();

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

    // Device telemetry runs independently of the print transport — whenever Devices[] is configured.
    RegisterDeviceForwarding(builder, cloudPrintOptions, dryRun);

    var host = builder.Build();
    // Logged after Build() so it reaches the configured file sink, not just the bootstrap console.
    Log.Information("CloudPrint service starting — version {Version}", ServiceVersion());
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

// Version stamped by CI (-p:Version from the release tag); default 1.0.0 in dev builds.
static string ServiceVersion() =>
    Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0]
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
    ?? "unknown";

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
        lanes.Count, string.Join(", ", lanes.Select(l =>
            $"{l.PrinterName} ({l.PdfRenderDpi} DPI, {l.PdfFitMode}, " +
            $"paper={(string.IsNullOrEmpty(l.PdfPaperSize) ? "driver default" : l.PdfPaperSize)}" +
            $"{(l.PdfMonochrome ? ", B/W" : "")})")));

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
        PdfFitMode: options.PdfFitMode,
        PdfMonochrome: options.PdfMonochrome,
        PdfPaperSize: options.PdfPaperSize);

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

static void RegisterDeviceForwarding(HostApplicationBuilder builder, CloudPrintOptions options, bool dryRun)
{
    var devices = options.ResolvedDevices();
    if (devices.Count == 0)
        return;

    Log.Information("CloudPrint configured for {Count} device(s): {Devices}",
        devices.Count, string.Join(", ", devices.Select(d => $"{d.Type}/{d.Name}")));

    // The simulated reader is always available (dry-run, or Windows-only device types off Windows).
    // TCP readers are cross-platform and run for real everywhere unless --dry-run was given.
    builder.Services.AddSingleton<IDeviceReaderFactory, SimulatedDeviceReaderFactory>();
    var explicitDryRun = dryRun && OperatingSystem.IsWindows() || Environment.GetCommandLineArgs().Contains("--dry-run");
    if (!explicitDryRun)
    {
        builder.Services.AddSingleton<IDeviceReaderFactory, TcpScaleReaderFactory>();
        builder.Services.AddSingleton<IDeviceReaderFactory, RawTcpReaderFactory>();
    }
#if WINDOWS
    if (!dryRun)
    {
        builder.Services.AddSingleton<IDeviceReaderFactory, SerialScaleReaderFactory>();
        builder.Services.AddSingleton<IDeviceReaderFactory, RawSerialReaderFactory>();
        builder.Services.AddSingleton<IDeviceReaderFactory, HidScaleReaderFactory>();
        builder.Services.AddSingleton<IDeviceReaderFactory, RawHidReaderFactory>();
    }
#endif
    builder.Services.AddSingleton<DeviceReaderRegistry>();
    builder.Services.AddSingleton<DeviceCommandRouter>();

    // A device may publish to SQS even when the print transport is HTTP, so ensure the SQS client
    // is registered whenever any device output (or the command queue) needs it.
    var needsSqs = devices.Any(d => d.Output.Transport == "sqs") || !string.IsNullOrWhiteSpace(options.DeviceCommandQueueUrl);
    if (needsSqs)
        EnsureSqsClient(builder, options);

    builder.Services.AddHttpClient();
    builder.Services.AddHttpClient("device-readings");

    foreach (var device in devices)
    {
        var captured = device;
        builder.Services.AddSingleton<IHostedService>(sp =>
        {
            var registry = sp.GetRequiredService<DeviceReaderRegistry>();
            // Off Windows (or in dry-run) the Windows-only types fall back to the simulator; TCP stays real unless --dry-run.
            var simulate = explicitDryRun || (!OperatingSystem.IsWindows() && !captured.IsTcp);
            var effective = simulate ? captured with { Type = "simulated" } : captured;
            var reader = registry.Create(effective, sp);

            // Simulated devices never publish for real (keeps macOS/dry-run runs from polluting live queues).
            IReadingPublisher publisher = simulate
                ? new DryRunReadingPublisher(sp.GetRequiredService<ILogger<DryRunReadingPublisher>>())
                : CreateReadingPublisher(captured.Output, sp);

            var service = new DeviceForwardingService(
                reader,
                publisher,
                captured,
                sp.GetRequiredService<ILogger<DeviceForwardingService>>());
            sp.GetRequiredService<DeviceCommandRouter>().Register(service);
            return service;
        });
    }

    // Cloud → device commands: one SQS queue per station, routed to devices by name.
    if (!string.IsNullOrWhiteSpace(options.DeviceCommandQueueUrl) && !explicitDryRun)
    {
        builder.Services.AddSingleton<IHostedService>(sp => new DeviceCommandPollingService(
            new SqsDeviceCommandSource(
                sp.GetRequiredService<IAmazonSQS>(),
                options.DeviceCommandQueueUrl,
                sp.GetRequiredService<ILogger<SqsDeviceCommandSource>>()),
            sp.GetRequiredService<DeviceCommandRouter>(),
            sp.GetRequiredService<ILogger<DeviceCommandPollingService>>()));
        Log.Information("CloudPrint device command queue: {Queue}", options.DeviceCommandQueueUrl);
    }
}

// Idempotent — RegisterSqsLanes may already have added the client.
static void EnsureSqsClient(HostApplicationBuilder builder, CloudPrintOptions options) =>
    builder.Services.TryAddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(
        options.AwsAccessKeyId,
        options.AwsSecretAccessKey,
        RegionEndpoint.GetBySystemName(options.Region)));

static IReadingPublisher CreateReadingPublisher(ResolvedOutput output, IServiceProvider sp) => output.Transport switch
{
    "http" => new HttpReadingPublisher(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("device-readings"),
        output,
        sp.GetRequiredService<ILogger<HttpReadingPublisher>>()),
    "sqs" => new SqsReadingPublisher(
        sp.GetRequiredService<IAmazonSQS>(),
        output.QueueUrl ?? string.Empty,
        sp.GetRequiredService<ILogger<SqsReadingPublisher>>()),
    _ => throw new InvalidOperationException($"Unknown device output transport: {output.Transport}")
};

// Connects to a device using its config (from stdin) and streams live readings as JSON lines to
// stdout for a bounded window, so the configurator can show "reading now: 12.34 lb". Bounded so it
// always exits cleanly (disposing the port); the configurator may also kill it when its dialog closes.
static async Task<int> PreviewDevice()
{
    var input = await Console.In.ReadToEndAsync();

    DeviceConfig? config;
    try
    {
        config = JsonSerializer.Deserialize<DeviceConfig>(input,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine("Invalid device JSON on stdin: " + ex.Message);
        return 1;
    }

    if (config is null || string.IsNullOrWhiteSpace(config.Name))
    {
        Console.Error.WriteLine("Expected device config JSON on stdin with a \"Name\".");
        return 1;
    }

    var resolved = new CloudPrintOptions { Devices = { config } }.ResolvedDevices().FirstOrDefault();
    if (resolved is null)
    {
        Console.Error.WriteLine("Could not resolve device config.");
        return 1;
    }

    var services = new ServiceCollection();
    services.AddLogging(); // no provider added => silent, keeps stdout clean for readings
    services.AddSingleton<IDeviceReaderFactory, SimulatedDeviceReaderFactory>();
    services.AddSingleton<IDeviceReaderFactory, TcpScaleReaderFactory>();
    services.AddSingleton<IDeviceReaderFactory, RawTcpReaderFactory>();
#if WINDOWS
    services.AddSingleton<IDeviceReaderFactory, SerialScaleReaderFactory>();
    services.AddSingleton<IDeviceReaderFactory, RawSerialReaderFactory>();
    services.AddSingleton<IDeviceReaderFactory, HidScaleReaderFactory>();
    services.AddSingleton<IDeviceReaderFactory, RawHidReaderFactory>();
#endif
    services.AddSingleton<DeviceReaderRegistry>();
    await using var provider = services.BuildServiceProvider();

    IDeviceReader reader;
    try
    {
        reader = provider.GetRequiredService<DeviceReaderRegistry>().Create(resolved, provider);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    await using (reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await reader.ConnectAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        // First line: what we connected to (HID product/usages/descriptor, COM identity, TCP endpoint).
        Console.WriteLine(JsonSerializer.Serialize(new DeviceReading
        {
            DeviceId = reader.DeviceId,
            DeviceType = reader.DeviceType,
            Source = reader.Source,
            Status = "connected",
            Metadata = reader.Metadata is null ? null : new Dictionary<string, string>(reader.Metadata)
        }));
        await Console.Out.FlushAsync();

        while (!cts.IsCancellationRequested)
        {
            DeviceReading? reading;
            try
            {
                reading = await reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                break;
            }

            if (reading is not null)
            {
                reading.DeviceId = reader.DeviceId;
                reading.DeviceType = reader.DeviceType;
                reading.Source = reader.Source;
                Console.WriteLine(JsonSerializer.Serialize(reading));
                await Console.Out.FlushAsync();
            }
        }
    }

    return 0;
}

// Prints a built-in ZPL test label (or supplied content) to a printer, so the configurator's
// "Test" button gives physical confirmation. Windows-only (raw printing via winspool).
static int TestPrint()
{
    var input = Console.In.ReadToEnd();

    TestPrintInput? req;
    try
    {
        req = JsonSerializer.Deserialize<TestPrintInput>(input,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine("Invalid JSON on stdin: " + ex.Message);
        return 1;
    }

    if (req is null || string.IsNullOrWhiteSpace(req.PrinterName))
    {
        Console.Error.WriteLine("Expected JSON on stdin: {\"printerName\":\"...\"}");
        return 1;
    }

#if WINDOWS
    var zpl = string.IsNullOrWhiteSpace(req.Content)
        ? "^XA\n^FO40,40^A0N,50,50^FDCloudPrint^FS\n^FO40,110^A0N,35,35^FDTest Label^FS\n"
          + "^FO40,170^A0N,28,28^FD" + req.PrinterName + "^FS\n^XZ\n"
        : req.Content;

    var tempFile = Path.Combine(Path.GetTempPath(), $"cloudprint-test-{Guid.NewGuid():N}.zpl");
    try
    {
        File.WriteAllBytes(tempFile, System.Text.Encoding.ASCII.GetBytes(zpl));
        new RawPrinter(Microsoft.Extensions.Logging.Abstractions.NullLogger<RawPrinter>.Instance)
            .PrintRaw(tempFile, req.PrinterName, "CloudPrint Test Label");
        Console.WriteLine($"Sent test label to {req.PrinterName}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
    finally
    {
        try { File.Delete(tempFile); } catch { /* best effort */ }
    }
#else
    Console.Error.WriteLine("test-print is only supported on Windows.");
    return 1;
#endif
}

// Publishes a synthetic "Hello" reading to a device's configured output (SQS queue or HTTP webhook),
// so the configurator's device "Test" confirms the outbound pipe end-to-end.
static async Task<int> TestOutput()
{
    var input = await Console.In.ReadToEndAsync();

    TestOutputInput? req;
    try
    {
        req = JsonSerializer.Deserialize<TestOutputInput>(input,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine("Invalid JSON on stdin: " + ex.Message);
        return 1;
    }

    if (req is null || string.IsNullOrWhiteSpace(req.Transport))
    {
        Console.Error.WriteLine("Expected JSON on stdin with a \"transport\".");
        return 1;
    }

    var reading = new DeviceReading
    {
        Station = string.IsNullOrWhiteSpace(req.Station) ? Environment.MachineName : req.Station,
        Host = Environment.MachineName,
        DeviceId = string.IsNullOrWhiteSpace(req.DeviceName) ? "test" : req.DeviceName,
        DeviceType = "test",
        Status = "ok",
        Raw = string.IsNullOrWhiteSpace(req.Message) ? "Hello from CloudPrint" : req.Message,
        Source = new ReadingSource { Connection = "test" },
    };

    try
    {
        IReadingPublisher publisher;
        switch (req.Transport.ToLowerInvariant())
        {
            case "sqs":
                if (string.IsNullOrWhiteSpace(req.QueueUrl) || string.IsNullOrWhiteSpace(req.AccessKey)
                    || string.IsNullOrWhiteSpace(req.SecretKey) || string.IsNullOrWhiteSpace(req.Region))
                {
                    Console.Error.WriteLine("sqs test requires queueUrl, accessKey, secretKey, region.");
                    return 1;
                }

                var sqs = new AmazonSQSClient(req.AccessKey, req.SecretKey,
                    RegionEndpoint.GetBySystemName(req.Region));
                publisher = new SqsReadingPublisher(sqs, req.QueueUrl,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<SqsReadingPublisher>.Instance);
                break;

            case "http":
                if (string.IsNullOrWhiteSpace(req.WebhookUrl))
                {
                    Console.Error.WriteLine("http test requires webhookUrl.");
                    return 1;
                }

                var output = new ResolvedOutput("http", null, req.WebhookUrl, req.HeaderName, req.HeaderValue);
                publisher = new HttpReadingPublisher(new HttpClient(), output,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpReadingPublisher>.Instance);
                break;

            default:
                Console.Error.WriteLine($"Unknown transport: {req.Transport}");
                return 1;
        }

        await publisher.PublishAsync(reading, CancellationToken.None);
        Console.WriteLine($"Sent test message via {req.Transport}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static int ListDevices()
{
    var json = Environment.GetCommandLineArgs().Skip(1).Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
#if WINDOWS
    var inventory = DeviceInventory.Collect();
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    Console.WriteLine("Serial ports:");
    foreach (var port in inventory.SerialPorts)
        Console.WriteLine($"  {port.Name}");

    Console.WriteLine("HID devices:");
    foreach (var hid in inventory.HidDevices)
        Console.WriteLine($"  VID={hid.Vid} PID={hid.Pid} {hid.Product ?? "unknown"}");

    // Human-friendly detail (not parsed by the configurator, which uses --json).
    Console.WriteLine();
    Console.WriteLine("Detail:");
    foreach (var port in inventory.SerialPorts)
        Console.WriteLine($"  {port.Name}: {port.FriendlyName ?? "?"}" +
                          (port.Vid is null ? "" : $"  VID={port.Vid} PID={port.Pid}") +
                          (port.Serial is null ? "" : $"  serial={port.Serial}"));
    foreach (var hid in inventory.HidDevices)
        Console.WriteLine($"  VID={hid.Vid} PID={hid.Pid} {hid.Product ?? "?"} ({hid.Manufacturer ?? "?"})" +
                          (hid.Serial is null ? "" : $" serial={hid.Serial}") +
                          $" usages={hid.Usages ?? "?"}{(hid.IsScale ? " [HID scale]" : "")}");
    return 0;
#else
    if (json) { Console.WriteLine("{\"serialPorts\":[],\"hidDevices\":[]}"); return 0; }
    Console.Error.WriteLine("list-devices is only supported on Windows.");
    return 1;
#endif
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

        var queueTags = tags ?? new Dictionary<string, string>();

        // Tags are applied separately (via TagQueue below) rather than passed into
        // CreateQueue. SQS rejects CreateQueue when a queue of the same name already
        // exists with different tags ("a queue already exists with the same name and
        // different tags"), so provisioning must not depend on tags matching at create
        // time. TagQueue is additive and never fails on an existing queue.

        // Create the DLQ first, reusing it if it already exists.
        var dlqName = $"{queueName}-dlq";
        var dlqUrl = await EnsureQueue(dlqName, attributes: null);

        // Get DLQ ARN
        var dlqAttributes = await sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            AttributeNames = ["QueueArn"]
        });
        var dlqArn = dlqAttributes.Attributes["QueueArn"];

        // Create the main queue with its redrive policy, reusing it if it already exists.
        var queueUrl = await EnsureQueue(queueName, new Dictionary<string, string>
        {
            ["RedrivePolicy"] = JsonSerializer.Serialize(new { deadLetterTargetArn = dlqArn, maxReceiveCount = 5 })
        });

        // Apply tags additively to both queues — safe whether they were just created or
        // already existed (TagQueue overwrites matching keys and adds new ones).
        await TagIfAny(dlqUrl);
        await TagIfAny(queueUrl);

        Console.WriteLine(queueUrl);
        return 0;

        // Creates the named queue with the given attributes, or reuses an existing queue
        // and brings its attributes in line. Tags are handled separately by TagIfAny so a
        // tag mismatch on an existing queue can't fail provisioning.
        async Task<string> EnsureQueue(string name, Dictionary<string, string>? attributes)
        {
            try
            {
                var response = await sqsClient.CreateQueueAsync(new CreateQueueRequest
                {
                    QueueName = name,
                    Attributes = attributes ?? new Dictionary<string, string>()
                });
                return response.QueueUrl;
            }
            catch (QueueNameExistsException)
            {
                // Same name, different attributes — reuse the queue and update its attributes.
                var url = (await sqsClient.GetQueueUrlAsync(name)).QueueUrl;
                if (attributes is { Count: > 0 })
                {
                    await sqsClient.SetQueueAttributesAsync(new SetQueueAttributesRequest
                    {
                        QueueUrl = url,
                        Attributes = attributes
                    });
                }
                return url;
            }
        }

        async Task TagIfAny(string url)
        {
            if (queueTags.Count > 0)
            {
                await sqsClient.TagQueueAsync(new TagQueueRequest
                {
                    QueueUrl = url,
                    Tags = queueTags
                });
            }
        }
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

// --- test-print input ---
class TestPrintInput
{
    public string PrinterName { get; set; } = "";
    public string? Content { get; set; }
}

// --- test-output input ---
class TestOutputInput
{
    public string Transport { get; set; } = "";
    public string? QueueUrl { get; set; }
    public string? WebhookUrl { get; set; }
    public string? HeaderName { get; set; }
    public string? HeaderValue { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string? Region { get; set; }
    public string? Station { get; set; }
    public string? DeviceName { get; set; }
    public string? Message { get; set; }
}

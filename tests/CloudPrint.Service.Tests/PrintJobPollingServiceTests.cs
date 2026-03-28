using CloudPrint.Service.Configuration;
using CloudPrint.Service.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudPrint.Service.Tests;

public class PrintJobPollingServiceTests
{
    private readonly Mock<IJobSource> _jobSource = new();
    private readonly Mock<IJobProcessor> _processor = new();
    private readonly CloudPrintOptions _options = new() { Transport = "sqs", PrinterName = "TestPrinter" };

    [Fact]
    public async Task Processes_job_and_acknowledges_success()
    {
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl"
        };
        var envelope = new JobEnvelope { Id = "job-1", Job = job };

        var callCount = 0;
        _jobSource.Setup(s => s.ReceiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? envelope : null;
            });

        _processor.Setup(p => p.ProcessAsync("job-1", job, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        var service = new PrintJobPollingService(
            _jobSource.Object, _processor.Object, Options.Create(_options),
            NullLogger<PrintJobPollingService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        _jobSource.Verify(s => s.AcknowledgeAsync("job-1", true, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Processes_job_and_acknowledges_failure()
    {
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/bad.zpl",
            ContentType = "application/vnd.zebra.zpl"
        };
        var envelope = new JobEnvelope { Id = "job-2", Job = job };

        var callCount = 0;
        _jobSource.Setup(s => s.ReceiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? envelope : null;
            });

        _processor.Setup(p => p.ProcessAsync("job-2", job, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Validation failed"));

        var service = new PrintJobPollingService(
            _jobSource.Object, _processor.Object, Options.Create(_options),
            NullLogger<PrintJobPollingService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        _jobSource.Verify(s => s.AcknowledgeAsync("job-2", false, "Validation failed", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Continues_polling_when_no_jobs()
    {
        _jobSource.Setup(s => s.ReceiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobEnvelope?)null);

        var service = new PrintJobPollingService(
            _jobSource.Object, _processor.Object, Options.Create(_options),
            NullLogger<PrintJobPollingService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await service.StartAsync(cts.Token);
        await Task.Delay(800);
        await service.StopAsync(CancellationToken.None);

        _jobSource.Verify(s => s.ReceiveAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
        _processor.Verify(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<PrintJobMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Exception_in_processing_does_not_crash_loop()
    {
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl"
        };
        var envelope = new JobEnvelope { Id = "job-3", Job = job };

        var callCount = 0;
        _jobSource.Setup(s => s.ReceiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount <= 2 ? envelope : null;
            });

        _processor.Setup(p => p.ProcessAsync("job-3", job, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Boom"));

        var service = new PrintJobPollingService(
            _jobSource.Object, _processor.Object, Options.Create(_options),
            NullLogger<PrintJobPollingService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await service.StartAsync(cts.Token);
        await Task.Delay(7000);
        await service.StopAsync(CancellationToken.None);

        // Should have polled at least twice despite the exception (5s retry delay between)
        _jobSource.Verify(s => s.ReceiveAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }
}

using CloudPrint.Service.Configuration;
using CloudPrint.Service.FileHandling;
using CloudPrint.Service.Printing;
using CloudPrint.Service.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudPrint.Service.Tests;

public class JobProcessorTests
{
    private readonly Mock<IRawPrinter> _rawPrinter = new();
    private readonly Mock<IDocumentPrinter> _docPrinter = new();
    private readonly Mock<IPdfPrinter> _pdfPrinter = new();
    private readonly CloudPrintOptions _options = new();

    private static ResolvedLane DefaultLane(string printerName = "DefaultPrinter") =>
        new(PrinterName: printerName, QueueUrl: "https://q", PdfRenderDpi: 300, PdfFitMode: "Margins");

    private JobProcessor CreateProcessor(
        string httpContent = "^XA^FO50,50^FDTest^FS^XZ",
        ResolvedLane? lane = null,
        ILogger<JobProcessor>? logger = null)
    {
        var handler = new MockHttpHandler(httpContent);
        var httpClient = new HttpClient(handler);
        var downloader = new FileDownloader(httpClient, NullLogger<FileDownloader>.Instance);
        var router = new PrintRouter(_rawPrinter.Object, _docPrinter.Object, _pdfPrinter.Object, NullLogger<PrintRouter>.Instance);
        return new JobProcessor(
            lane ?? DefaultLane(),
            Options.Create(_options),
            downloader,
            router,
            logger ?? NullLogger<JobProcessor>.Instance);
    }

    [Fact]
    public async Task Successful_zpl_job_returns_success()
    {
        var processor = CreateProcessor("^XA^FO50,50^FDHello^FS^XZ");
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl",
            Copies = 1
        };

        var (success, error) = await processor.ProcessAsync("job-1", job, CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);
        _rawPrinter.Verify(p => p.PrintRaw(It.IsAny<string>(), "DefaultPrinter", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Job_printer_name_override_is_ignored_in_favor_of_lane()
    {
        var processor = CreateProcessor("^XA^FDTest^FS^XZ", lane: DefaultLane("LanePrinter"));
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl",
            PrinterName = "OverridePrinter"  // job-level override — should be ignored
        };

        await processor.ProcessAsync("job-2", job, CancellationToken.None);

        _rawPrinter.Verify(p => p.PrintRaw(It.IsAny<string>(), "LanePrinter", It.IsAny<string>()), Times.Once);
        _rawPrinter.Verify(p => p.PrintRaw(It.IsAny<string>(), "OverridePrinter", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Job_printer_name_matching_lane_does_not_warn()
    {
        var loggerMock = new Mock<ILogger<JobProcessor>>();
        var processor = CreateProcessor("^XA^FDTest^FS^XZ",
            lane: DefaultLane("LanePrinter"),
            logger: loggerMock.Object);
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl",
            PrinterName = "LanePrinter"  // matches the lane
        };

        await processor.ProcessAsync("job-match", job, CancellationToken.None);

        loggerMock.Verify(
            l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task Job_printer_name_mismatching_lane_logs_warning()
    {
        var loggerMock = new Mock<ILogger<JobProcessor>>();
        var processor = CreateProcessor("^XA^FDTest^FS^XZ",
            lane: DefaultLane("LanePrinter"),
            logger: loggerMock.Object);
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl",
            PrinterName = "OtherPrinter"
        };

        await processor.ProcessAsync("job-mismatch", job, CancellationToken.None);

        loggerMock.Verify(
            l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Multiple_copies_prints_multiple_times()
    {
        var processor = CreateProcessor("^XA^FDTest^FS^XZ");
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl",
            Copies = 3
        };

        var (success, _) = await processor.ProcessAsync("job-3", job, CancellationToken.None);

        Assert.True(success);
        _rawPrinter.Verify(p => p.PrintRaw(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Copies_clamped_to_100()
    {
        var processor = CreateProcessor("^XA^FDTest^FS^XZ");
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl",
            Copies = 999
        };

        await processor.ProcessAsync("job-4", job, CancellationToken.None);

        _rawPrinter.Verify(p => p.PrintRaw(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(100));
    }

    [Fact]
    public async Task Invalid_file_content_returns_failure()
    {
        var processor = CreateProcessor("This is not ZPL at all");
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/bad.zpl",
            ContentType = "application/vnd.zebra.zpl"
        };

        var (success, error) = await processor.ProcessAsync("job-5", job, CancellationToken.None);

        Assert.False(success);
        Assert.NotNull(error);
        _rawPrinter.Verify(p => p.PrintRaw(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Download_failure_throws()
    {
        var handler = new MockHttpHandler(statusCode: System.Net.HttpStatusCode.NotFound);
        var httpClient = new HttpClient(handler);
        var downloader = new FileDownloader(httpClient, NullLogger<FileDownloader>.Instance);
        var router = new PrintRouter(_rawPrinter.Object, _docPrinter.Object, _pdfPrinter.Object, NullLogger<PrintRouter>.Instance);
        var processor = new JobProcessor(
            DefaultLane(), Options.Create(_options), downloader, router, NullLogger<JobProcessor>.Instance);

        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/missing.zpl",
            ContentType = "application/vnd.zebra.zpl"
        };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            processor.ProcessAsync("job-6", job, CancellationToken.None));
    }

    [Fact]
    public async Task Inline_zpl_content_prints_successfully()
    {
        var processor = CreateProcessor();
        var job = new PrintJobMessage
        {
            ContentType = "application/vnd.zebra.zpl",
            Content = "^XA^FO50,50^FDInline Label^FS^XZ"
        };

        var (success, error) = await processor.ProcessAsync("job-inline-1", job, CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);
        _rawPrinter.Verify(p => p.PrintRaw(It.IsAny<string>(), "DefaultPrinter", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Inline_text_content_prints_successfully()
    {
        var processor = CreateProcessor();
        var job = new PrintJobMessage
        {
            ContentType = "text/plain",
            Content = "Hello, printer!"
        };

        var (success, error) = await processor.ProcessAsync("job-inline-2", job, CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);
        _rawPrinter.Verify(p => p.PrintRaw(It.IsAny<string>(), "DefaultPrinter", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Inline_binary_content_base64_decoded()
    {
        var processor = CreateProcessor();
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00 };
        var job = new PrintJobMessage
        {
            ContentType = "image/png",
            Content = Convert.ToBase64String(pngBytes)
        };

        var (success, error) = await processor.ProcessAsync("job-inline-3", job, CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);
        _docPrinter.Verify(p => p.Print(It.IsAny<string>(), "DefaultPrinter", "image/png"), Times.Once);
    }

    [Fact]
    public async Task Inline_invalid_base64_returns_failure()
    {
        var processor = CreateProcessor();
        var job = new PrintJobMessage
        {
            ContentType = "image/png",
            Content = "not-valid-base64!!!"
        };

        var (success, error) = await processor.ProcessAsync("job-inline-4", job, CancellationToken.None);

        Assert.False(success);
        Assert.Contains("Invalid base64", error);
    }

    [Fact]
    public async Task Missing_both_content_and_fileUrl_returns_failure()
    {
        var processor = CreateProcessor();
        var job = new PrintJobMessage
        {
            ContentType = "application/vnd.zebra.zpl"
        };

        var (success, error) = await processor.ProcessAsync("job-inline-5", job, CancellationToken.None);

        Assert.False(success);
        Assert.Contains("content", error);
        Assert.Contains("fileUrl", error);
    }

    [Fact]
    public async Task Inline_content_temp_file_cleaned_up()
    {
        var processor = CreateProcessor();
        var job = new PrintJobMessage
        {
            ContentType = "application/vnd.zebra.zpl",
            Content = "^XA^FDTest^FS^XZ"
        };

        await processor.ProcessAsync("job-inline-6", job, CancellationToken.None);

        var tempFiles = Directory.GetFiles(Path.GetTempPath(), "cloudprint-*");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task Inline_content_preferred_over_fileUrl_when_both_present()
    {
        var processor = CreateProcessor();
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl",
            Content = "^XA^FO10,10^FDInline wins^FS^XZ"
        };

        var (success, error) = await processor.ProcessAsync("job-inline-7", job, CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);
    }

    [Fact]
    public async Task Temp_file_cleaned_up_after_success()
    {
        var processor = CreateProcessor("^XA^FDTest^FS^XZ");
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.zpl",
            ContentType = "application/vnd.zebra.zpl"
        };

        await processor.ProcessAsync("job-7", job, CancellationToken.None);

        var tempFiles = Directory.GetFiles(Path.GetTempPath(), "cloudprint-*");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task Successful_pdf_fileUrl_job_returns_success()
    {
        var pdfContent = "%PDF-1.4\n%EOF";
        var processor = CreateProcessor(pdfContent);
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/document.pdf",
            ContentType = "application/pdf",
            Copies = 1
        };

        var (success, error) = await processor.ProcessAsync("pdf-job-1", job, CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);
        _pdfPrinter.Verify(p => p.Print(It.IsAny<string>(), "DefaultPrinter", It.IsAny<PdfRenderSettings>()), Times.Once);
    }

    [Fact]
    public async Task Inline_pdf_base64_content_prints_successfully()
    {
        var processor = CreateProcessor();
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 };
        var job = new PrintJobMessage
        {
            ContentType = "application/pdf",
            Content = Convert.ToBase64String(pdfBytes)
        };

        var (success, error) = await processor.ProcessAsync("pdf-job-inline-1", job, CancellationToken.None);

        Assert.True(success);
        Assert.Null(error);
        _pdfPrinter.Verify(p => p.Print(It.IsAny<string>(), "DefaultPrinter", It.IsAny<PdfRenderSettings>()), Times.Once);
    }

    [Fact]
    public async Task Invalid_pdf_bytes_returns_validation_failure()
    {
        var processor = CreateProcessor();
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var job = new PrintJobMessage
        {
            ContentType = "application/pdf",
            Content = Convert.ToBase64String(pngBytes)
        };

        var (success, error) = await processor.ProcessAsync("pdf-job-invalid-1", job, CancellationToken.None);

        Assert.False(success);
        Assert.NotNull(error);
        _pdfPrinter.Verify(p => p.Print(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PdfRenderSettings>()), Times.Never);
    }

    [Fact]
    public async Task Pdf_print_uses_per_lane_render_settings()
    {
        var pdfContent = "%PDF-1.4\n%EOF";
        var thermalLane = new ResolvedLane("ZebraThermal", "https://q", PdfRenderDpi: 203, PdfFitMode: "PhysicalPage");
        var processor = CreateProcessor(pdfContent, lane: thermalLane);
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/label.pdf",
            ContentType = "application/pdf"
        };

        await processor.ProcessAsync("pdf-lane-1", job, CancellationToken.None);

        _pdfPrinter.Verify(p => p.Print(
            It.IsAny<string>(),
            "ZebraThermal",
            It.Is<PdfRenderSettings>(s => s.Dpi == 203 && s.FitMode == "PhysicalPage")), Times.Once);
    }

    [Fact]
    public async Task Pdf_print_uses_default_settings_for_office_lane()
    {
        var pdfContent = "%PDF-1.4\n%EOF";
        var officeLane = new ResolvedLane("HP_LaserJet", "https://q", PdfRenderDpi: 300, PdfFitMode: "Margins");
        var processor = CreateProcessor(pdfContent, lane: officeLane);
        var job = new PrintJobMessage
        {
            FileUrl = "https://example.com/doc.pdf",
            ContentType = "application/pdf"
        };

        await processor.ProcessAsync("pdf-lane-2", job, CancellationToken.None);

        _pdfPrinter.Verify(p => p.Print(
            It.IsAny<string>(),
            "HP_LaserJet",
            It.Is<PdfRenderSettings>(s => s.Dpi == 300 && s.FitMode == "Margins")), Times.Once);
    }

    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly string? _content;
        private readonly System.Net.HttpStatusCode _statusCode;

        public MockHttpHandler(string? content = null, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _content = content;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode);
            if (_content != null)
                response.Content = new StringContent(_content);
            return Task.FromResult(response);
        }
    }
}

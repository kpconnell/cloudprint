using CloudPrint.Service.Printing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudPrint.Service.Tests;

public class PrintRouterTests
{
    private readonly Mock<IRawPrinter> _rawPrinter = new();
    private readonly Mock<IDocumentPrinter> _docPrinter = new();
    private readonly PrintRouter _router;

    public PrintRouterTests()
    {
        _router = new PrintRouter(
            _rawPrinter.Object,
            _docPrinter.Object,
            NullLogger<PrintRouter>.Instance);
    }

    [Theory]
    [InlineData("application/vnd.zebra.zpl")]
    [InlineData("text/plain")]
    public void Routes_raw_content_types_to_raw_printer(string contentType)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            _router.Print(tempFile, "TestPrinter", contentType);
            _rawPrinter.Verify(p => p.PrintRaw(tempFile, "TestPrinter", It.IsAny<string>()), Times.Once);
            _docPrinter.Verify(p => p.Print(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/bmp")]
    [InlineData("image/gif")]
    [InlineData("image/tiff")]
    public void Routes_document_content_types_to_document_printer(string contentType)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            _router.Print(tempFile, "TestPrinter", contentType);
            _docPrinter.Verify(p => p.Print(tempFile, "TestPrinter", contentType), Times.Once);
            _rawPrinter.Verify(p => p.PrintRaw(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Throws_for_unsupported_content_type()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            Assert.Throws<NotSupportedException>(() =>
                _router.Print(tempFile, "TestPrinter", "application/octet-stream"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData("APPLICATION/VND.ZEBRA.ZPL")]
    [InlineData("Image/PNG")]
    [InlineData("Image/JPEG")]
    public void Content_type_matching_is_case_insensitive(string contentType)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            _router.Print(tempFile, "TestPrinter", contentType);
            // Should not throw — just verify it was routed somewhere
            var totalCalls = _rawPrinter.Invocations.Count + _docPrinter.Invocations.Count;
            Assert.Equal(1, totalCalls);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}

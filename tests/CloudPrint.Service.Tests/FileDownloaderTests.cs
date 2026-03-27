using CloudPrint.Service.FileHandling;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudPrint.Service.Tests;

public class FileDownloaderTests
{
    [Fact]
    public async Task Downloads_file_from_url()
    {
        var handler = new MockHttpHandler("hello world");
        var httpClient = new HttpClient(handler);
        var downloader = new FileDownloader(httpClient, NullLogger<FileDownloader>.Instance);

        var path = await downloader.DownloadAsync("https://example.com/test.txt", CancellationToken.None);
        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal("hello world", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Throws_on_http_error()
    {
        var handler = new MockHttpHandler(statusCode: System.Net.HttpStatusCode.NotFound);
        var httpClient = new HttpClient(handler);
        var downloader = new FileDownloader(httpClient, NullLogger<FileDownloader>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            downloader.DownloadAsync("https://example.com/missing.txt", CancellationToken.None));
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

using System.Net;
using CloudPrint.Service.FileHandling;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudPrint.Service.Tests;

public class FileDownloaderTests
{
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]  // EC2 IMDS
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]       // carrier-grade NAT
    [InlineData("224.0.0.1")]        // multicast
    [InlineData("255.255.255.255")]  // broadcast
    public void Blocks_private_and_reserved_ipv4(string ip)
    {
        Assert.True(FileDownloader.IsPrivateOrReserved(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("54.239.28.85")]
    public void Allows_public_ipv4(string ip)
    {
        Assert.False(FileDownloader.IsPrivateOrReserved(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("::1")]                           // loopback
    [InlineData("fe80::1")]                       // link-local
    [InlineData("fc00::1")]                       // unique local
    [InlineData("fd12:3456:789a::1")]             // unique local
    [InlineData("2001:db8::1")]                   // documentation
    public void Blocks_private_and_reserved_ipv6(string ip)
    {
        Assert.True(FileDownloader.IsPrivateOrReserved(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Blocks_ipv4_mapped_ipv6()
    {
        // ::ffff:169.254.169.254
        Assert.True(FileDownloader.IsPrivateOrReserved(IPAddress.Parse("::ffff:169.254.169.254")));
        Assert.True(FileDownloader.IsPrivateOrReserved(IPAddress.Parse("::ffff:10.0.0.1")));
    }

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

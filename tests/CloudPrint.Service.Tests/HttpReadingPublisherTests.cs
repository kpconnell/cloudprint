using System.Net;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices;
using CloudPrint.Service.Publishing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudPrint.Service.Tests;

public class HttpReadingPublisherTests
{
    private static ResolvedOutput HttpOutput(string url, string? headerName = null, string? headerValue = null) =>
        new("http", QueueUrl: null, WebhookUrl: url, HeaderName: headerName, HeaderValue: headerValue);

    [Fact]
    public async Task Posts_reading_json_to_webhook()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var publisher = new HttpReadingPublisher(new HttpClient(handler),
            HttpOutput("https://example.com/readings"), NullLogger<HttpReadingPublisher>.Instance);

        await publisher.PublishAsync(
            new DeviceReading { DeviceId = "d", Value = 1m, Unit = "kg" }, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("\"unit\":\"kg\"", handler.LastBody);
    }

    [Fact]
    public async Task Adds_api_key_header_when_configured()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var publisher = new HttpReadingPublisher(new HttpClient(handler),
            HttpOutput("https://example.com/readings", "X-Api-Key", "secret"),
            NullLogger<HttpReadingPublisher>.Instance);

        await publisher.PublishAsync(new DeviceReading(), CancellationToken.None);

        Assert.True(handler.LastRequest!.Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task Throws_on_non_success_status()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var publisher = new HttpReadingPublisher(new HttpClient(handler),
            HttpOutput("https://example.com/readings"), NullLogger<HttpReadingPublisher>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            publisher.PublishAsync(new DeviceReading(), CancellationToken.None));
    }

    [Theory]
    [InlineData("http://example.com/r")]  // not https
    [InlineData("https://localhost/r")]   // loopback host
    [InlineData("https://127.0.0.1/r")]   // loopback ip
    [InlineData("https://10.0.0.5/r")]    // private ip
    public async Task Rejects_unsafe_webhook_urls(string url)
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var publisher = new HttpReadingPublisher(new HttpClient(handler),
            HttpOutput(url), NullLogger<HttpReadingPublisher>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            publisher.PublishAsync(new DeviceReading(), CancellationToken.None));

        Assert.Null(handler.LastRequest); // blocked before any request was sent
    }

    private class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        public HttpRequestMessage? LastRequest;
        public string? LastBody;

        public CapturingHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode);
        }
    }
}

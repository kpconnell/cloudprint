using System.Net;
using System.Text.Json;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudPrint.Service.Tests;

public class HttpApiJobSourceTests
{
    private readonly CloudPrintOptions _options = new()
    {
        ApiUrl = "https://api.example.com/print-jobs/next",
        AckUrl = "https://api.example.com/print-jobs",
        ApiHeaderName = "X-Api-Key",
        ApiHeaderValue = "test-key-123",
        HttpPollTimeoutSeconds = 5
    };

    private HttpApiJobSource CreateSource(MockApiHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new HttpApiJobSource(httpClient, Options.Create(_options), NullLogger<HttpApiJobSource>.Instance);
    }

    [Fact]
    public async Task Receive_returns_envelope_on_200()
    {
        var job = new { id = "job-123", fileUrl = "https://example.com/label.zpl", contentType = "application/vnd.zebra.zpl", copies = 2 };
        var handler = new MockApiHandler(HttpStatusCode.OK, JsonSerializer.Serialize(job));
        var source = CreateSource(handler);

        var envelope = await source.ReceiveAsync(CancellationToken.None);

        Assert.NotNull(envelope);
        Assert.Equal("job-123", envelope.Id);
        Assert.Equal("https://example.com/label.zpl", envelope.Job.FileUrl);
        Assert.Equal("application/vnd.zebra.zpl", envelope.Job.ContentType);
        Assert.Equal(2, envelope.Job.Copies);
    }

    [Fact]
    public async Task Receive_returns_null_on_204()
    {
        var handler = new MockApiHandler(HttpStatusCode.NoContent);
        var source = CreateSource(handler);

        var envelope = await source.ReceiveAsync(CancellationToken.None);

        Assert.Null(envelope);
    }

    [Fact]
    public async Task Receive_returns_null_on_401()
    {
        var handler = new MockApiHandler(HttpStatusCode.Unauthorized);
        var source = CreateSource(handler);

        // 401 should log and return null (with a delay, but we can't test the delay easily)
        var envelope = await source.ReceiveAsync(CancellationToken.None);

        Assert.Null(envelope);
    }

    [Fact]
    public async Task Receive_sends_auth_header()
    {
        var handler = new MockApiHandler(HttpStatusCode.NoContent);
        var source = CreateSource(handler);

        await source.ReceiveAsync(CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.Contains("X-Api-Key"));
        Assert.Equal("test-key-123", handler.LastRequest.Headers.GetValues("X-Api-Key").First());
    }

    [Fact]
    public async Task Receive_includes_timeout_query_param()
    {
        var handler = new MockApiHandler(HttpStatusCode.NoContent);
        var source = CreateSource(handler);

        await source.ReceiveAsync(CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("timeout=5", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task Acknowledge_success_sends_completed_patch()
    {
        var handler = new MockApiHandler(HttpStatusCode.OK);
        var source = CreateSource(handler);

        await source.AcknowledgeAsync("job-123", true, null, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Patch, handler.LastRequest.Method);
        Assert.Equal("https://api.example.com/print-jobs/job-123", handler.LastRequest.RequestUri!.ToString());

        var body = await handler.LastRequest.Content!.ReadAsStringAsync();
        Assert.Contains("\"status\":\"completed\"", body);
    }

    [Fact]
    public async Task Acknowledge_failure_sends_failed_patch_with_error()
    {
        var handler = new MockApiHandler(HttpStatusCode.OK);
        var source = CreateSource(handler);

        await source.AcknowledgeAsync("job-456", false, "Printer not found", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Patch, handler.LastRequest.Method);

        var body = await handler.LastRequest.Content!.ReadAsStringAsync();
        Assert.Contains("\"status\":\"failed\"", body);
        Assert.Contains("Printer not found", body);
    }

    [Fact]
    public async Task Acknowledge_sends_auth_header()
    {
        var handler = new MockApiHandler(HttpStatusCode.OK);
        var source = CreateSource(handler);

        await source.AcknowledgeAsync("job-789", true, null, CancellationToken.None);

        Assert.True(handler.LastRequest!.Headers.Contains("X-Api-Key"));
        Assert.Equal("test-key-123", handler.LastRequest.Headers.GetValues("X-Api-Key").First());
    }

    [Fact]
    public async Task Receive_generates_id_if_missing()
    {
        var job = new { fileUrl = "https://example.com/label.zpl", contentType = "application/vnd.zebra.zpl" };
        var handler = new MockApiHandler(HttpStatusCode.OK, JsonSerializer.Serialize(job));
        var source = CreateSource(handler);

        var envelope = await source.ReceiveAsync(CancellationToken.None);

        Assert.NotNull(envelope);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Id));
    }

    public class MockApiHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _responseBody;

        public HttpRequestMessage? LastRequest { get; private set; }

        public MockApiHandler(HttpStatusCode statusCode, string? responseBody = null)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Clone the content before it's disposed
            LastRequest = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                LastRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (request.Content != null)
            {
                var contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                LastRequest.Content = new ByteArrayContent(contentBytes);
                if (request.Content.Headers.ContentType != null)
                    LastRequest.Content.Headers.ContentType = request.Content.Headers.ContentType;
            }

            var response = new HttpResponseMessage(_statusCode);
            if (_responseBody != null)
                response.Content = new StringContent(_responseBody);
            return response;
        }
    }
}

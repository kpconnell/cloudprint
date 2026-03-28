using System.Net;
using System.Text;
using System.Text.Json;
using CloudPrint.Service.Configuration;
using Microsoft.Extensions.Options;

namespace CloudPrint.Service.Transport;

public class HttpApiJobSource : IJobSource
{
    private readonly HttpClient _httpClient;
    private readonly CloudPrintOptions _options;
    private readonly ILogger<HttpApiJobSource> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HttpApiJobSource(
        HttpClient httpClient,
        IOptions<CloudPrintOptions> options,
        ILogger<HttpApiJobSource> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        // Set timeout to poll timeout + buffer so the server times out first
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.HttpPollTimeoutSeconds + 10);
    }

    public async Task<JobEnvelope?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var separator = _options.ApiUrl.Contains('?') ? "&" : "?";
        var url = $"{_options.ApiUrl}{separator}timeout={_options.HttpPollTimeoutSeconds}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_options.ApiHeaderName) && !string.IsNullOrWhiteSpace(_options.ApiHeaderValue))
        {
            request.Headers.TryAddWithoutValidation(_options.ApiHeaderName, _options.ApiHeaderValue);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogError("HTTP API returned 401 Unauthorized — check API key configuration");
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Received HTTP API job: {Body}", body);

        var job = JsonSerializer.Deserialize<PrintJobMessage>(body, JsonOptions);
        if (job is null)
        {
            _logger.LogError("Failed to deserialize HTTP API response. Body: {Body}", body);
            return null;
        }

        var jobId = !string.IsNullOrWhiteSpace(job.Id) ? job.Id : Guid.NewGuid().ToString();

        return new JobEnvelope
        {
            Id = jobId,
            Job = job
        };
    }

    public async Task AcknowledgeAsync(string jobId, bool success, string? error, CancellationToken cancellationToken)
    {
        var url = $"{_options.AckUrl.TrimEnd('/')}/{jobId}";

        var payload = success
            ? new { status = "completed" }
            : (object)new { status = "failed", error = error ?? "Unknown error" };

        using var request = new HttpRequestMessage(HttpMethod.Patch, url);
        if (!string.IsNullOrWhiteSpace(_options.ApiHeaderName) && !string.IsNullOrWhiteSpace(_options.ApiHeaderValue))
        {
            request.Headers.TryAddWithoutValidation(_options.ApiHeaderName, _options.ApiHeaderValue);
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to acknowledge job {JobId}: HTTP {StatusCode}", jobId, response.StatusCode);
        }
        else
        {
            _logger.LogInformation("Acknowledged job {JobId} as {Status}", jobId, success ? "completed" : "failed");
        }
    }
}

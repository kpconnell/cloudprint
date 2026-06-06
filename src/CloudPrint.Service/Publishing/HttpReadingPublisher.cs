using System.Text;
using System.Text.Json;
using CloudPrint.Service.Configuration;
using CloudPrint.Service.Devices;
using CloudPrint.Service.FileHandling;

namespace CloudPrint.Service.Publishing;

/// <summary>
/// POSTs readings as JSON to a configured webhook. Reuses the shared <see cref="UrlSafetyValidator"/>
/// (HTTPS only, no private/reserved hosts, DNS-rebinding protection) and the optional API-key header
/// convention. Throws on a non-2xx response so the forwarding loop retries.
/// </summary>
public class HttpReadingPublisher : IReadingPublisher
{
    private readonly HttpClient _httpClient;
    private readonly ResolvedOutput _output;
    private readonly ILogger<HttpReadingPublisher> _logger;

    public HttpReadingPublisher(HttpClient httpClient, ResolvedOutput output, ILogger<HttpReadingPublisher> logger)
    {
        _httpClient = httpClient;
        _output = output;
        _logger = logger;
    }

    public async Task PublishAsync(DeviceReading reading, CancellationToken cancellationToken)
    {
        var url = _output.WebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Device output WebhookUrl is not configured");

        UrlSafetyValidator.ValidateUrl(url);
        await UrlSafetyValidator.ValidateResolvedAddressAsync(url, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(reading), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_output.HeaderName) && !string.IsNullOrWhiteSpace(_output.HeaderValue))
            request.Headers.TryAddWithoutValidation(_output.HeaderName, _output.HeaderValue);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Webhook returned {(int)response.StatusCode} for reading {reading.ReadingId}");

        _logger.LogDebug("[{DeviceId}] posted reading {ReadingId} to {Url}",
            reading.DeviceId, reading.ReadingId, url);
    }
}

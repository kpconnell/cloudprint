namespace CloudPrint.Service.FileHandling;

public class FileDownloader
{
    private const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50 MB
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly ILogger<FileDownloader> _logger;

    public FileDownloader(HttpClient httpClient, ILogger<FileDownloader> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = DownloadTimeout;
        _logger = logger;
    }

    public async Task<string> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        ValidateUrl(url);

        _logger.LogInformation("Downloading file from {Url}", url);

        var tempPath = Path.Combine(Path.GetTempPath(), $"cloudprint-{Guid.NewGuid()}");
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Check Content-Length if available
        if (response.Content.Headers.ContentLength > MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"File size {response.Content.Headers.ContentLength} bytes exceeds maximum of {MaxFileSizeBytes} bytes");

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = File.Create(tempPath);

        var buffer = new byte[81920];
        long totalBytes = 0;
        int bytesRead;

        while ((bytesRead = await responseStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            totalBytes += bytesRead;
            if (totalBytes > MaxFileSizeBytes)
            {
                fileStream.Close();
                File.Delete(tempPath);
                throw new InvalidOperationException(
                    $"Download exceeded maximum file size of {MaxFileSizeBytes} bytes");
            }
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        _logger.LogInformation("Downloaded {Bytes} bytes to {Path}", totalBytes, tempPath);
        return tempPath;
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid URL: {url}");

        if (uri.Scheme != "https")
            throw new ArgumentException($"Only HTTPS URLs are allowed, got: {uri.Scheme}");

        if (uri.IsLoopback || uri.Host == "localhost")
            throw new ArgumentException($"Loopback URLs are not allowed: {url}");
    }
}

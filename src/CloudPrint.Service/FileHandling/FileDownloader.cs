namespace CloudPrint.Service.FileHandling;

public class FileDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FileDownloader> _logger;

    public FileDownloader(HttpClient httpClient, ILogger<FileDownloader> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Downloading file from {Url}", url);

        var tempPath = Path.Combine(Path.GetTempPath(), $"cloudprint-{Guid.NewGuid()}");
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var fileStream = File.Create(tempPath);
        await response.Content.CopyToAsync(fileStream, cancellationToken);

        _logger.LogDebug("Downloaded {Bytes} bytes to {Path}", fileStream.Length, tempPath);
        return tempPath;
    }
}

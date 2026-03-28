using System.Net;
using System.Net.Sockets;

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
        await ValidateResolvedAddress(url, cancellationToken);

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

        // Block IP-literal URLs pointing to private/reserved ranges
        if (IPAddress.TryParse(uri.Host, out var ip) && IsPrivateOrReserved(ip))
            throw new ArgumentException($"URLs pointing to private/reserved IP addresses are not allowed: {uri.Host}");
    }

    /// <summary>
    /// Resolves the hostname and verifies the resulting IP is not in a private or reserved range.
    /// Prevents DNS rebinding attacks where a public domain resolves to an internal IP.
    /// </summary>
    private static async Task ValidateResolvedAddress(string url, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);

        foreach (var address in addresses)
        {
            if (IsPrivateOrReserved(address))
                throw new ArgumentException(
                    $"URL host '{uri.Host}' resolves to private/reserved address {address} — request blocked");
        }
    }

    internal static bool IsPrivateOrReserved(IPAddress address)
    {
        // Normalize IPv6-mapped IPv4 (e.g. ::ffff:10.0.0.1) to IPv4
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,                                                  // 0.0.0.0/8 — current network
                10 => true,                                                 // 10.0.0.0/8 — RFC 1918
                127 => true,                                                // 127.0.0.0/8 — loopback
                169 when bytes[1] == 254 => true,                           // 169.254.0.0/16 — link-local / IMDS
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,          // 172.16.0.0/12 — RFC 1918
                192 when bytes[1] == 168 => true,                           // 192.168.0.0/16 — RFC 1918
                192 when bytes[1] == 0 && bytes[2] == 0 => true,            // 192.0.0.0/24 — IETF protocol
                198 when bytes[1] >= 18 && bytes[1] <= 19 => true,          // 198.18.0.0/15 — benchmarking
                100 when bytes[1] >= 64 && bytes[1] <= 127 => true,         // 100.64.0.0/10 — carrier-grade NAT
                _ when bytes[0] >= 224 => true,                             // 224.0.0.0+ — multicast & reserved
                _ => false
            };
        }

        // IPv6: block loopback (::1), link-local (fe80::/10), ULA (fc00::/7)
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(address))
                return true;

            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xE0) == 0x20 && bytes[0] == 0x20 && bytes[1] == 0x01
                && bytes[2] == 0x0D && bytes[3] == 0xB8)
                return true; // 2001:db8::/32 — documentation

            if ((bytes[0] & 0xFE) == 0xFC)
                return true; // fc00::/7 — unique local

            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                return true; // fe80::/10 — link-local
        }

        return false;
    }
}

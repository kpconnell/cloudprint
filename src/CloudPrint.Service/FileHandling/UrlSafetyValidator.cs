using System.Net;
using System.Net.Sockets;

namespace CloudPrint.Service.FileHandling;

/// <summary>
/// Shared HTTPS/SSRF validation for outbound and inbound URLs: HTTPS only, no loopback, and no
/// private/reserved IPs (including via DNS resolution to block rebinding). Used by both
/// <see cref="FileDownloader"/> (inbound) and the HTTP reading publisher (outbound).
/// </summary>
internal static class UrlSafetyValidator
{
    internal static void ValidateUrl(string url)
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
    internal static async Task ValidateResolvedAddressAsync(string url, CancellationToken cancellationToken)
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

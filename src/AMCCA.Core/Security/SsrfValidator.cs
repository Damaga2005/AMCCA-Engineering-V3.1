using System;
using System.Net;
using System.Net.Sockets;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Security;

public static class SsrfValidator
{
    public static void ValidateDestinationUri(Uri uri)
    {
        if (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new AmccaException(
                AmccaErrors.Sec003,
                ErrorCategory.Security,
                $"SSRF guard rejected target '{uri}': loopback address prohibited (SPEC/28, SPEC/72 S-06).");
        }

        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IsPrivateOrReservedIp(ip))
            {
                throw new AmccaException(
                    AmccaErrors.Sec003,
                    ErrorCategory.Security,
                    $"SSRF guard rejected target '{uri}': IP {ip} is in a private, link-local or reserved range (SPEC/28, SPEC/72 S-06).");
            }
        }
        else
        {
            // Resolve host to ensure it doesn't point to private IP
            try
            {
                var addresses = Dns.GetHostAddresses(uri.DnsSafeHost);
                foreach (var addr in addresses)
                {
                    if (IsPrivateOrReservedIp(addr))
                    {
                        throw new AmccaException(
                            AmccaErrors.Sec003,
                            ErrorCategory.Security,
                            $"SSRF guard rejected target '{uri}': hostname resolves to private IP {addr} (SPEC/28, SPEC/72 S-06).");
                    }
                }
            }
            catch (SocketException)
            {
                // Unresolvable host will fail naturally at network connect time
            }
        }
    }

    public static bool IsPrivateOrReservedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10) return true;

            // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // 169.254.0.0/16 (Link-local & AWS/GCP/Azure metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // 127.0.0.0/8
            if (bytes[0] == 127) return true;

            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;

            var bytes = ip.GetAddressBytes();
            // fc00::/7 (Unique local addresses)
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        return false;
    }
}

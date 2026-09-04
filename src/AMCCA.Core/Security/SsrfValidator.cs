using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;

namespace AMCCA.Core.Security;

public static class SsrfValidator
{
    public static void ValidateUrl(Uri uri) => ValidateDestinationUri(uri);

    public static void ValidateDestinationUri(Uri uri)
    {
        if (uri == null)
        {
            throw new AmccaException(
                AmccaErrors.Sec003,
                ErrorCategory.Security,
                "Destination URI cannot be null.");
        }

        // Validate scheme (D-024 / SPEC/28): Only http and https permitted
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new AmccaException(
                AmccaErrors.Sec003,
                ErrorCategory.Security,
                $"SSRF guard rejected scheme '{uri.Scheme}': only HTTP and HTTPS are permitted (SPEC/28, S-06).");
        }

        // Reject known cloud metadata hostnames
        if (string.Equals(uri.Host, "metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "instance-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new AmccaException(
                AmccaErrors.Sec003,
                ErrorCategory.Security,
                $"SSRF guard rejected cloud metadata hostname '{uri.Host}' (SPEC/28, S-06).");
        }

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
            // Resolve host to ensure it doesn't resolve to private IP
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

        // Unpack IPv4-mapped IPv6 (::ffff:192.168.1.1 -> 192.168.1.1)
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

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

            // 127.0.0.0/8 (Loopback)
            if (bytes[0] == 127) return true;

            // 0.0.0.0/8 ("This network")
            if (bytes[0] == 0) return true;

            // 100.64.0.0/10 (Carrier-grade NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;

            // 198.18.0.0/15 (Benchmark network testing)
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;

            // 224.0.0.0/4 (Multicast) & 240.0.0.0/4 (Reserved)
            if (bytes[0] >= 224) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;

            var bytes = ip.GetAddressBytes();

            // fc00::/7 (Unique local addresses / ULA)
            if ((bytes[0] & 0xFE) == 0xFC) return true;

            // ::ffff:0:0/96 (IPv4-mapped)
            if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 &&
                bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 &&
                bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0xFF && bytes[11] == 0xFF)
            {
                var ipv4 = new IPAddress(new ReadOnlySpan<byte>(bytes, 12, 4));
                return IsPrivateOrReservedIp(ipv4);
            }
        }

        return false;
    }

    /// <summary>
    /// Creates a SocketsHttpHandler that enforces coupled DNS resolution and socket binding (DEF-014),
    /// eliminating DNS rebinding TOCTOU vulnerabilities.
    /// </summary>
    public static SocketsHttpHandler CreateSafeSocketsHttpHandler()
    {
        return new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                // Reject direct cloud metadata hostnames
                if (string.Equals(host, "metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(host, "instance-data", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AmccaException(
                        AmccaErrors.Sec003,
                        ErrorCategory.Security,
                        $"SSRF ConnectCallback rejected host '{host}' (SPEC/28, S-06).");
                }

                IPAddress? targetIp = null;

                if (IPAddress.TryParse(host, out var parsedIp))
                {
                    if (IsPrivateOrReservedIp(parsedIp))
                    {
                        throw new AmccaException(
                            AmccaErrors.Sec003,
                            ErrorCategory.Security,
                            $"SSRF ConnectCallback rejected target IP '{parsedIp}': private or reserved (DEF-014).");
                    }
                    targetIp = parsedIp;
                }
                else
                {
                    var entry = await Dns.GetHostEntryAsync(host, cancellationToken);
                    foreach (var addr in entry.AddressList)
                    {
                        if (!IsPrivateOrReservedIp(addr))
                        {
                            targetIp = addr;
                            break;
                        }
                    }

                    if (targetIp == null)
                    {
                        throw new AmccaException(
                            AmccaErrors.Sec003,
                            ErrorCategory.Security,
                            $"SSRF ConnectCallback rejected connection to '{host}': all resolved IP addresses are private or reserved (DEF-014, S-06, S-08).");
                    }
                }

                // Connect socket directly to the exact validated IP address
                var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(targetIp, port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }
}

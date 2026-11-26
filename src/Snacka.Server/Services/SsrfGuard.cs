using System.Net;
using System.Net.Sockets;

namespace Snacka.Server.Services;

/// <summary>
/// Outgoing requests on behalf of users (link previews) may only reach the public internet (#18).
/// The check runs on the address actually dialled, inside the connection callback, so neither DNS
/// rebinding, redirects nor alternative spellings of an address (IPv4-mapped IPv6, NAT64, 6to4)
/// get around it.
/// </summary>
public static class SsrfGuard
{
    /// <summary>A handler that refuses to connect anywhere <paramref name="isAllowed"/> rejects, follows no redirects and uses no proxy.</summary>
    public static SocketsHttpHandler CreateHandler(Func<IPAddress, bool>? isAllowed = null)
    {
        isAllowed ??= IsPublic;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // SendAsync follows them, through this same check
            UseProxy = false,          // a proxy would be the address dialled instead of the target
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, cancellationToken);
                var allowed = addresses.Where(isAllowed).ToList();
                if (allowed.Count == 0)
                    throw new HttpRequestException($"Refusing to connect to {host}: not a public address.");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed.ToArray(), context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    /// <summary>
    /// Sends <paramref name="uri"/>, following up to <paramref name="maxRedirects"/> http(s) redirects
    /// itself; every hop goes through the client's handler and so through the address check.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri uri, Action<HttpRequestMessage>? configure, CancellationToken cancellationToken, int maxRedirects = 3)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            configure?.Invoke(request);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is < 300 or >= 400 || response.Headers.Location is not { } location)
                return response;
            response.Dispose();
            if (hop == maxRedirects)
                throw new HttpRequestException("Too many redirects.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException($"Refusing to follow a redirect to {uri.Scheme}.");
        }
    }

    /// <summary>Whether <paramref name="ip"/> is a globally routable unicast address.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0                                   // "this network"
                || b[0] == 10                                    // private
                || b[0] == 127                                   // loopback
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                 // link-local, cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)      // IETF protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)      // documentation
                || (b[0] == 192 && b[1] == 168)                 // private
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))  // benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)   // documentation
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)    // documentation
                || b[0] >= 224);                                // multicast, reserved, broadcast
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        // Addresses that carry an IPv4 address: judge that one.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)   // NAT64 64:ff9b::/96
            return IsPublic(new IPAddress(b[12..16]));
        if (b[0] == 0x20 && b[1] == 0x02)                                   // 6to4 2002::/16
            return IsPublic(new IPAddress(b[2..6]));

        return !(ip.Equals(IPAddress.IPv6Loopback)
            || ip.Equals(IPAddress.IPv6Any)
            || ip.IsIPv6LinkLocal
            || ip.IsIPv6SiteLocal
            || ip.IsIPv6Multicast
            || ip.IsIPv4MappedToIPv6
            || (b[0] & 0xfe) == 0xfc                                         // unique local fc00::/7
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) // documentation 2001:db8::/32
            || b.Take(12).All(x => x == 0));                                  // IPv4-compatible ::a.b.c.d (deprecated)
    }
}

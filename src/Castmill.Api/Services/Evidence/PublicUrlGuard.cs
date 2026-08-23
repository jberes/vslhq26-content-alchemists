using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Castmill.Api.Services.Evidence;

public sealed class PublicUrlException(string message) : Exception(message);

public static class PublicUrlGuard
{
    public static async Task<Uri> ValidateAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new PublicUrlException("That is not a valid URL.");
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new PublicUrlException("Only http and https URLs can be imported.");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new PublicUrlException("URLs containing credentials cannot be imported.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        }
        catch (SocketException)
        {
            throw new PublicUrlException($"Couldn't resolve {uri.DnsSafeHost}.");
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivate))
        {
            throw new PublicUrlException(
                "That host resolves to a private address and cannot be fetched.");
        }
        return uri;
    }

    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolvePublicAsync(context.DnsEndPoint.Host, ct);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
                if (ex is OperationCanceledException)
                {
                    throw;
                }
            }
        }
        throw new HttpRequestException("No validated public address accepted the connection.", lastError);
    }

    private static async Task<IPAddress[]> ResolvePublicAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            throw new PublicUrlException($"Couldn't resolve {host}.");
        }
        if (addresses.Length == 0 || addresses.Any(IsPrivate))
        {
            throw new PublicUrlException(
                "That host resolves to a private or reserved address and cannot be fetched.");
        }
        return addresses;
    }

    internal static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)
            || ip.IsIPv6LinkLocal
            || ip.IsIPv6SiteLocal
            || ip.IsIPv6UniqueLocal)
        {
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                return IsPrivate(ip.MapToIPv4());
            }
            var v6Bytes = ip.GetAddressBytes();
            var globalUnicast = (v6Bytes[0] & 0xe0) == 0x20;
            var documentation = v6Bytes[0] == 0x20 && v6Bytes[1] == 0x01
                && v6Bytes[2] == 0x0d && v6Bytes[3] == 0xb8;
            var teredo = v6Bytes[0] == 0x20 && v6Bytes[1] == 0x01
                && v6Bytes[2] == 0x00 && v6Bytes[3] == 0x00;
            var sixToFour = v6Bytes[0] == 0x20 && v6Bytes[1] == 0x02;
            return ip.Equals(IPAddress.IPv6Any)
                || ip.IsIPv6Multicast
                || !globalUnicast
                || documentation
                || teredo
                || sixToFour;
        }

        var bytes = ip.GetAddressBytes();
        return bytes[0] switch
        {
            10 or 127 or 0 => true,
            172 => bytes[1] is >= 16 and <= 31,
            100 => bytes[1] is >= 64 and <= 127,
            192 => bytes[1] == 168
                || (bytes[1] == 0 && bytes[2] is 0 or 2)
                || (bytes[1] == 88 && bytes[2] == 99),
            169 => bytes[1] == 254,
            198 => bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100),
            203 => bytes[1] == 0 && bytes[2] == 113,
            _ => bytes[0] >= 224,
        };
    }
}
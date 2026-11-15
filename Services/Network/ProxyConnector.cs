using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.Http;

namespace GHPC_Mod_Manager.Services.Network;

internal static class ProxyConnector
{
    public static bool TryParseEndpoint(string? address, out DnsEndPoint endpoint, out string error)
    {
        endpoint = new DnsEndPoint("localhost", 1);
        error = string.Empty;
        var value = address?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            error = "Proxy address is empty.";
            return false;
        }

        string host;
        string portText;
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket <= 1 || closingBracket + 1 >= value.Length || value[closingBracket + 1] != ':')
            {
                error = "IPv6 proxy addresses must use [address]:port format.";
                return false;
            }

            host = value[1..closingBracket];
            portText = value[(closingBracket + 2)..];
            if (!IPAddress.TryParse(host, out var ipv6Address) || ipv6Address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "The proxy address contains an invalid IPv6 host.";
                return false;
            }
        }
        else
        {
            var separator = value.LastIndexOf(':');
            if (separator <= 0 || separator == value.Length - 1 || value.AsSpan(0, separator).Contains(':'))
            {
                error = "Proxy address must use host:port format.";
                return false;
            }

            host = value[..separator].Trim();
            portText = value[(separator + 1)..].Trim();
            var hostType = Uri.CheckHostName(host);
            if (hostType == UriHostNameType.Unknown || host.Any(char.IsWhiteSpace))
            {
                error = "The proxy address contains an invalid host.";
                return false;
            }
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            error = "Proxy port must be between 1 and 65535.";
            return false;
        }

        endpoint = new DnsEndPoint(host, port);
        return true;
    }

    public static async ValueTask<Stream> ConnectSocks5Async(
        SocketsHttpConnectionContext context,
        string proxyAddress,
        CancellationToken cancellationToken)
    {
        if (!TryParseEndpoint(proxyAddress, out var proxyEndpoint, out var error))
            throw new InvalidOperationException(error);

        var socket = new Socket(proxyEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(proxyEndpoint, cancellationToken);
            var stream = new NetworkStream(socket, ownsSocket: true);

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, cancellationToken);
            if (greeting[0] != 0x05 || greeting[1] != 0x00)
                throw new IOException("The SOCKS5 proxy rejected the no-authentication method.");

            var destinationHost = context.DnsEndPoint.Host;
            using var request = new MemoryStream();
            request.WriteByte(0x05); // SOCKS5
            request.WriteByte(0x01); // CONNECT
            request.WriteByte(0x00); // Reserved

            if (IPAddress.TryParse(destinationHost, out var destinationIp))
            {
                if (destinationIp.AddressFamily == AddressFamily.InterNetwork)
                {
                    request.WriteByte(0x01);
                    request.Write(destinationIp.GetAddressBytes());
                }
                else
                {
                    request.WriteByte(0x04);
                    request.Write(destinationIp.GetAddressBytes());
                }
            }
            else
            {
                var asciiHost = Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(destinationHost));
                if (asciiHost.Length is 0 or > 255)
                    throw new IOException("The destination host name is too long for SOCKS5.");

                request.WriteByte(0x03);
                request.WriteByte((byte)asciiHost.Length);
                request.Write(asciiHost);
            }

            request.WriteByte((byte)(context.DnsEndPoint.Port >> 8));
            request.WriteByte((byte)(context.DnsEndPoint.Port & 0xFF));
            await stream.WriteAsync(request.ToArray(), cancellationToken);

            var responseHeader = new byte[4];
            await stream.ReadExactlyAsync(responseHeader, cancellationToken);
            if (responseHeader[0] != 0x05 || responseHeader[1] != 0x00)
                throw new IOException($"SOCKS5 proxy connection failed with status 0x{responseHeader[1]:X2}.");

            var boundAddressLength = responseHeader[3] switch
            {
                0x01 => 4,
                0x04 => 16,
                0x03 => await ReadDomainLengthAsync(stream, cancellationToken),
                _ => throw new IOException("SOCKS5 proxy returned an invalid address type.")
            };
            var trailingResponse = new byte[boundAddressLength + 2];
            await stream.ReadExactlyAsync(trailingResponse, cancellationToken);
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async ValueTask<int> ReadDomainLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, cancellationToken);
        return length[0];
    }
}

internal sealed class SettingsWebProxy : IWebProxy
{
    private readonly ISettingsService _settingsService;
    private readonly IWebProxy? _systemProxy;

    public SettingsWebProxy(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _systemProxy = HttpClient.DefaultProxy;
    }

    public ICredentials? Credentials { get; set; }

    public Uri GetProxy(Uri destination)
    {
        var settings = _settingsService.Settings;
        if (!settings.UseCustomProxy)
            return _systemProxy?.GetProxy(destination) ?? destination;

        if (string.Equals(settings.ProxyProtocol, "SOCKS5", StringComparison.OrdinalIgnoreCase))
            return destination;

        if (!string.Equals(settings.ProxyProtocol, "HTTP", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configured proxy protocol is not supported.");

        if (!ProxyConnector.TryParseEndpoint(settings.ProxyAddress, out var endpoint, out var error))
            throw new InvalidOperationException(error);

        return new UriBuilder(Uri.UriSchemeHttp, endpoint.Host, endpoint.Port).Uri;
    }

    public bool IsBypassed(Uri host)
    {
        var settings = _settingsService.Settings;
        if (!settings.UseCustomProxy)
            return _systemProxy?.IsBypassed(host) ?? true;

        if (string.Equals(settings.ProxyProtocol, "SOCKS5", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(settings.ProxyProtocol, "HTTP", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The configured proxy protocol is not supported.");

        if (!ProxyConnector.TryParseEndpoint(settings.ProxyAddress, out _, out var error))
            throw new InvalidOperationException(error);

        return false;
    }
}

using System.Net;
using System.Net.Http;

namespace GHPC_Mod_Manager.Services.Network;

internal static class ProxyConnectionTester
{
    public static async Task<long> TestAsync(string protocol, string address, CancellationToken cancellationToken = default)
    {
        if (!ProxyConnector.TryParseEndpoint(address, out var endpoint, out var error))
            throw new ArgumentException(error, nameof(address));

        if (!string.Equals(protocol, "HTTP", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(protocol, "SOCKS5", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The proxy protocol is not supported.", nameof(protocol));
        }

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            UseProxy = string.Equals(protocol, "HTTP", StringComparison.OrdinalIgnoreCase)
        };

        if (handler.UseProxy)
        {
            handler.Proxy = new WebProxy(new UriBuilder(Uri.UriSchemeHttp, endpoint.Host, endpoint.Port).Uri);
        }
        else
        {
            handler.ConnectCallback = (context, token) =>
                ProxyConnector.ConnectSocks5Async(context, address, token);
        }

        using (handler)
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) })
        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://github.com/"))
        {
            request.Headers.UserAgent.ParseAdd("GHPC-Mod-Manager/1.0");
            var started = System.Diagnostics.Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            started.Stop();
            return started.ElapsedMilliseconds;
        }
    }
}

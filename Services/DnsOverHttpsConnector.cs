using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace GHPC_Mod_Manager.Services;

internal static class DnsOverHttpsConnector
{
    private static readonly string[] DohEndpoints =
    {
        "https://223.5.5.5/dns-query",
        "https://1.12.12.12/dns-query",
        "https://223.6.6.6/dns-query",
        "https://120.53.53.53/dns-query",
        "https://doh.apad.pro/dns-query",
        "https://v.recipes/dns-cn/dns.google/dns-query"
    };

    private static readonly HttpClient DohClient = CreateDohClient();
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MinCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromMinutes(3);

    private sealed record CacheEntry(IPAddress[] Addresses, DateTimeOffset ExpiresAt, string Endpoint);
    private sealed record ResolveResult(IPAddress[] Addresses, TimeSpan CacheDuration, string Endpoint);
    private sealed record AddressLookupResult(IPAddress[] Addresses, bool FromCache, string Endpoint);

    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        bool useDnsOverHttps,
        ILoggingService loggingService,
        CancellationToken cancellationToken)
    {
        var dnsEndPoint = context.DnsEndPoint;
        if (!useDnsOverHttps || IPAddress.TryParse(dnsEndPoint.Host, out _))
        {
            return await ConnectWithSystemDnsAsync(dnsEndPoint, cancellationToken);
        }

        var host = dnsEndPoint.Host;
        var lookupResult = await GetAddressesAsync(host, loggingService, cancellationToken);
        var addresses = lookupResult.Addresses;
        if (addresses.Length == 0)
        {
            loggingService.LogWarning("DoH disabled for {0} after all resolvers failed; using system DNS.", host);
            return await ConnectWithSystemDnsAsync(dnsEndPoint, cancellationToken);
        }

        Exception? lastConnectError = null;
        foreach (var address in addresses)
        {
            try
            {
                return await ConnectWithResolvedIpAsync(address, dnsEndPoint.Port, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastConnectError = ex;
            }
        }

        if (lastConnectError != null)
        {
            loggingService.LogWarning(
                "DoH resolved {0}, but all addresses failed ({1}); using system DNS.",
                host,
                lastConnectError.Message);
        }

        return await ConnectWithSystemDnsAsync(dnsEndPoint, cancellationToken);
    }

    public static void ClearCache() => Cache.Clear();

    private static async Task<AddressLookupResult> GetAddressesAsync(
        string host,
        ILoggingService loggingService,
        CancellationToken cancellationToken)
    {
        if (TryGetCache(host, out var cached) && cached != null)
            return new AddressLookupResult(cached.Addresses, FromCache: true, cached.Endpoint);

        var result = await ResolveByDohAsync(host, loggingService, cancellationToken);
        if (result.Addresses.Length == 0)
            return new AddressLookupResult(Array.Empty<IPAddress>(), FromCache: false, string.Empty);

        var expiresAt = DateTimeOffset.UtcNow.Add(result.CacheDuration);
        Cache[host] = new CacheEntry(result.Addresses, expiresAt, result.Endpoint);
        return new AddressLookupResult(result.Addresses, FromCache: false, result.Endpoint);
    }

    private static bool TryGetCache(string host, out CacheEntry? entry)
    {
        if (Cache.TryGetValue(host, out var cached) &&
            cached != null &&
            cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            entry = cached;
            return true;
        }

        entry = null;
        return false;
    }

    private static async Task<ResolveResult> ResolveByDohAsync(
        string host,
        ILoggingService loggingService,
        CancellationToken cancellationToken)
    {
        foreach (var endpoint in DohEndpoints)
        {
            var aResult = await ResolveSingleTypeAsync(endpoint, host, queryType: 1, cancellationToken);
            var aaaaResult = await ResolveSingleTypeAsync(endpoint, host, queryType: 28, cancellationToken);

            var combined = aResult.Addresses
                .Concat(aaaaResult.Addresses)
                .GroupBy(ip => ip.ToString())
                .Select(group => group.First())
                .ToArray();

            if (combined.Length == 0)
                continue;

            var cacheDuration = GetMinimumDuration(aResult.CacheDuration, aaaaResult.CacheDuration);
            return new ResolveResult(combined, cacheDuration, endpoint);
        }

        return new ResolveResult(Array.Empty<IPAddress>(), TimeSpan.Zero, string.Empty);
    }

    private static TimeSpan GetMinimumDuration(TimeSpan first, TimeSpan second)
    {
        if (first == TimeSpan.Zero && second == TimeSpan.Zero)
            return DefaultCacheDuration;
        if (first == TimeSpan.Zero)
            return second;
        if (second == TimeSpan.Zero)
            return first;
        return first <= second ? first : second;
    }

    private static async Task<ResolveResult> ResolveSingleTypeAsync(
        string endpoint,
        string host,
        ushort queryType,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = BuildDnsQuery(host, queryType);
            var queryParam = ToBase64Url(query);
            var requestUrl = $"{endpoint}?dns={queryParam}";

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/dns-message");

            using var response = await DohClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return new ResolveResult(Array.Empty<IPAddress>(), TimeSpan.Zero, string.Empty);

            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return ParseDnsResponse(payload, queryType);
        }
        catch
        {
            return new ResolveResult(Array.Empty<IPAddress>(), TimeSpan.Zero, string.Empty);
        }
    }

    private static byte[] BuildDnsQuery(string host, ushort queryType)
    {
        var idn = new IdnMapping();
        var asciiHost = idn.GetAscii(host.TrimEnd('.'));
        var labels = asciiHost.Split('.', StringSplitOptions.RemoveEmptyEntries);

        var buffer = new List<byte>(256);
        var id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);

        // Header
        buffer.Add((byte)(id >> 8));
        buffer.Add((byte)(id & 0xFF));
        buffer.Add(0x01); // recursion desired
        buffer.Add(0x00);
        buffer.Add(0x00);
        buffer.Add(0x01); // question count = 1
        buffer.Add(0x00);
        buffer.Add(0x00); // answer count
        buffer.Add(0x00);
        buffer.Add(0x00); // authority count
        buffer.Add(0x00);
        buffer.Add(0x00); // additional count

        foreach (var label in labels)
        {
            var labelBytes = Encoding.ASCII.GetBytes(label);
            if (labelBytes.Length is < 1 or > 63)
                throw new InvalidOperationException("Invalid DNS label.");

            buffer.Add((byte)labelBytes.Length);
            buffer.AddRange(labelBytes);
        }

        buffer.Add(0x00); // end of qname
        buffer.Add((byte)(queryType >> 8));
        buffer.Add((byte)(queryType & 0xFF));
        buffer.Add(0x00);
        buffer.Add(0x01); // class IN

        return buffer.ToArray();
    }

    private static ResolveResult ParseDnsResponse(byte[] payload, ushort queryType)
    {
        if (payload.Length < 12)
            return new ResolveResult(Array.Empty<IPAddress>(), TimeSpan.Zero, string.Empty);

        var span = payload.AsSpan();
        var flags = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
        var rcode = flags & 0x000F;
        if (rcode != 0)
            return new ResolveResult(Array.Empty<IPAddress>(), TimeSpan.Zero, string.Empty);

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(4, 2));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(6, 2));

        var offset = 12;
        for (var i = 0; i < questionCount; i++)
        {
            offset = SkipDnsName(payload, offset);
            if (offset < 0 || offset + 4 > payload.Length)
                return new ResolveResult(Array.Empty<IPAddress>(), TimeSpan.Zero, string.Empty);

            offset += 4; // qtype + qclass
        }

        var result = new List<IPAddress>();
        uint? minTtl = null;

        for (var i = 0; i < answerCount; i++)
        {
            offset = SkipDnsName(payload, offset);
            if (offset < 0 || offset + 10 > payload.Length)
                break;

            var type = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
            var classCode = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 2, 2));
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 4, 4));
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 8, 2));
            offset += 10;

            if (offset + rdLength > payload.Length)
                break;

            if (classCode == 1 && type == queryType)
            {
                if (queryType == 1 && rdLength == 4)
                {
                    result.Add(new IPAddress(span.Slice(offset, 4)));
                    minTtl = minTtl.HasValue ? Math.Min(minTtl.Value, ttl) : ttl;
                }
                else if (queryType == 28 && rdLength == 16)
                {
                    result.Add(new IPAddress(span.Slice(offset, 16)));
                    minTtl = minTtl.HasValue ? Math.Min(minTtl.Value, ttl) : ttl;
                }
            }

            offset += rdLength;
        }

        if (result.Count == 0)
            return new ResolveResult(Array.Empty<IPAddress>(), TimeSpan.Zero, string.Empty);

        var ttlSeconds = minTtl.GetValueOrDefault((uint)DefaultCacheDuration.TotalSeconds);
        var bounded = TimeSpan.FromSeconds(Math.Clamp((int)ttlSeconds, (int)MinCacheDuration.TotalSeconds, (int)MaxCacheDuration.TotalSeconds));
        return new ResolveResult(result.ToArray(), bounded, string.Empty);
    }

    private static int SkipDnsName(byte[] payload, int offset)
    {
        while (offset < payload.Length)
        {
            var length = payload[offset];
            if (length == 0)
                return offset + 1;

            // compressed name pointer
            if ((length & 0xC0) == 0xC0)
            {
                if (offset + 1 >= payload.Length)
                    return -1;
                return offset + 2;
            }

            offset++;
            if (offset + length > payload.Length)
                return -1;
            offset += length;
        }

        return -1;
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static async ValueTask<Stream> ConnectWithSystemDnsAsync(DnsEndPoint endPoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async ValueTask<Stream> ConnectWithResolvedIpAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static HttpClient CreateDohClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "GHPC-Mod-Manager-DoH/1.0");
        return client;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace MyTranslator.Api.FileTasks;

public sealed class UrlImportClient(HttpClient httpClient)
{
    private const int MaximumRedirects = 5;
    private const int MaximumBytes = 10 * 1024 * 1024;

    public async Task<FetchedHtml> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var current) ||
            current.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(current.UserInfo) ||
            !string.IsNullOrEmpty(current.Fragment))
        {
            throw new InvalidFileTaskRequestException("invalid_source_url", "The source URL is invalid.");
        }

        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            await EnsurePublicAddressAsync(current, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MyTranslator/1.0)");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidFileTaskRequestException("source_fetch_timeout", "The source URL request timed out.", exception);
            }
            catch (HttpRequestException exception) when (HasProhibitedNetworkCause(exception))
            {
                throw new InvalidFileTaskRequestException(
                    "unsafe_source_url",
                    "The source URL connected to a prohibited network address.",
                    exception);
            }
            catch (HttpRequestException exception)
            {
                throw new InvalidFileTaskRequestException("source_fetch_failed", "The source URL could not be fetched.", exception);
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
                {
                    if (redirect == MaximumRedirects)
                    {
                        throw new InvalidFileTaskRequestException("source_fetch_failed", "The source URL redirected too many times.");
                    }

                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidFileTaskRequestException("source_fetch_failed", "The source URL returned an unsuccessful response.");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null &&
                    !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidFileTaskRequestException("import_parse_failed", "The source URL did not return HTML.");
                }

                var bytes = await ReadLimitedAsync(response.Content, cancellationToken);
                return new FetchedHtml(
                    bytes,
                    current,
                    mediaType ?? "text/html",
                    response.Content.Headers.ContentType?.CharSet?.Trim('"'));
            }
        }

        throw new InvalidFileTaskRequestException("source_fetch_failed", "The source URL could not be fetched.");
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumBytes)
        {
            throw new InvalidFileTaskRequestException("content_too_large", "The source URL response is too large.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaximumBytes)
            {
                throw new InvalidFileTaskRequestException("content_too_large", "The source URL response is too large.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return destination.ToArray();
    }

    private static async Task EnsurePublicAddressAsync(Uri uri, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            }
            catch (SocketException exception)
            {
                throw new InvalidFileTaskRequestException("source_fetch_failed", "The source URL host could not be resolved.", exception);
            }
        }

        if (addresses.Length == 0 || addresses.Any(address => !PublicAddressHttpHandler.IsPublic(address)))
        {
            throw new InvalidFileTaskRequestException("unsafe_source_url", "The source URL points to a prohibited network address.");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool HasProhibitedNetworkCause(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProhibitedNetworkException)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record FetchedHtml(
    byte[] Bytes,
    Uri FinalUrl,
    string MediaType,
    string? DeclaredEncoding);

internal static class PublicAddressHttpHandler
{
    public static SocketsHttpHandler Create() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = ConnectAsync
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !address.Equals(IPAddress.IPv6Any) &&
                   !address.Equals(IPAddress.IPv6None) &&
                   (bytes[0] & 0xFE) != 0xFC;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return bytes[0] switch
        {
            0 or 10 or 127 => false,
            100 when bytes[1] is >= 64 and <= 127 => false,
            169 when bytes[1] == 254 => false,
            172 when bytes[1] is >= 16 and <= 31 => false,
            192 when bytes[1] == 168 => false,
            >= 224 => false,
            _ => true
        };
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
        {
            throw new ProhibitedNetworkException("The destination resolved to a prohibited network address.");
        }

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                var remoteAddress = ((IPEndPoint)socket.RemoteEndPoint!).Address;
                if (!IsPublic(remoteAddress))
                {
                    socket.Dispose();
                    throw new ProhibitedNetworkException("The connected destination is a prohibited network address.");
                }

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or HttpRequestException)
            {
                socket.Dispose();
                lastError = exception;
            }
        }

        throw new HttpRequestException("The destination could not be connected.", lastError);
    }
}

internal sealed class ProhibitedNetworkException(string message) : HttpRequestException(message);

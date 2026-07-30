using System.Net;

namespace Vortex.Desktop.Services;

public enum DesktopProxyMode
{
    SystemDefault,
    NoProxy,
    Manual
}

public static class DesktopHttpHandlerFactory
{
    public static HttpClientHandler Create(DesktopSettings settings)
    {
        var handler = new HttpClientHandler();
        switch (settings.SafeProxyMode)
        {
            case DesktopProxyMode.NoProxy:
                handler.UseProxy = false;
                break;
            case DesktopProxyMode.Manual:
                handler.UseProxy = true;
                handler.Proxy = CreateManualProxy(settings);
                break;
            default:
                handler.UseProxy = true;
                handler.Proxy = null;
                break;
        }

        return handler;
    }

    private static IWebProxy CreateManualProxy(DesktopSettings settings)
    {
        var httpProxy = ParseProxyUri(settings.ManualHttpProxyUrl, "HTTP");
        var httpsProxy = ParseProxyUri(settings.ManualHttpsProxyUrl, "HTTPS");
        if (httpProxy is null && httpsProxy is null)
        {
            throw new InvalidOperationException("Manual proxy mode requires an HTTP or HTTPS proxy URL.");
        }

        var proxy = new SchemeWebProxy(httpProxy, httpsProxy ?? httpProxy)
        {
            Credentials = string.IsNullOrWhiteSpace(settings.ProxyUsername)
                ? null
                : new NetworkCredential(settings.ProxyUsername, settings.ProxyPassword)
        };
        return proxy;
    }

    private static Uri? ParseProxyUri(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Manual {label} proxy URL must be an absolute HTTP or HTTPS URL without embedded credentials.");
        }
        return uri;
    }

    private sealed class SchemeWebProxy(Uri? httpProxy, Uri? httpsProxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination)
        {
            if (IsBypassed(destination)) return destination;
            return destination.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? httpsProxy ?? httpProxy ?? destination
                : httpProxy ?? httpsProxy ?? destination;
        }

        public bool IsBypassed(Uri host) =>
            host.IsLoopback ||
            host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }
}

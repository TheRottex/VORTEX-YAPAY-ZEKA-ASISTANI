using System.Net.Http.Headers;
using System.Text.Json;
using Vortex.Contracts;

namespace Vortex.Web.Pages;

internal static class WebAuth
{
    public const string TokenCookie = "__Host-vortex_access";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void SetTokenCookie(HttpResponse response, AuthResponse auth, bool isProduction)
    {
        var remaining = auth.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("The server returned an expired access token.");

        response.Cookies.Append(TokenCookie, auth.AccessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = isProduction,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            Expires = auth.ExpiresAt,
            MaxAge = remaining
        });
    }

    public static void ClearTokenCookie(HttpResponse response, bool isProduction) =>
        response.Cookies.Delete(TokenCookie, new CookieOptions
        {
            HttpOnly = true,
            Secure = isProduction,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true
        });

    public static HttpClient CreateServerClient(IHttpClientFactory factory, HttpRequest request)
    {
        var client = factory.CreateClient("vortex-server");
        if (request.Cookies.TryGetValue(TokenCookie, out var token) && !string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

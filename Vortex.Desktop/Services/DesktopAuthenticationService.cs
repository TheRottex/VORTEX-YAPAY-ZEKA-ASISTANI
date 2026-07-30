using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Vortex.Shared;

namespace Vortex.Desktop.Services;

public sealed class LoginFailureException(string reason, string? correlationId = null) : Exception("Password login failed.")
{
    public string Reason { get; } = reason;
    public string? CorrelationId { get; } = correlationId;
}

public interface IDesktopAuthenticationService
{
    Task<UserProfileDto?> SignInWithBrowserAsync(bool preferRegister, CancellationToken cancellationToken);
    Task<UserProfileDto?> SignInWithProviderAsync(string provider, CancellationToken cancellationToken);
    Task<UserProfileDto?> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken);
    Task<UserProfileDto?> RegisterAsync(string email, string password, string displayName, string firstName, string lastName, string birthDate, string? phoneNumber, CancellationToken cancellationToken);
}

public sealed class DesktopAuthenticationService(BackendClient backendClient, Uri webBaseUri) : IDesktopAuthenticationService
{
    private readonly Uri _webBaseUri = webBaseUri ?? throw new ArgumentNullException(nameof(webBaseUri));

    public Task<UserProfileDto?> SignInWithBrowserAsync(bool preferRegister, CancellationToken cancellationToken)
        => RunLoopbackFlowAsync((authorizationUrl, authorizePath, _) => preferRegister
            ? BuildWebUrl("register", authorizePath)
            : authorizationUrl, cancellationToken);

    public Task<UserProfileDto?> SignInWithProviderAsync(string provider, CancellationToken cancellationToken)
        => RunLoopbackFlowAsync((_, authorizePath, sessionId) =>
            AppendQuery(BuildWebUrl($"auth/{provider}/start", authorizePath), new Dictionary<string, string> { ["desktopSessionId"] = sessionId.ToString() }), cancellationToken);

    public async Task<UserProfileDto?> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken)
    {
        var result = await backendClient.LoginDetailedAsync(email, password, cancellationToken);
        if (result.Auth is not null) return result.Auth.User;
        throw new LoginFailureException(result.Reason, result.CorrelationId);
    }

    public async Task<UserProfileDto?> RegisterAsync(string email, string password, string displayName, string firstName, string lastName, string birthDate, string? phoneNumber, CancellationToken cancellationToken)
    {
        var auth = await backendClient.RegisterAsync(email, password, displayName, firstName, lastName, birthDate, phoneNumber, cancellationToken);
        return auth?.User;
    }

    private async Task<UserProfileDto?> RunLoopbackFlowAsync(
        Func<string, string, Guid, string> buildAuthorizationUrl,
        CancellationToken cancellationToken)
    {
        var state = RandomUrlString(32);
        var verifier = RandomUrlString(64);
        var challenge = Sha256Url(verifier);
        var port = GetFreePort();

        var callbackUri = $"http://127.0.0.1:{port}/callback/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(callbackUri);
        listener.Start();
        DesktopLogService.Info("Desktop auth callback listener started on loopback.");

        try
        {
            var session = await backendClient.StartDesktopAuthAsync(
                new StartDesktopAuthRequest(
                    Sha256Url(state),
                    challenge,
                    callbackUri),
                cancellationToken);

            if (session is null)
            {
                throw new InvalidOperationException(
                    "Vortex Server giriş oturumu oluşturamadı.");
            }

            var authorizationUrl = AppendQuery(
                session.AuthorizationUrl,
                new Dictionary<string, string> { ["state"] = state });

            var authorizePath = new Uri(authorizationUrl).PathAndQuery;
            authorizationUrl = buildAuthorizationUrl(authorizationUrl, authorizePath, session.SessionId);

            DesktopLogService.Info($"Desktop auth authorization URL: {new Uri(authorizationUrl).GetLeftPart(UriPartial.Authority)}");

            var callbackTask = listener.GetContextAsync();
            OpenBrowser(authorizationUrl);
            DesktopLogService.Info("Desktop auth browser opened.");

            var completed = await Task.WhenAny(
                callbackTask,
                Task.Delay(TimeSpan.FromMinutes(5), cancellationToken));

            if (completed != callbackTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    "Tarayıcı giriş işlemi zaman aşımına uğradı.");
            }

            var context = await callbackTask;
            DesktopLogService.Info("1. Callback alındı.");

            var callbackResponseWritten = false;

            try
            {
                if (!TryReadCallback(context.Request, out var code, out var returnedState))
                {
                    DesktopLogService.Info("Desktop auth callback doğrulaması başarısız.");
                    await WriteCallbackResponseAsync(
                        context.Response,
                        "Giriş işlemi tamamlanamadı. Lütfen tekrar deneyin.",
                        false,
                        CancellationToken.None);
                    callbackResponseWritten = true;
                    throw new InvalidOperationException("Desktop auth callback geçersiz.");
                }

                if (!string.Equals(state, returnedState, StringComparison.Ordinal))
                {
                    DesktopLogService.Info("2. State doğrulaması başarısız.");
                    await WriteCallbackResponseAsync(
                        context.Response,
                        "Giriş işlemi tamamlanamadı. Lütfen tekrar deneyin.",
                        false,
                        CancellationToken.None);
                    callbackResponseWritten = true;
                    throw new InvalidOperationException("State doğrulaması başarısız.");
                }

                DesktopLogService.Info("2. State doğrulandı.");
                DesktopLogService.Info("3. Authorization code alındı.");
                DesktopLogService.Info("4. Code-token exchange çağrısı yapıldı.");

                var exchange = await backendClient.ExchangeDesktopCodeDetailedAsync(
                    new ExchangeDesktopCodeRequest(
                        session.SessionId,
                        code,
                        verifier,
                        state),
                    cancellationToken);

                DesktopLogService.Info(
                    $"5. Exchange HTTP durum kodu alındı: {(int)exchange.StatusCode}.");

                if (!exchange.StatusCode.IsSuccess())
                {
                    const string message = "Giriş doğrulaması tamamlanamadı. Lütfen tekrar deneyin.";
                    await WriteCallbackResponseAsync(
                        context.Response,
                        message,
                        false,
                        CancellationToken.None);
                    callbackResponseWritten = true;

                    throw new InvalidOperationException(message);
                }

                if (exchange.AuthResponse is null)
                {
                    await WriteCallbackResponseAsync(
                        context.Response,
                        "Token yanıtı okunamadı.",
                        false,
                        CancellationToken.None);
                    callbackResponseWritten = true;

                    throw new InvalidOperationException(
                        "AuthResponse parse edilemedi.");
                }

                DesktopLogService.Info("6. AuthResponse parse edildi.");

                await backendClient.SetTokenAsync(
                    exchange.AuthResponse.AccessToken,
                    cancellationToken);

                DesktopLogService.Info("7. Access token kaydedildi.");

                var user = await backendClient.GetMeAsync(cancellationToken);

                if (user is null)
                {
                    backendClient.Logout();

                    await WriteCallbackResponseAsync(
                        context.Response,
                        "/api/me çağrısı başarısız oldu. Lütfen tekrar giriş yapın.",
                        false,
                        CancellationToken.None);
                    callbackResponseWritten = true;

                    throw new InvalidOperationException(
                        "/api/me çağrısı başarısız oldu.");
                }

                DesktopLogService.Info("8. /api/me çağrısı başarılı oldu.");

                await WriteCallbackResponseAsync(
                    context.Response,
                    "Giriş başarılı. Artık işleminize Vortex uygulamasından devam edebilirsiniz.",
                    true,
                    CancellationToken.None);
                callbackResponseWritten = true;

                return user;
            }
            catch
            {
                if (!callbackResponseWritten)
                {
                    try
                    {
                        await WriteCallbackResponseAsync(
                            context.Response,
                            "Giriş işlemi tamamlanamadı.",
                            false,
                            CancellationToken.None);
                    }
                    catch
                    {
                        // Response might already be closed.
                    }
                }

                throw;
            }
        }
        finally
        {
            if (listener.IsListening)
            {
                listener.Stop();
            }
        }
    }

    private string BuildWebUrl(string relativePath, string authorizePath)
    {
        var path = relativePath.TrimStart('/');
        var baseUri = _webBaseUri.AbsoluteUri.TrimEnd('/');
        return $"{baseUri}/{path}?returnUrl={Uri.EscapeDataString(authorizePath)}";
    }

    private static async Task WriteCallbackResponseAsync(
        HttpListenerResponse response,
        string message,
        bool success,
        CancellationToken cancellationToken)
    {
        var title = success
            ? "Vortex girişi başarılı"
            : "Vortex girişi başarısız";

        var html = $$"""
            <!doctype html>
            <html lang="tr">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{WebUtility.HtmlEncode(title)}}</title>
            </head>
            <body style="
                margin:0;
                min-height:100vh;
                display:flex;
                align-items:center;
                justify-content:center;
                background:linear-gradient(135deg,#101213,#15172E);
                color:white;
                font-family:Segoe UI,Arial,sans-serif;">
                <main style="
                    max-width:650px;
                    padding:40px;
                    border-radius:20px;
                    background:rgba(255,255,255,0.06);
                    text-align:center;">
                    <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                    <p>{{WebUtility.HtmlEncode(message)}}</p>
                    <p>Bu pencereyi kapatabilirsiniz.</p>
                </main>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);

        response.StatusCode = success
            ? (int)HttpStatusCode.OK
            : (int)HttpStatusCode.BadRequest;

        response.ContentType = "text/html; charset=utf-8";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;

        await response.OutputStream.WriteAsync(
            bytes,
            cancellationToken);

        await response.OutputStream.FlushAsync(
            cancellationToken);

        response.Close();
    }

    internal static bool TryReadCallback(HttpListenerRequest request, out string code, out string state)
    {
        code = string.Empty;
        state = string.Empty;
        if (request.Url is not { } uri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 ||
            !IsLoopbackHost(uri.Host) ||
            uri.IsDefaultPort ||
            uri.Port is < 1 or > 65535 ||
            !string.Equals(uri.AbsolutePath, "/callback/", StringComparison.Ordinal) ||
            !HasOnlySingleCodeAndState(uri.Query))
        {
            return false;
        }

        var codes = request.QueryString.GetValues("code");
        var states = request.QueryString.GetValues("state");
        if (codes is not [var callbackCode] || states is not [var callbackState] ||
            string.IsNullOrWhiteSpace(callbackCode) || string.IsNullOrWhiteSpace(callbackState))
        {
            return false;
        }

        code = callbackCode;
        state = callbackState;
        return true;
    }

    private static bool HasOnlySingleCodeAndState(string query)
    {
        var pairs = query.TrimStart('?').Split('&', StringSplitOptions.None);
        if (pairs.Length != 2)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || pair.IndexOf('=', separator + 1) >= 0)
            {
                return false;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (!keys.Add(key) || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
        }

        return keys.SetEquals(["code", "state"]);
    }

    internal static bool IsLoopbackHost(string host) =>
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", [url]);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", [url]);
            }
        }
        catch (Exception ex)
        {
            DesktopLogService.Info($"Desktop auth browser launch failed. type={ex.GetType().Name}");
        }
    }

    private static string AppendQuery(string uri, Dictionary<string, string> values)
    {
        var separator = uri.Contains('?') ? "&" : "?";
        return uri + separator + string.Join(
            '&',
            values.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static string RandomUrlString(int bytes)
    {
        return TokenServiceCompat.Base64Url(
            RandomNumberGenerator.GetBytes(bytes));
    }

    private static string Sha256Url(string value)
    {
        return TokenServiceCompat.Base64Url(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(value)));
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(
            IPAddress.Loopback,
            0);

        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static class TokenServiceCompat
    {
        public static string Base64Url(byte[] bytes)
        {
            return Convert
                .ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}

internal static class HttpStatusCodeExtensions
{
    public static bool IsSuccess(this HttpStatusCode code)
    {
        var value = (int)code;
        return value >= 200 && value <= 299;
    }
}

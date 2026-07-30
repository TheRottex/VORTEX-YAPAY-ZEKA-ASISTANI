using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Vortex.Desktop.Services;

public enum DesktopNetworkDiagnosticStage
{
    Dns,
    Tcp,
    Tls,
    Health
}

public sealed record DesktopNetworkDiagnosticResult(
    DesktopNetworkDiagnosticStage Stage,
    bool Succeeded,
    string Reason);

public sealed record DesktopNetworkDiagnosticsReport(
    bool Succeeded,
    IReadOnlyList<DesktopNetworkDiagnosticResult> Results);

public sealed class DesktopNetworkDiagnosticsService
{
    private readonly TimeSpan _stageTimeout;
    private readonly Func<string, CancellationToken, Task> _dnsProbe;
    private readonly Func<string, int, CancellationToken, Task> _tcpProbe;
    private readonly Func<string, int, CancellationToken, Task> _tlsProbe;
    private readonly Func<Uri, CancellationToken, Task> _healthProbe;

    public DesktopNetworkDiagnosticsService(HttpClient httpClient, TimeSpan? stageTimeout = null)
        : this(
            stageTimeout ?? TimeSpan.FromSeconds(5),
            async (host, ct) => { _ = await System.Net.Dns.GetHostAddressesAsync(host, ct); },
            ProbeTcpAsync,
            ProbeTlsAsync,
            async (uri, ct) =>
            {
                using var response = await httpClient.GetAsync(uri, ct);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("Health endpoint returned a non-success status.");
            })
    {
    }

    public DesktopNetworkDiagnosticsService(
        TimeSpan stageTimeout,
        Func<string, CancellationToken, Task> dnsProbe,
        Func<string, int, CancellationToken, Task> tcpProbe,
        Func<string, int, CancellationToken, Task> tlsProbe,
        Func<Uri, CancellationToken, Task> healthProbe)
    {
        if (stageTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stageTimeout));
        _stageTimeout = stageTimeout;
        _dnsProbe = dnsProbe;
        _tcpProbe = tcpProbe;
        _tlsProbe = tlsProbe;
        _healthProbe = healthProbe;
    }

    public Task<DesktopNetworkDiagnosticsReport> RunAsync(Uri serverBaseUri, CancellationToken cancellationToken) =>
        RunAsync(serverBaseUri, new DesktopSettings(), cancellationToken);

    public async Task<DesktopNetworkDiagnosticsReport> RunAsync(Uri serverBaseUri, DesktopSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUri);
        ArgumentNullException.ThrowIfNull(settings);
        if (!serverBaseUri.IsAbsoluteUri || serverBaseUri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Public diagnostics require an absolute HTTPS server URI.", nameof(serverBaseUri));

        var port = serverBaseUri.IsDefaultPort ? 443 : serverBaseUri.Port;
        var healthUri = new Uri(serverBaseUri, "/health");
        var results = new List<DesktopNetworkDiagnosticResult>(4);

        if (settings.SafeProxyMode == DesktopProxyMode.Manual)
        {
            results.Add(new(DesktopNetworkDiagnosticStage.Dns, true, "proxy_managed"));
            results.Add(new(DesktopNetworkDiagnosticStage.Tcp, true, "proxy_managed"));
            results.Add(new(DesktopNetworkDiagnosticStage.Tls, true, "proxy_managed"));
        }
        else
        {
            if (!await RunStageAsync(DesktopNetworkDiagnosticStage.Dns, ct => _dnsProbe(serverBaseUri.DnsSafeHost, ct), results, cancellationToken)) return new(false, results);
            if (!await RunStageAsync(DesktopNetworkDiagnosticStage.Tcp, ct => _tcpProbe(serverBaseUri.DnsSafeHost, port, ct), results, cancellationToken)) return new(false, results);
            if (!await RunStageAsync(DesktopNetworkDiagnosticStage.Tls, ct => _tlsProbe(serverBaseUri.DnsSafeHost, port, ct), results, cancellationToken)) return new(false, results);
        }
        if (!await RunStageAsync(DesktopNetworkDiagnosticStage.Health, ct => _healthProbe(healthUri, ct), results, cancellationToken)) return new(false, results);
        return new(true, results);
    }

    private async Task<bool> RunStageAsync(
        DesktopNetworkDiagnosticStage stage,
        Func<CancellationToken, Task> probe,
        List<DesktopNetworkDiagnosticResult> results,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_stageTimeout);
        try
        {
            await probe(timeout.Token);
            results.Add(new(stage, true, "ok"));
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            results.Add(new(stage, false, "timeout"));
            return false;
        }
        catch (OperationCanceledException)
        {
            results.Add(new(stage, false, "cancelled"));
            throw;
        }
        catch (SocketException)
        {
            results.Add(new(stage, false, StageFailureReason(stage)));
            return false;
        }
        catch (AuthenticationException)
        {
            results.Add(new(stage, false, "tls_failed"));
            return false;
        }
        catch (HttpRequestException)
        {
            results.Add(new(stage, false, StageFailureReason(stage)));
            return false;
        }
        catch
        {
            results.Add(new(stage, false, StageFailureReason(stage)));
            return false;
        }
    }

    private static string StageFailureReason(DesktopNetworkDiagnosticStage stage) => stage switch
    {
        DesktopNetworkDiagnosticStage.Dns => "dns_failed",
        DesktopNetworkDiagnosticStage.Tcp => "tcp_failed",
        DesktopNetworkDiagnosticStage.Tls => "tls_failed",
        _ => "health_failed"
    };

    private static async Task ProbeTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
    }

    private static async Task ProbeTlsAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        await using var ssl = new SslStream(client.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancellationToken);
    }
}

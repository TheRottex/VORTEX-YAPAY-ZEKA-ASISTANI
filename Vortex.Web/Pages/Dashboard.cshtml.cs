using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vortex.Contracts;

namespace Vortex.Web.Pages;

public sealed class DashboardModel(IHttpClientFactory httpClientFactory, IWebHostEnvironment environment) : PageModel
{
    public UserProfileDto? Profile { get; private set; }
    public IReadOnlyList<LocalAgentDeviceDto> Devices { get; private set; } = Array.Empty<LocalAgentDeviceDto>();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.ContainsKey(WebAuth.TokenCookie)) return RedirectToPage("/Login", new { ReturnUrl = "/Dashboard" });
        return await LoadAsync(cancellationToken) ? Page() : SignOut();
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        var client = WebAuth.CreateServerClient(httpClientFactory, Request);
        var me = await client.GetAsync("/api/me", cancellationToken);
        if (me.StatusCode == HttpStatusCode.Unauthorized) return false;
        if (!me.IsSuccessStatusCode) return true;

        Profile = await me.Content.ReadFromJsonAsync<UserProfileDto>(WebAuth.JsonOptions, cancellationToken);
        var devices = await client.GetAsync("/api/devices", cancellationToken);
        if (devices.IsSuccessStatusCode)
            Devices = await devices.Content.ReadFromJsonAsync<List<LocalAgentDeviceDto>>(WebAuth.JsonOptions, cancellationToken) ?? Array.Empty<LocalAgentDeviceDto>();
        return true;
    }

    private IActionResult SignOut()
    {
        WebAuth.ClearTokenCookie(Response, environment.IsProduction());
        return RedirectToPage("/Login");
    }
}

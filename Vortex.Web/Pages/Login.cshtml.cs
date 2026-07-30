using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vortex.Contracts;

namespace Vortex.Web.Pages;

public sealed class LoginModel(IHttpClientFactory httpClientFactory, IWebHostEnvironment environment) : PageModel
{
    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty] public bool RememberMe { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet() => ReturnUrl = SafeReturnUrl(ReturnUrl);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var returnUrl = SafeReturnUrl(ReturnUrl);
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "E-posta ve parola gereklidir.";
            return Page();
        }

        var client = httpClientFactory.CreateClient("vortex-server");
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Email.Trim(), Password), WebAuth.JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ErrorMessage = response.StatusCode == HttpStatusCode.Unauthorized ? "E-posta veya parola hatalı." : "Giriş şu anda tamamlanamadı.";
            return Page();
        }

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(WebAuth.JsonOptions, cancellationToken);
        if (auth is null) { ErrorMessage = "Giriş yanıtı okunamadı."; return Page(); }

        WebAuth.SetTokenCookie(Response, auth, environment.IsProduction());
        return LocalRedirect(returnUrl);
    }

    private string SafeReturnUrl(string? value) => Url.IsLocalUrl(value) ? value! : "/Dashboard";
}

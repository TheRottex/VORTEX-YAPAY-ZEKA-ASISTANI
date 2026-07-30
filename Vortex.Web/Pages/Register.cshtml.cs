using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Vortex.Contracts;

namespace Vortex.Web.Pages;

public sealed class RegisterModel(IHttpClientFactory httpClientFactory, IWebHostEnvironment environment) : PageModel
{
    [BindProperty] public string FirstName { get; set; } = string.Empty;
    [BindProperty] public string LastName { get; set; } = string.Empty;
    [BindProperty] public string DisplayName { get; set; } = string.Empty;
    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty] public string ConfirmPassword { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet() => ReturnUrl = SafeReturnUrl(ReturnUrl);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var returnUrl = SafeReturnUrl(ReturnUrl);
        if (string.IsNullOrWhiteSpace(DisplayName) || string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Görünen ad, e-posta ve parola gereklidir.";
            return Page();
        }
        if (Password != ConfirmPassword) { ErrorMessage = "Parolalar eşleşmiyor."; return Page(); }

        var request = new RegisterRequest(Email.Trim(), Password, DisplayName.Trim(), EmptyToNull(FirstName), EmptyToNull(LastName));
        var client = httpClientFactory.CreateClient("vortex-server");
        var response = await client.PostAsJsonAsync("/api/auth/register", request, WebAuth.JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ErrorMessage = "Kayıt tamamlanamadı. Bilgileri kontrol edin.";
            return Page();
        }

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(WebAuth.JsonOptions, cancellationToken);
        if (auth is null) { ErrorMessage = "Kayıt yanıtı okunamadı."; return Page(); }

        WebAuth.SetTokenCookie(Response, auth, environment.IsProduction());
        return LocalRedirect(returnUrl);
    }

    private string SafeReturnUrl(string? value) => Url.IsLocalUrl(value) ? value! : "/Dashboard";
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

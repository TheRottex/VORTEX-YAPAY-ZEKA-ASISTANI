using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Vortex.Web.Pages;

public sealed class LogoutModel(IWebHostEnvironment environment) : PageModel
{
    public IActionResult OnPost()
    {
        WebAuth.ClearTokenCookie(Response, environment.IsProduction());
        return RedirectToPage("/Index");
    }
}

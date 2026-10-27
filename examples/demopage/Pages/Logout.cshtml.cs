using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Demopage.Pages;

/// <summary>
/// Logout: чистим cookie-сессию + Single Logout на стороне IdP (end_session_endpoint).
/// DefaultSignOutScheme = OIDC — SignOutAsync вторым аргументом уводит браузер в Keycloak.
/// </summary>
public class LogoutModel : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync("Cookies");
        await HttpContext.SignOutAsync("oidc", new AuthenticationProperties { RedirectUri = "/Logout" });
        return Page(); // при возврате из IdP покажем подтверждение
    }
}

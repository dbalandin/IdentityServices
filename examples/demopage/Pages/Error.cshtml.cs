using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;

namespace Demopage.Pages;

[AllowAnonymous]
public class ErrorModel : PageModel
{
    public string? Message { get; private set; }

    public void OnGet() => Message = HttpContext.Items["ErrorMessage"] as string;
}

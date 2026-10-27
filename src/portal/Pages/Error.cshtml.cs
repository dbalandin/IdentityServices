using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Portal.Pages;

[AllowAnonymous]
public class ErrorModel : PageModel
{
    public string Message { get; private set; } = "неизвестная ошибка";

    public void OnGet()
        => Message = HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error.Message ?? Message;
}

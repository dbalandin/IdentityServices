using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Portal.Pages;

[Authorize]
public class IndexModel : PageModel
{
    public string? Email => User.Identity?.Name;
    public string? Organization => User.Organization();
    public IReadOnlyList<string> Roles => User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
    public IReadOnlyList<string> Groups => User.Groups();
    public bool IsSuperAdmin => PortalAdminService.IsSuperAdmin(User);
    public bool IsOrgAdmin => Organization is not null && User.IsInRole("admin");

    public void OnGet() { }
}

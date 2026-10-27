using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Portal.Pages;

[Authorize(Policy = "OrgAdmin")]
public class UsersModel : PageModel
{
    private readonly PortalAdminService _svc;
    private readonly ILogger<UsersModel> _logger;

    public UsersModel(PortalAdminService svc, ILogger<UsersModel> logger)
    {
        _svc = svc;
        _logger = logger;
    }

    public IReadOnlyList<PortalUserView> Users { get; private set; } = [];
    public IReadOnlyList<PortalGroupView> Groups { get; private set; } = [];
    public IReadOnlyList<string> OrgServices { get; private set; } = [];
    public string? Organization => User.Organization();

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string? FirstName { get; set; }
    [BindProperty] public string? LastName { get; set; }
    [BindProperty] public string Role { get; set; } = "user";
    [BindProperty] public string[] SelectedGroups { get; set; } = [];
    [BindProperty] public string[] SelectedServices { get; set; } = [];
    [BindProperty] public string? Password { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Users = await _svc.ListUsersAsync(User, ct: ct);
            Groups = await _svc.ListGroupsAsync(User, ct);
            OrgServices = await GetOrgServicesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "список пользователей");
            TempData["Err"] = "не удалось загрузить список: " + ex.Message;
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        try
        {
            var req = new CreateUserRequest(
                Email, FirstName, LastName, Role,
                SelectedGroups, SelectedServices, Password, TemporaryPassword: true);
            var id = await _svc.CreateUserAsync(User, req, ct);
            TempData["Ok"] = $"пользователь {Email} создан (id {id})";
        }
        catch (PortalValidationException ex)
        {
            var details = ex.Details.Count > 0 ? "\n" + string.Join("\n", ex.Details) : "";
            TempData["Err"] = ex.Message + details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "создание пользователя");
            TempData["Err"] = "не удалось создать: " + ex.Message;
        }
        return RedirectToPage();
    }

    private async Task<IReadOnlyList<string>> GetOrgServicesAsync(CancellationToken ct)
    {
        try
        {
            var orgs = await _svc.ListOrganizationsForAdminAsync(User, ct);
            return orgs;
        }
        catch
        {
            return [];
        }
    }
}

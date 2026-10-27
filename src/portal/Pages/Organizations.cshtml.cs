using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Portal.Pages;

[Authorize(Policy = "SuperAdmin")]
public class OrganizationsModel : PageModel
{
    private readonly PortalAdminService _svc;
    private readonly ILogger<OrganizationsModel> _logger;

    public OrganizationsModel(PortalAdminService svc, ILogger<OrganizationsModel> logger)
    {
        _svc = svc;
        _logger = logger;
    }

    public IReadOnlyList<PortalOrgView> Orgs { get; private set; } = [];

    [BindProperty] public string Alias { get; set; } = "";
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string[] CreateServices { get; set; } = [];
    [BindProperty] public string UpdateAlias { get; set; } = "";
    [BindProperty] public string[] UpdateServices { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Orgs = await _svc.ListOrganizationsAsync(User, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "список организаций");
            TempData["Err"] = "не удалось загрузить список: " + ex.Message;
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        try
        {
            await _svc.CreateOrganizationAsync(User, Alias, Name, CreateServices, ct);
            TempData["Ok"] = $"организация {Alias} создана";
        }
        catch (PortalValidationException ex)
        {
            var details = ex.Details.Count > 0 ? "\n" + string.Join("\n", ex.Details) : "";
            TempData["Err"] = ex.Message + details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "создание организации");
            TempData["Err"] = "не удалось создать: " + ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAsync(CancellationToken ct)
    {
        try
        {
            var org = await _svc.UpdateOrganizationAsync(User, UpdateAlias, null, UpdateServices, ct);
            TempData["Ok"] = $"availableServices организации {org.Alias}: {string.Join(", ", org.AvailableServices)}";
        }
        catch (PortalValidationException ex)
        {
            var details = ex.Details.Count > 0 ? "\n" + string.Join("\n", ex.Details) : "";
            TempData["Err"] = ex.Message + details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "обновление организации");
            TempData["Err"] = "не удалось обновить: " + ex.Message;
        }
        return RedirectToPage();
    }
}

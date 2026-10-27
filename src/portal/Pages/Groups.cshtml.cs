using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Portal.Pages;

[Authorize(Policy = "OrgAdmin")]
public class GroupsModel : PageModel
{
    private readonly PortalAdminService _svc;
    private readonly ILogger<GroupsModel> _logger;

    public GroupsModel(PortalAdminService svc, ILogger<GroupsModel> logger)
    {
        _svc = svc;
        _logger = logger;
    }

    public IReadOnlyList<PortalGroupView> Groups { get; private set; } = [];
    public string? Organization => User.Organization();

    [BindProperty] public string Name { get; set; } = "";

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Groups = await _svc.ListGroupsAsync(User, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "список групп");
            TempData["Err"] = "не удалось загрузить список: " + ex.Message;
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        try
        {
            var g = await _svc.CreateGroupAsync(User, Name, ct);
            TempData["Ok"] = $"группа {g.Name} создана";
        }
        catch (PortalValidationException ex)
        {
            var details = ex.Details.Count > 0 ? "\n" + string.Join("\n", ex.Details) : "";
            TempData["Err"] = ex.Message + details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "создание группы");
            TempData["Err"] = "не удалось создать: " + ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, CancellationToken ct)
    {
        try
        {
            await _svc.DeleteGroupAsync(User, id, ct);
            TempData["Ok"] = "группа удалена";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "удаление группы");
            TempData["Err"] = "не удалось удалить: " + ex.Message;
        }
        return RedirectToPage();
    }
}

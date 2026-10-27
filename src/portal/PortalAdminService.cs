using System.Security.Claims;
using Portal.KeycloakAdmin;

namespace Portal;

/// <summary>Запрос на создание пользователя (API + Web UI используют одно и то же).</summary>
public sealed record CreateUserRequest(
    string Email,
    string? FirstName,
    string? LastName,
    string Role,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Services,
    string? Password,
    bool TemporaryPassword);

/// <summary>Запрос на обновление пользователя (назначения заменяются целиком).</summary>
public sealed record UpdateUserRequest(
    string? FirstName,
    string? LastName,
    bool? Enabled,
    string? Role,
    IReadOnlyList<string>? Groups,
    IReadOnlyList<string>? Services);

/// <summary>Пользователь организации в выдаче API/UI.</summary>
public sealed record PortalUserView(
    string Id,
    string Username,
    string? FirstName,
    string? LastName,
    bool Enabled,
    IReadOnlyList<string> Services,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Groups = null);

/// <summary>Группа для выдачи.</summary>
public sealed record PortalGroupView(string Id, string Name);

/// <summary>Организация для выдачи (суперадмин).</summary>
public sealed record PortalOrgView(string Alias, string Name, IReadOnlyList<string> AvailableServices);

/// <summary>
/// ЕДИНСТВЕННОЕ место инвариантов портала (task-07, план §5.7):
///   1) админ видит и меняет только пользователей/группы СВОЕЙ организации (иначе 403);
///   2) роли фиксированы (user/admin/auditor) — создавать роли нельзя;
///   3) сервисы — только из ServiceCatalog; пользователю назначаются только сервисы
///      из availableServices его организации (иначе 400);
///   4) группы организации именуются с префиксом `<orgAlias>-` (соглашение task-02).
/// Сверх этого портал ничего не умеет — создание realm-ролей и элементов scope API портала не предоставляет.
/// </summary>
public sealed class PortalAdminService
{
    private readonly KeycloakAdminClient _kc;
    private readonly ILogger<PortalAdminService> _logger;

    public PortalAdminService(KeycloakAdminClient kc, ILogger<PortalAdminService> logger)
    {
        _kc = kc;
        _logger = logger;
    }

    // ── контекст администратора ────────────────────────────────────────────────

    public static string? OrgAliasOf(ClaimsPrincipal user) => user.Organization();

    /// <summary>Суперадмин — аутентифицированный пользователь вне организаций (root, task-02).</summary>
    public static bool IsSuperAdmin(ClaimsPrincipal user)
        => user.Identity?.IsAuthenticated == true && OrgAliasOf(user) is null;

    public static string GroupPrefix(string orgAlias) => $"{orgAlias}-";

    public static bool IsOwnOrgGroupName(string orgAlias, string? groupName)
        => groupName is not null && groupName.StartsWith(GroupPrefix(orgAlias), StringComparison.Ordinal);

    /// <summary>Разрешить организацию администратора (политика OrgAdmin гарантирует роль+клейм; здесь — фактическая организация).</summary>
    private async Task<KcOrganization> RequireAdminOrgAsync(ClaimsPrincipal admin, CancellationToken ct)
    {
        var alias = OrgAliasOf(admin)
            ?? throw new PortalForbiddenException("у администратора нет организации (суперадмин управляет организациями: /api/organizations)");
        var org = await _kc.GetOrganizationByAliasAsync(alias, ct)
            ?? throw new PortalNotFoundException($"организация {alias} не найдена в realm");
        return org;
    }

    // ── пользователи своей организации ─────────────────────────────────────────

    public async Task<IReadOnlyList<PortalUserView>> ListUsersAsync(ClaimsPrincipal admin,
        string? search = null, int first = 0, int max = 100, CancellationToken ct = default)
    {
        var org = await RequireAdminOrgAsync(admin, ct);
        var members = await _kc.ListOrgMembersAsync(org.Id!, first, Math.Min(max, 300), search, ct);
        return members.Select(m => ToView(m)).ToList();
    }

    public async Task<PortalUserView> GetUserAsync(ClaimsPrincipal admin, string userId, CancellationToken ct)
    {
        var org = await RequireAdminOrgAsync(admin, ct);
        await EnsureOwnOrgMemberAsync(org.Id!, userId, ct);
        var user = await _kc.GetUserAsync(userId, ct);
        var roles = (await _kc.GetUserRealmRolesAsync(userId, ct)).Select(r => r.Name!).ToList();
        var groups = (await _kc.GetUserGroupsAsync(userId, ct)).Select(g => g.Name!).ToList();
        return ToView(user, roles, groups);
    }

    public async Task<string> CreateUserAsync(ClaimsPrincipal admin, CreateUserRequest req, CancellationToken ct)
    {
        var org = await RequireAdminOrgAsync(admin, ct);

        // — валидация (инварианты 2–3) —
        var errors = new List<string>();

        var email = req.Email?.Trim().ToLowerInvariant() ?? "";
        if (email.Length < 3 || !email.Contains('@') || email.Contains(' '))
            errors.Add("email обязателен и должен содержать '@' (username = email, контракт jwt-claims.md §3)");
        if (email.Length > 255)
            errors.Add("email длиннее 255 символов");

        var role = string.IsNullOrWhiteSpace(req.Role) ? "user" : req.Role.Trim();
        if (!ServiceCatalog.IsKnownRole(role))
            errors.Add($"роль '{role}' вне фиксированного набора: {string.Join(", ", ServiceCatalog.Roles)} (создание ролей невозможно, PRD FR 6.6)");

        var services = ValidateServices(req.Services, org, errors);

        if (req.Password is { Length: > 0 } pwd && pwd.Length < 8)
            errors.Add("пароль короче 8 символов (password policy realm)");

        if (errors.Count > 0)
            throw new PortalValidationException("запрос не прошёл валидацию", errors);

        // — группы: только существующие группы своей организации (инвариант 4) —
        var groups = await ResolveOwnOrgGroupsAsync(org, req.Groups, ct);

        var existing = await _kc.GetUserByUsernameAsync(email, ct);
        if (existing is not null)
            throw new PortalValidationException($"пользователь {email} уже существует");

        // — создание пользователя —
        var user = new KcUser
        {
            Username = email,
            Email = email,
            FirstName = req.FirstName?.Trim(),
            LastName = req.LastName?.Trim(),
            Enabled = true,
            EmailVerified = true,
            Attributes = new() { ["services"] = [string.Join(' ', services)] },
        };
        var userId = await _kc.CreateUserAsync(user, ct);

        // роли: у каждого есть user; admin/auditor — дополнительные (контракт §4)
        var roleNames = new List<string> { "user" };
        if (role != "user") roleNames.Add(role);
        var roleReps = new List<KcRole>();
        foreach (var rn in roleNames)
            roleReps.Add(await _kc.GetRealmRoleAsync(rn, ct));
        await _kc.AddRealmRolesAsync(userId, roleReps, ct);

        foreach (var g in groups)
            await _kc.AddUserToGroupAsync(userId, g.Id!, ct);

        await _kc.AddOrgMemberAsync(org.Id!, userId, ct);

        if (req.Password is { Length: > 0 } password)
            await _kc.SetPasswordAsync(userId, password, req.TemporaryPassword, ct);
        else
            _logger.LogInformation("пользователь {Email} создан без пароля — задайте пароль или сброс", email);

        _logger.LogInformation("админ {Admin} создал пользователя {Email} в организации {Org}",
            admin.Identity?.Name, email, org.Alias);
        return userId;
    }

    public async Task UpdateUserAsync(ClaimsPrincipal admin, string userId, UpdateUserRequest req, CancellationToken ct)
    {
        var org = await RequireAdminOrgAsync(admin, ct);
        await EnsureOwnOrgMemberAsync(org.Id!, userId, ct);

        var user = await _kc.GetUserAsync(userId, ct);

        if (req.FirstName is not null) user.FirstName = req.FirstName.Trim();
        if (req.LastName is not null) user.LastName = req.LastName.Trim();
        if (req.Enabled is { } enabled) user.Enabled = enabled;

        // services (инвариант 3): замена целиком
        if (req.Services is not null)
        {
            var errors = new List<string>();
            var services = ValidateServices(req.Services, org, errors);
            if (errors.Count > 0) throw new PortalValidationException("services не прошёл валидацию", errors);
            user.Attributes ??= new();
            user.Attributes["services"] = [string.Join(' ', services)];
        }
        await _kc.UpdateUserAsync(userId, user, ct);

        // роль (инвариант 2): прикладные роли заменяются на user + выбранную
        if (req.Role is not null)
        {
            if (!ServiceCatalog.IsKnownRole(req.Role))
                throw new PortalValidationException($"роль '{req.Role}' вне фиксированного набора: {string.Join(", ", ServiceCatalog.Roles)}");
            var desired = new List<string> { "user" };
            if (req.Role != "user") desired.Add(req.Role);
            var current = (await _kc.GetUserRealmRolesAsync(userId, ct))
                .Where(r => ServiceCatalog.Roles.Contains(r.Name)).ToList();
            var toAdd = desired.Except(current.Select(r => r.Name!)).ToList();
            var toRemove = current.Where(r => !desired.Contains(r.Name!)).ToList();
            if (toAdd.Count > 0)
            {
                var reps = new List<KcRole>();
                foreach (var rn in toAdd) reps.Add(await _kc.GetRealmRoleAsync(rn, ct));
                await _kc.AddRealmRolesAsync(userId, reps, ct);
            }
            if (toRemove.Count > 0)
                await _kc.RemoveRealmRolesAsync(userId, toRemove, ct);
        }

        // группы (инвариант 4): замена целиком, только группы своей организации
        if (req.Groups is not null)
        {
            var desired = await ResolveOwnOrgGroupsAsync(org, req.Groups, ct);
            var current = await _kc.GetUserGroupsAsync(userId, ct);
            var desiredIds = desired.Select(g => g.Id!).ToHashSet();
            foreach (var g in current.Where(g => !desiredIds.Contains(g.Id!)))
                await _kc.RemoveUserFromGroupAsync(userId, g.Id!, ct);
            var currentIds = current.Select(g => g.Id!).ToHashSet();
            foreach (var g in desired.Where(g => !currentIds.Contains(g.Id!)))
                await _kc.AddUserToGroupAsync(userId, g.Id!, ct);
        }

        _logger.LogInformation("админ {Admin} обновил пользователя {UserId}", admin.Identity?.Name, userId);
    }

    public async Task DeleteUserAsync(ClaimsPrincipal admin, string userId, CancellationToken ct)
    {
        var org = await RequireAdminOrgAsync(admin, ct);
        await EnsureOwnOrgMemberAsync(org.Id!, userId, ct);
        await _kc.DeleteUserAsync(userId, ct);
        _logger.LogInformation("админ {Admin} удалил пользователя {UserId}", admin.Identity?.Name, userId);
    }

    /// <summary>Смена/сброс пароля: temporary=true → одноразовый пароль + required action UPDATE_PASSWORD (PRD FR 6).</summary>
    public async Task SetPasswordAsync(ClaimsPrincipal admin, string userId, string password, bool temporary, CancellationToken ct)
    {
        var org = await RequireAdminOrgAsync(admin, ct);
        await EnsureOwnOrgMemberAsync(org.Id!, userId, ct);
        if (password.Length < 8)
            throw new PortalValidationException("пароль короче 8 символов (password policy realm)");
        await _kc.SetPasswordAsync(userId, password, temporary, ct);
        _logger.LogInformation("админ {Admin} сменил пароль пользователя {UserId} (temporary={Temporary})",
            admin.Identity?.Name, userId, temporary);
    }

    /// <summary>availableServices СВОЕЙ организации (для отображения в UI при создании пользователя).</summary>
    public async Task<IReadOnlyList<string>> ListOrganizationsForAdminAsync(ClaimsPrincipal admin, CancellationToken ct)
    {
        var org = await RequireAdminOrgAsync(admin, ct);
        return org.AvailableServices;
    }

    // ── группы своей организации ───────────────────────────────────────────────

    public async Task<IReadOnlyList<PortalGroupView>> ListGroupsAsync(ClaimsPrincipal admin, CancellationToken ct)
    {
        var org = await RequireAdminOrgAsync(admin, ct);
        var prefix = GroupPrefix(org.Alias!);
        var all = await _kc.ListGroupsAsync(ct);
        return all
            .Where(g => g.Name?.StartsWith(prefix, StringComparison.Ordinal) == true)
            .Select(g => new PortalGroupView(g.Id!, g.Name!))
            .ToList();
    }

    public async Task<PortalGroupView> CreateGroupAsync(ClaimsPrincipal admin, string name, CancellationToken ct)
    {
        var org = await RequireAdminOrgAsync(admin, ct);
        var clean = name?.Trim() ?? "";
        var errors = new List<string>();
        if (clean.Length is < 1 or > 100) errors.Add("имя группы: 1–100 символов");
        if (clean.Contains('/')) errors.Add("имя группы не может содержать '/' (группы плоские, контракт jwt-claims.md §3)");
        if (clean.Contains(' ')) errors.Add("имя группы не может содержать пробелы");
        if (errors.Count > 0) throw new PortalValidationException("имя группы не прошло валидацию", errors);

        // префикс организации добавляет портал; явное указание чужого/своего префикса запрещено
        var allOrgs = await _kc.ListOrganizationsAsync(ct);
        foreach (var other in allOrgs)
        {
            if (other.Alias is { } alias && (clean == alias || clean.StartsWith(alias + "-", StringComparison.Ordinal)))
            {
                errors.Add($"не указывайте префикс организации ({alias}-) — портал добавит префикс {org.Alias}- сам");
                break;
            }
        }
        if (errors.Count > 0) throw new PortalValidationException("имя группы не прошло валидацию", errors);

        var fullName = GroupPrefix(org.Alias!) + clean;
        var existing = await _kc.ListGroupsAsync(ct);
        if (existing.Any(g => g.Name == fullName))
            throw new PortalValidationException($"группа {fullName} уже существует");

        var id = await _kc.CreateGroupAsync(fullName, ct);
        _logger.LogInformation("админ {Admin} создал группу {Group}", admin.Identity?.Name, fullName);
        return new PortalGroupView(id, fullName);
    }

    public async Task DeleteGroupAsync(ClaimsPrincipal admin, string groupId, CancellationToken ct)
    {
        var org = await RequireAdminOrgAsync(admin, ct);
        var group = await _kc.GetGroupAsync(groupId, ct)
            ?? throw new PortalNotFoundException($"группа {groupId} не найдена");
        if (!IsOwnOrgGroupName(org.Alias!, group.Name))
            throw new PortalForbiddenException($"группа '{group.Name}' вне организации {org.Alias}");
        await _kc.DeleteGroupAsync(groupId, ct);
        _logger.LogInformation("админ {Admin} удалил группу {Group}", admin.Identity?.Name, group.Name);
    }

    // ── суперадмин: организации и их availableServices ─────────────────────────

    public async Task<IReadOnlyList<PortalOrgView>> ListOrganizationsAsync(ClaimsPrincipal admin, CancellationToken ct)
    {
        RequireSuperAdmin(admin);
        var orgs = await _kc.ListOrganizationsAsync(ct);
        return orgs.Select(o => new PortalOrgView(o.Alias!, o.Name ?? o.Alias!, o.AvailableServices)).ToList();
    }

    public async Task<PortalOrgView> CreateOrganizationAsync(ClaimsPrincipal admin, string alias, string name,
        IReadOnlyList<string> services, CancellationToken ct)
    {
        RequireSuperAdmin(admin);
        var errors = new List<string>();
        alias = alias?.Trim().ToLowerInvariant() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(alias, "^[a-z0-9][a-z0-9-]{1,30}$"))
            errors.Add("alias: 2–31 символ [a-z0-9-], начинается с цифры/буквы");
        var known = ValidateAgainstCatalog(services, errors);
        if (errors.Count > 0) throw new PortalValidationException("организация не прошла валидацию", errors);

        if (await _kc.GetOrganizationByAliasAsync(alias, ct) is not null)
            throw new PortalValidationException($"организация {alias} уже существует");
        await _kc.CreateOrganizationAsync(alias, name?.Trim() ?? alias, known, ct);
        _logger.LogInformation("суперадмин {Admin} создал организацию {Alias}", admin.Identity?.Name, alias);
        return new PortalOrgView(alias, name?.Trim() ?? alias, known);
    }

    public async Task<PortalOrgView> UpdateOrganizationAsync(ClaimsPrincipal admin, string alias,
        string? name, IReadOnlyList<string> services, CancellationToken ct)
    {
        RequireSuperAdmin(admin);
        var org = await _kc.GetOrganizationByAliasAsync(alias, ct)
            ?? throw new PortalNotFoundException($"организация {alias} не найдена");
        var errors = new List<string>();
        var known = ValidateAgainstCatalog(services, errors);
        if (errors.Count > 0) throw new PortalValidationException("availableServices не прошёл валидацию", errors);
        await _kc.UpdateOrganizationAsync(org.Id!, name?.Trim() is { Length: > 0 } n ? n : org.Name!, known, ct);
        _logger.LogInformation("суперадмин {Admin} обновил организацию {Alias}: services={Services}",
            admin.Identity?.Name, alias, string.Join(",", known));
        return new PortalOrgView(org.Alias!, name?.Trim() is { Length: > 0 } n2 ? n2 : org.Name!, known);
    }

    // ── приватное ──────────────────────────────────────────────────────────────

    private static void RequireSuperAdmin(ClaimsPrincipal admin)
    {
        if (!IsSuperAdmin(admin))
            throw new PortalForbiddenException("операция доступна только суперадмину (пользователю вне организаций)");
    }

    /// <summary>Инвариант 1: целевой пользователь обязан состоять в организации администратора.</summary>
    private async Task EnsureOwnOrgMemberAsync(string orgId, string userId, CancellationToken ct)
    {
        if (!await _kc.IsOrgMemberAsync(orgId, userId, ct))
            throw new PortalForbiddenException("пользователь вне вашей организации");
    }

    /// <summary>Сервисы: ⊆ каталог (фиксирован) и ⊆ availableServices организации.</summary>
    private static IReadOnlyList<string> ValidateServices(IReadOnlyList<string> requested,
        KcOrganization org, List<string> errors)
    {
        var known = ValidateAgainstCatalog(requested, errors);
        var outside = known.Except(org.AvailableServices).ToList();
        if (outside.Count > 0)
            errors.Add($"сервисы вне availableServices организации {org.Alias}: {string.Join(", ", outside)}");
        return known;
    }

    private static IReadOnlyList<string> ValidateAgainstCatalog(IReadOnlyList<string> requested, List<string> errors)
    {
        var result = new List<string>();
        foreach (var s in requested.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()))
        {
            if (!ServiceCatalog.IsKnownService(s))
                errors.Add($"сервис '{s}' вне фиксированного каталога: {string.Join(", ", ServiceCatalog.Services)} (создание элементов scope невозможно, PRD FR 6.6)");
            else if (!result.Contains(s, StringComparer.Ordinal))
                result.Add(s);
        }
        return result;
    }

    /// <summary>Группы назначения: каждая должна существовать и принадлежать организации администратора.</summary>
    private async Task<List<KcGroup>> ResolveOwnOrgGroupsAsync(KcOrganization org,
        IReadOnlyList<string> requested, CancellationToken ct)
    {
        if (requested.Count == 0) return [];
        var all = await _kc.ListGroupsAsync(ct);
        var result = new List<KcGroup>();
        foreach (var name in requested.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()))
        {
            var group = all.FirstOrDefault(g => g.Name == name);
            if (group is null)
                throw new PortalValidationException($"группа {name} не найдена");
            if (!IsOwnOrgGroupName(org.Alias!, group.Name))
                throw new PortalForbiddenException($"группа '{group.Name}' вне организации {org.Alias}");
            result.Add(group);
        }
        return result;
    }

    private static PortalUserView ToView(KcUser u,
        IReadOnlyList<string>? roles = null, IReadOnlyList<string>? groups = null)
        => new(u.Id!, u.Username ?? u.Email ?? u.Id!, u.FirstName, u.LastName, u.Enabled, u.Services, roles, groups);
}

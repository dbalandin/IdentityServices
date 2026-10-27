using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Portal;

namespace Portal.KeycloakAdmin;

/// <summary>
/// Клиент Keycloak Admin REST API от сервис-аккаунта клиента `portal`
/// (client_credentials, контракт jwt-claims.md §6; роли сервис-аккаунта выдаёт
/// init-скрипт task-02: manage-users/view-users/query-users/view-realm/
/// manage-organizations/view-organizations/query-organizations).
///
/// Клиент ТОЛЬКО транспортирует вызовы — инварианты org-изоляции и валидации
/// (services ⊆ availableServices, префикс групп, фиксированные роли) живут
/// в PortalAdminService, единственном месте, где они проверяются.
/// </summary>
public sealed class KeycloakAdminClient
{
    private readonly HttpClient _http;
    private readonly AdminTokenProvider _tokens;
    private readonly PortalOptions _opt;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public KeycloakAdminClient(IHttpClientFactory factory, AdminTokenProvider tokens, PortalOptions opt)
    {
        _http = factory.CreateClient("keycloak-admin");
        _tokens = tokens;
        _opt = opt;
    }

    private string AdminBase => $"{_opt.KeycloakBaseUrl.TrimEnd('/')}/admin/realms/{_opt.Realm}";

    private async Task<HttpRequestMessage> AuthedAsync(HttpMethod method, string url,
        object? body = null, CancellationToken ct = default)
    {
        var token = await _tokens.GetAccessTokenAsync(ct);
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: Json);
        return req;
    }

    private async Task SendExpectAsync(HttpRequestMessage req, System.Net.HttpStatusCode expected,
        CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode != expected)
            throw new KeycloakAdminException(
                $"{req.Method} {req.RequestUri!.AbsolutePath}: Keycloak ответил {resp.StatusCode}");
    }

    // ── пользователи ────────────────────────────────────────────────────────────

    public async Task<KcUser?> GetUserByUsernameAsync(string username, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Get,
            $"{AdminBase}/users?username={Uri.EscapeDataString(username)}&exact=true", ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var users = await resp.Content.ReadFromJsonAsync<List<KcUser>>(Json, ct);
        return users?.FirstOrDefault(u => u.Username == username);
    }

    public async Task<KcUser> GetUserAsync(string userId, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Get, $"{AdminBase}/users/{userId}", ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new PortalNotFoundException($"пользователь {userId} не найден");
        var user = await resp.Content.ReadFromJsonAsync<KcUser>(Json, ct);
        return user ?? throw new KeycloakAdminException($"пустой ответ пользователей {userId}");
    }

    /// <summary>Создать пользователя; возвращает id из Location-заголовка.</summary>
    public async Task<string> CreateUserAsync(KcUser user, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Post, $"{AdminBase}/users", user, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new PortalValidationException($"пользователь {user.Username} уже существует");
        if (!resp.IsSuccessStatusCode)
            throw new KeycloakAdminException($"создание пользователя: Keycloak ответил {resp.StatusCode}");
        var location = resp.Headers.Location?.ToString()
            ?? throw new KeycloakAdminException("Keycloak не вернул Location при создании пользователя");
        return location[(location.LastIndexOf('/') + 1)..];
    }

    public Task UpdateUserAsync(string userId, KcUser user, CancellationToken ct)
        => SendAsync(userId, user, ct);

    private async Task SendAsync(string userId, KcUser user, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Put, $"{AdminBase}/users/{userId}", user, ct: ct);
        await SendExpectAsync(req, System.Net.HttpStatusCode.NoContent, ct);
    }

    public async Task DeleteUserAsync(string userId, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Delete, $"{AdminBase}/users/{userId}", ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new KeycloakAdminException($"удаление пользователя: Keycloak ответил {resp.StatusCode}");
    }

    /// <summary>Смена/сброс пароля; temporary=true → одноразовый пароль + required action UPDATE_PASSWORD.</summary>
    public async Task SetPasswordAsync(string userId, string password, bool temporary, CancellationToken ct)
    {
        var body = new { type = "password", value = password, temporary };
        using var req = await AuthedAsync(HttpMethod.Put, $"{AdminBase}/users/{userId}/reset-password", body, ct: ct);
        await SendExpectAsync(req, System.Net.HttpStatusCode.NoContent, ct);
    }

    // ── роли (только фиксированные user/admin/auditor — см. ServiceCatalog) ─────

    public async Task<KcRole> GetRealmRoleAsync(string name, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Get, $"{AdminBase}/roles/{Uri.EscapeDataString(name)}", ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new PortalValidationException($"роль {name} не найдена в realm (роли фиксированы: {string.Join(", ", ServiceCatalog.Roles)})");
        var role = await resp.Content.ReadFromJsonAsync<KcRole>(Json, ct);
        return role ?? throw new KeycloakAdminException($"пустой ответ роли {name}");
    }

    public async Task<IReadOnlyList<KcRole>> GetUserRealmRolesAsync(string userId, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Get, $"{AdminBase}/users/{userId}/role-mappings/realm", ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        return await resp.Content.ReadFromJsonAsync<List<KcRole>>(Json, ct) ?? [];
    }

    public async Task AddRealmRolesAsync(string userId, IReadOnlyList<KcRole> roles, CancellationToken ct)
    {
        if (roles.Count == 0) return;
        using var req = await AuthedAsync(HttpMethod.Post, $"{AdminBase}/users/{userId}/role-mappings/realm", roles, ct: ct);
        await SendExpectAsync(req, System.Net.HttpStatusCode.NoContent, ct);
    }

    public async Task RemoveRealmRolesAsync(string userId, IReadOnlyList<KcRole> roles, CancellationToken ct)
    {
        if (roles.Count == 0) return;
        // DELETE с телом: KC требует id+name каждой роли
        using var req = await AuthedAsync(HttpMethod.Delete, $"{AdminBase}/users/{userId}/role-mappings/realm", roles, ct: ct);
        await SendExpectAsync(req, System.Net.HttpStatusCode.NoContent, ct);
    }

    // ── группы ──────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<KcGroup>> ListGroupsAsync(CancellationToken ct, int max = 500)
    {
        using var req = await AuthedAsync(HttpMethod.Get, $"{AdminBase}/groups?max={max}", ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        return await resp.Content.ReadFromJsonAsync<List<KcGroup>>(Json, ct) ?? [];
    }

    public async Task<KcGroup?> GetGroupAsync(string groupId, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Get, $"{AdminBase}/groups/{groupId}", ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<KcGroup>(Json, ct);
    }

    public async Task<string> CreateGroupAsync(string name, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Post, $"{AdminBase}/groups", new { name }, ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new PortalValidationException($"группа {name} уже существует");
        if (!resp.IsSuccessStatusCode)
            throw new KeycloakAdminException($"создание группы: Keycloak ответил {resp.StatusCode}");
        var location = resp.Headers.Location?.ToString()
            ?? throw new KeycloakAdminException("Keycloak не вернул Location при создании группы");
        return location[(location.LastIndexOf('/') + 1)..];
    }

    public async Task DeleteGroupAsync(string groupId, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Delete, $"{AdminBase}/groups/{groupId}", ct: ct);
        await SendExpectAsync(req, System.Net.HttpStatusCode.NoContent, ct);
    }

    public async Task<IReadOnlyList<KcGroup>> GetUserGroupsAsync(string userId, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Get, $"{AdminBase}/users/{userId}/groups?max=500", ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        return await resp.Content.ReadFromJsonAsync<List<KcGroup>>(Json, ct) ?? [];
    }

    public async Task AddUserToGroupAsync(string userId, string groupId, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Put, $"{AdminBase}/users/{userId}/groups/{groupId}", ct: ct);
        await SendExpectAsync(req, System.Net.HttpStatusCode.NoContent, ct);
    }

    public async Task RemoveUserFromGroupAsync(string userId, string groupId, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Delete, $"{AdminBase}/users/{userId}/groups/{groupId}", ct: ct);
        await SendExpectAsync(req, System.Net.HttpStatusCode.NoContent, ct);
    }

    // ── организации (Keycloak Organizations, KC 26+) ────────────────────────────

    public async Task<IReadOnlyList<KcOrganization>> ListOrganizationsAsync(CancellationToken ct, int max = 300)
    {
        using var req = await AuthedAsync(HttpMethod.Get, $"{AdminBase}/organizations?max={max}", ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        return await resp.Content.ReadFromJsonAsync<List<KcOrganization>>(Json, ct) ?? [];
    }

    public async Task<KcOrganization?> GetOrganizationByAliasAsync(string alias, CancellationToken ct)
    {
        var orgs = await ListOrganizationsAsync(ct);
        return orgs.FirstOrDefault(o =>
            string.Equals(o.Alias, alias, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> CreateOrganizationAsync(string alias, string name,
        IReadOnlyList<string> availableServices, CancellationToken ct)
    {
        var body = new
        {
            name,
            alias,
            enabled = true,
            attributes = new Dictionary<string, List<string>>
            {
                ["availableServices"] = availableServices.ToList(),
            },
        };
        using var req = await AuthedAsync(HttpMethod.Post, $"{AdminBase}/organizations", body, ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new PortalValidationException($"организация {alias} уже существует");
        if (!resp.IsSuccessStatusCode)
            throw new KeycloakAdminException($"создание организации: Keycloak ответил {resp.StatusCode}");
        var location = resp.Headers.Location?.ToString()
            ?? throw new KeycloakAdminException("Keycloak не вернул Location при создании организации");
        return location[(location.LastIndexOf('/') + 1)..];
    }

    /// <summary>Обновление организации: PUT заменяет attributes целиком — отправляем полный набор.</summary>
    public async Task UpdateOrganizationAsync(string orgId, string name,
        IReadOnlyList<string> availableServices, CancellationToken ct)
    {
        var body = new
        {
            name,
            attributes = new Dictionary<string, List<string>>
            {
                ["availableServices"] = availableServices.ToList(),
            },
        };
        using var req = await AuthedAsync(HttpMethod.Put, $"{AdminBase}/organizations/{orgId}", body, ct: ct);
        await SendExpectAsync(req, System.Net.HttpStatusCode.NoContent, ct);
    }

    public async Task<IReadOnlyList<KcUser>> ListOrgMembersAsync(string orgId,
        int first = 0, int max = 100, string? search = null, CancellationToken ct = default)
    {
        var url = $"{AdminBase}/organizations/{orgId}/members?first={first}&max={max}";
        if (!string.IsNullOrWhiteSpace(search))
            url += $"&search={Uri.EscapeDataString(search)}";
        using var req = await AuthedAsync(HttpMethod.Get, url, ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        return await resp.Content.ReadFromJsonAsync<List<KcUser>>(Json, ct) ?? [];
    }

    /// <summary>Член ли пользователь организации (200/404 от KC).</summary>
    public async Task<bool> IsOrgMemberAsync(string orgId, string userId, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Get,
            $"{AdminBase}/organizations/{orgId}/members/{userId}", ct: ct);
        using var resp = await _http.SendAsync(req, ct);
        return resp.StatusCode == System.Net.HttpStatusCode.OK;
    }

    /// <summary>Добавить пользователя в организацию (тело — JSON-строка с id пользователя, как в KC API).</summary>
    public async Task AddOrgMemberAsync(string orgId, string userId, CancellationToken ct)
    {
        using var req = await AuthedAsync(HttpMethod.Post,
            $"{AdminBase}/organizations/{orgId}/members", userId, ct: ct);
        await SendExpectAsync(req, System.Net.HttpStatusCode.NoContent, ct);
    }
}

/// <summary>Кэш service-токена клиентa portal (client_credentials): один на процесс, обновляется до истечения.</summary>
public sealed class AdminTokenProvider
{
    private readonly HttpClient _http;
    private readonly PortalOptions _opt;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public AdminTokenProvider(IHttpClientFactory factory, PortalOptions opt)
    {
        _http = factory.CreateClient("keycloak-admin");
        _opt = opt;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _token;
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _token;
            var baseUri = _opt.KeycloakBaseUrl.TrimEnd('/');
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _opt.ClientId,
                ["client_secret"] = _opt.ClientSecret
                    ?? throw new InvalidOperationException("Portal:ClientSecret не задан (env PORTAL_CLIENT_SECRET)"),
            };
            using var resp = await _http.PostAsync(
                $"{baseUri}/realms/{_opt.Realm}/protocol/openid-connect/token",
                new FormUrlEncodedContent(form), ct);
            if (!resp.IsSuccessStatusCode)
                throw new KeycloakAdminException(
                    $"client_credentials для {_opt.ClientId}: Keycloak ответил {resp.StatusCode}");
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            _token = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new KeycloakAdminException("token endpoint не вернул access_token");
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var secs)
                ? secs : 60;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresIn - 30));
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Незапланированный ответ Keycloak Admin REST (5xx-эквивалент для логики портала).</summary>
public sealed class KeycloakAdminException(string message) : Exception(message);

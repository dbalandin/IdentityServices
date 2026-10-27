using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Portal;
using Portal.KeycloakAdmin;

// ─────────────────────────────────────────────────────────────────────────────
// portal — ASP.NET Core (.NET 10) портал администратора IdentityService (task-07):
//   • OIDC-логин администраторов (confidential-клиент `portal`, task-02);
//   • API /api/* — Bearer-JWT (токены realm rulink; UI ходит с cookie);
//   • Admin REST — от сервис-аккаунта клиента portal (client_credentials);
//   • org-изоляция и валидации — PortalAdminService (единственное место инвариантов);
//   • роли и элементы scope зафиксированы константами ServiceCatalog — создания нет.
// Секреты: только из окружения (PORTAL_CLIENT_SECRET), в репо/конфиге их нет.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

var identity = builder.Configuration.GetSection("Identity");
var authority = identity["Authority"]
    ?? throw new InvalidOperationException("Identity:Authority не задан (env: Identity__Authority)");

var portal = builder.Configuration.GetSection("Portal").Get<PortalOptions>() ?? new PortalOptions();
portal.ClientSecret ??= Environment.GetEnvironmentVariable("PORTAL_CLIENT_SECRET");
if (string.IsNullOrWhiteSpace(portal.ClientSecret))
    throw new InvalidOperationException(
        "Portal:ClientSecret не задан: укажите PORTAL_CLIENT_SECRET в окружении (из .env, см. .envexample)");
builder.Services.AddSingleton(portal);

var isHttpAuthority = Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
                     && authorityUri.Scheme != "https";

// ── аутентификация: диспетчер схем (свой, см. PortalAuthHandler) ─────────────
// /api/* — JWT (401 без редиректа); UI — cookie + OIDC-логин на Keycloak.

builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = PortalAuthHandler.SchemeName;
    o.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme; // logout на IdP
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
{
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
})
// OIDC-логин администратора: confidential-клиент portal (секрет из env)
.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
{
    o.Authority = authority;
    o.ClientId = portal.ClientId;
    o.ClientSecret = portal.ClientSecret;
    o.ResponseType = "code";
    o.UsePkce = true;
    o.SaveTokens = false;
    o.GetClaimsFromUserInfoEndpoint = false; // расширенные клеймы уже в токенах (мапперы task-02)
    o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme; // результат логина — cookie
    o.RequireHttpsMetadata = !isHttpAuthority;
    o.MapInboundClaims = false;              // клеймы контракта под своими именами
    o.TokenValidationParameters = new()
    {
        NameClaimType = "preferred_username", // username = email (контракт §3)
        ValidIssuer = authority,
    };
    o.Scope.Clear();
    o.Scope.Add("openid");
    o.Scope.Add("profile");
    o.Scope.Add("email");
    // organization/groups/services — дефолтные client scope realm'а (task-02)
})
// API: Bearer-JWT пользователей realm ( iss+подпись; aud опционален — контракт §3 )
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
{
    o.Authority = authority;
    o.RequireHttpsMetadata = !isHttpAuthority;
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new()
    {
        NameClaimType = "preferred_username",
        ValidIssuer = authority,
        ValidateAudience = false,
    };
});

// схема-диспетчер (IAuthenticationHandler без базового класса — регистрируем напрямую)
builder.Services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(
    o => o.AddScheme<PortalAuthHandler>(PortalAuthHandler.SchemeName, "Portal: cookie для UI, Bearer для /api"));

// ── авторизация: политики портала ─────────────────────────────────────────────
// OrgAdmin  — роль admin + клейм organization: управляет СВОЕЙ организацией;
// SuperAdmin — аутентифицированный пользователь ВНЕ организаций (root, task-02).

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("OrgAdmin", p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
        ctx.User.IsInRole("admin") && ctx.User.HasClaim(c => c.Type == KeycloakClaims.OrganizationClaim)));
    o.AddPolicy("SuperAdmin", p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
        !ctx.User.HasClaim(c => c.Type == KeycloakClaims.OrganizationClaim)));
});

// realm_access.roles → роли (контракт §7)
builder.Services.AddSingleton<IClaimsTransformation, RealmRolesTransformer>();

// Admin REST: один именованный HttpClient (BaseAddress из Portal:KeycloakBaseUrl);
// токен сервис-аккаунта кэширует AdminTokenProvider
builder.Services.AddHttpClient("keycloak-admin", (sp, http) =>
{
    var opt = sp.GetRequiredService<PortalOptions>();
    http.BaseAddress = new Uri(opt.KeycloakBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<AdminTokenProvider>();
builder.Services.AddSingleton<KeycloakAdminClient>();
builder.Services.AddScoped<PortalAdminService>();

builder.Services.AddRazorPages();

var app = builder.Build();

// ── ошибки API: Portal* → 403/404/400 (ProblemDetails-подобный JSON) ─────────

app.Use(async (ctx, next) =>
{
    try
    {
        await next(ctx);
    }
    catch (PortalForbiddenException ex) { await ApiErrorExtensions.WriteApiError(ctx, 403, ex.Message); }
    catch (PortalNotFoundException ex) { await ApiErrorExtensions.WriteApiError(ctx, 404, ex.Message); }
    catch (PortalValidationException ex) { await ApiErrorExtensions.WriteApiError(ctx, 400, ex.Message, ex.Details); }
    catch (KeycloakAdminException ex)
    {
        ctx.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogError(ex, "Keycloak Admin REST недоступен/ошибка");
        await ApiErrorExtensions.WriteApiError(ctx, 502, "identity backend error");
    }
});

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// ── API ──────────────────────────────────────────────────────────────────────

app.MapGet("/api/me", (ClaimsPrincipal user) => Results.Ok(new
{
    name = user.Identity?.Name,
    email = user.FindFirst("email")?.Value,
    organization = user.Organization(),
    roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
    groups = user.Groups(),
    isSuperAdmin = PortalAdminService.IsSuperAdmin(user),
    catalog = new { roles = ServiceCatalog.Roles, services = ServiceCatalog.Services },
}))
.WithSummary("Кто я: клеймы + фиксированный каталог ролей/сервисов")
.RequireAuthorization();

app.MapGet("/api/catalog", () => Results.Ok(new
{
    roles = ServiceCatalog.Roles,
    services = ServiceCatalog.Services,
}))
.WithSummary("Фиксированные константы портала: роли и сервисы (создавать нельзя)")
.RequireAuthorization();

app.MapGet("/api/users", async (ClaimsPrincipal user, PortalAdminService svc,
    string? search, int? first, int? max, CancellationToken ct) =>
    Results.Ok(await svc.ListUsersAsync(user, search, first ?? 0, max ?? 100, ct)))
.WithSummary("Пользователи СВОЕЙ организации")
.RequireAuthorization("OrgAdmin");

app.MapGet("/api/users/{id}", async (ClaimsPrincipal user, PortalAdminService svc,
    string id, CancellationToken ct) => Results.Ok(await svc.GetUserAsync(user, id, ct)))
.WithSummary("Пользователь своей организации (+роли/группы); чужая организация → 403")
.RequireAuthorization("OrgAdmin");

app.MapPost("/api/users", async (ClaimsPrincipal user, PortalAdminService svc,
    CreateUserRequest req, CancellationToken ct) =>
{
    var id = await svc.CreateUserAsync(user, req, ct);
    return Results.Created($"/api/users/{id}", new { id });
})
.WithSummary("Создать пользователя в своей организации (email, роль, группы, services ⊆ availableServices)")
.RequireAuthorization("OrgAdmin");

app.MapPut("/api/users/{id}", async (ClaimsPrincipal user, PortalAdminService svc,
    string id, UpdateUserRequest req, CancellationToken ct) =>
{
    await svc.UpdateUserAsync(user, id, req, ct);
    return Results.NoContent();
})
.WithSummary("Обновить пользователя своей организации (имя, роль, группы, services)")
.RequireAuthorization("OrgAdmin");

app.MapDelete("/api/users/{id}", async (ClaimsPrincipal user, PortalAdminService svc,
    string id, CancellationToken ct) =>
{
    await svc.DeleteUserAsync(user, id, ct);
    return Results.NoContent();
})
.WithSummary("Удалить пользователя своей организации")
.RequireAuthorization("OrgAdmin");

app.MapPost("/api/users/{id}/password", async (ClaimsPrincipal user, PortalAdminService svc,
    string id, PasswordRequest req, CancellationToken ct) =>
{
    await svc.SetPasswordAsync(user, id, req.Password, req.Temporary, ct);
    return Results.NoContent();
})
.WithSummary("Смена/сброс пароля; temporary=true → одноразовый пароль + UPDATE_PASSWORD при входе")
.RequireAuthorization("OrgAdmin");

app.MapGet("/api/groups", async (ClaimsPrincipal user, PortalAdminService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListGroupsAsync(user, ct)))
.WithSummary("Группы СВОЕЙ организации (префикс <org>-)")
.RequireAuthorization("OrgAdmin");

app.MapPost("/api/groups", async (ClaimsPrincipal user, PortalAdminService svc,
    GroupRequest req, CancellationToken ct) =>
{
    var group = await svc.CreateGroupAsync(user, req.Name, ct);
    return Results.Created($"/api/groups/{group.Id}", group);
})
.WithSummary("Создать группу своей организации (префикс добавляется автоматически)")
.RequireAuthorization("OrgAdmin");

app.MapDelete("/api/groups/{id}", async (ClaimsPrincipal user, PortalAdminService svc,
    string id, CancellationToken ct) =>
{
    await svc.DeleteGroupAsync(user, id, ct);
    return Results.NoContent();
})
.WithSummary("Удалить группу своей организации")
.RequireAuthorization("OrgAdmin");

app.MapGet("/api/organizations", async (ClaimsPrincipal user, PortalAdminService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListOrganizationsAsync(user, ct)))
.WithSummary("[суперадмин] Список организаций с availableServices")
.RequireAuthorization("SuperAdmin");

app.MapPost("/api/organizations", async (ClaimsPrincipal user, PortalAdminService svc,
    CreateOrgRequest req, CancellationToken ct) =>
{
    var org = await svc.CreateOrganizationAsync(user, req.Alias, req.Name, req.Services, ct);
    return Results.Created($"/api/organizations/{org.Alias}", org);
})
.WithSummary("[суперадмин] Создать организацию (services ⊆ каталог)")
.RequireAuthorization("SuperAdmin");

app.MapPut("/api/organizations/{alias}", async (ClaimsPrincipal user, PortalAdminService svc,
    string alias, UpdateOrgRequest req, CancellationToken ct) =>
    Results.Ok(await svc.UpdateOrganizationAsync(user, alias, req.Name, req.Services, ct)))
.WithSummary("[суперадмин] Изменить availableServices организации")
.RequireAuthorization("SuperAdmin");

app.Run();

// ── записи запросов API (body-модели) ────────────────────────────────────────

/// <summary>Смена/сброс пароля.</summary>
public sealed record PasswordRequest(string Password, bool Temporary);

/// <summary>Создание группы (имя без префикса организации).</summary>
public sealed record GroupRequest(string Name);

/// <summary>Создание организации (суперадмин).</summary>
public sealed record CreateOrgRequest(string Alias, string Name, IReadOnlyList<string> Services);

/// <summary>Обновление организации (суперадмин).</summary>
public sealed record UpdateOrgRequest(string? Name, IReadOnlyList<string> Services);

/// <summary>Для xUnit (WebApplicationFactory).</summary>
public partial class Program;

// ── хелпер ошибок API ────────────────────────────────────────────────────────

internal static class ApiErrorExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteApiError(HttpContext ctx, int status, string message,
        IReadOnlyList<string>? details = null)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error = message,
            details = details ?? [],
        }, Json));
    }
}

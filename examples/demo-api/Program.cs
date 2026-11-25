using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using IdentityService;

// ─────────────────────────────────────────────────────────────────────────────
// demo-api — .NET 10 minimal api, демонстрация паттерней P2/P3 (task-04):
//   • JWT bearer-валидация против realm rulink (режим Jwt библиотеки IdentityService, контракт §7)
//   • роли realm_access.roles → [Authorize(Roles = …)] (RealmRolesTransformer библиотеки)
//   • групповой ресурс по клейму groups (task-06)
// Собственной реализации аутентификации здесь нет: схемы, трансформация ролей и хелперы клеймов
// (Organization / Groups / RealmRoles / Services) — из библиотеки IdentityService.
// Конфигурация: секция Identity в appsettings.json + env (IDENTITY__AUTHORITY, IDENTITY__MODE,
// IDENTITY__AUDIENCE), GROUPDATA__ALLOWEDGROUPS. Секретов нет — это resource-server, только валидация.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// Mode = Jwt (appsettings.json): JWT Bearer на всех маршрутах — без токена 401, без редиректов;
// cookie/OIDC не регистрируются. Audience = demo-api — строгая проверка aud (контракт §3).
// Ошибки конфигурации (нет Authority и т.п.) падают здесь, при старте.
builder.Services.AddRulinkIdentityService(builder.Configuration);

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Групповой ресурс (P3): группа(ы)-владелец задаётся конфигом (по умолчанию orga-team-a —
// «рабочая пара» bob+carol из контракта §4); доступ — членство в groups-клейме.
var allowedGroups = (builder.Configuration["GroupData:AllowedGroups"] ?? "orga-team-a")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// ── endpoints ────────────────────────────────────────────────────────────────

// анонимный: health/echo (IdentityConfiguration — singleton, зарегистрированный библиотекой)
app.MapGet("/public", (IdentityConfiguration identity) => Results.Ok(new
{
    status = "ok",
    service = "demo-api",
    timeUtc = DateTimeOffset.UtcNow,
    identityAuthority = identity.Authority,
}))
.WithSummary("Анонимный health/echo");

// [Authorize]: клеймы пользователя по контракту JWT
app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
{
    sub = user.FindFirst("sub")?.Value,
    preferredUsername = user.Identity?.Name,
    email = user.FindFirst("email")?.Value,
    emailVerified = bool.TryParse(user.FindFirst("email_verified")?.Value, out var ev) && ev,
    organization = user.Organization(),
    groups = user.Groups(),
    realmRoles = user.RealmRoles(),
    scope = user.FindFirst("scope")?.Value,
    services = user.Services(),
}))
.RequireAuthorization()
.WithSummary("Клеймы текущего пользователя (контракт jwt-claims.md)");

// [Authorize(Roles = "admin")]
app.MapGet("/admin", () => Results.Ok(new { message = "admin ok", require = "role admin" }))
.RequireAuthorization(new AuthorizeAttribute { Roles = "admin" })
.WithSummary("Только роль admin");

// [Authorize(Roles = "auditor")]
app.MapGet("/audit", () => Results.Ok(new { message = "audit ok", require = "role auditor" }))
.RequireAuthorization(new AuthorizeAttribute { Roles = "auditor" })
.WithSummary("Только роль auditor");

// Групповой ресурс (P3): 200 членам групп-владельцев, 403 остальным аутентифицированным.
// Паттерн «данные видны всем членам группы»: пересечение groups пользователя с группами ресурса.
app.MapGet("/group-data", (ClaimsPrincipal user) =>
{
    var userGroups = user.Groups();
    var visible = allowedGroups.Intersect(userGroups, StringComparer.Ordinal).ToList();
    return visible.Count > 0
        ? Results.Ok(new
        {
            resourceGroups = visible,
            yourGroups = userGroups,
            organization = user.Organization(),
            data = new[]
            {
                new { id = 1, group = visible[0], title = $"Данные группы {visible[0]} #1" },
                new { id = 2, group = visible[0], title = $"Данные группы {visible[0]} #2" },
            },
        })
        : Results.Forbid(); // аутентифицирован, но не член групп-владельцев → 403
})
.RequireAuthorization()
.WithSummary("Групповой ресурс: только члены заданной группы (клейм groups)");

app.Run();

/// <summary>Для xUnit (WebApplicationFactory точка входа).</summary>
public partial class Program;

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using DemoApi;

// ─────────────────────────────────────────────────────────────────────────────
// demo-api — .NET 10 minimal api, демонстрация паттерней P2/P3 (task-04):
//   • JWT bearer-валидация против realm rulink (Authority + JWKS, контракт §7)
//   • роли realm_access.roles → [Authorize(Roles = …)] (RealmRolesTransformer)
//   • групповой ресурс по клейму groups (task-06)
// Конфигурация: appsettings.json + env (IDENTITY__AUTHORITY, IDENTITY__AUDIENCE,
// GROUPDATA__ALLOWEDGROUPS). Секретов нет — это resource-server, только валидация.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

var identity = builder.Configuration.GetSection("Identity");
var authority = identity["Authority"]
    ?? throw new InvalidOperationException("Identity:Authority не задан (appsettings/env IDENTITY__AUTHORITY)");
var audience = identity["Audience"] ?? "demo-api";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = authority;
        o.Audience = audience;
        // http-authority только в dev (KC на 127.0.0.1 без TLS); для https-issuer требуем https metadata
        var requireHttps = bool.TryParse(identity["RequireHttpsMetadata"], out var v) ? v
            : Uri.TryCreate(authority, UriKind.Absolute, out var u) && u.Scheme != "https" ? false : true;
        o.RequireHttpsMetadata = requireHttps;
        // оригинальные имена клеймов (organization, groups, scope, realm_access) — без маппинга в ClaimTypes.*
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new()
        {
            NameClaimType = "preferred_username",       // User.Identity.Name = email (username = email)
            ValidateAudience = bool.TryParse(identity["ValidateAudience"], out var va) ? va : true,
            ValidAudience = audience,
            // для realm Keycloak issuer всегда равен Authority (контракт: iss = …/realms/rulink);
            // явный ValidIssuer не зависит от discovery и не мешает проверке по метаданным
            ValidIssuer = authority,
        };
    });

builder.Services.AddAuthorization();

// realm_access.roles → ClaimTypes.Role: работает [Authorize(Roles = "…")] (контракт §7)
builder.Services.AddSingleton<IClaimsTransformation, RealmRolesTransformer>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Групповой ресурс (P3): группа(ы)-владелец задаётся конфигом (по умолчанию orga-team-a —
// «рабочая пара» bob+carol из контракта §4); доступ — членство в groups-клейме.
var allowedGroups = (builder.Configuration["GroupData:AllowedGroups"] ?? "orga-team-a")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

// ── endpoints ────────────────────────────────────────────────────────────────

// анонимный: health/echo
app.MapGet("/public", () => Results.Ok(new
{
    status = "ok",
    service = "demo-api",
    timeUtc = DateTimeOffset.UtcNow,
    identityAuthority = authority,
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

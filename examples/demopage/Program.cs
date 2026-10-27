using System.Security.Claims;
using DemoApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

// ─────────────────────────────────────────────────────────────────────────────
// demopage — .NET 10 Razor Pages, демонстрация OIDC-логина через IdentityService
// (Keycloak realm rulink, задание 2 task-04):
//   • /Index — [AllowAnonymous];
//   • /Private — [Authorize]: редирект на IdP, после логина — клеймы + вызов demo-api.
// Prod: Identity:Authority = https://id.rulink.io/realms/rulink (appsettings.json).
// Dev:  appsettings.Development.json → http://127.0.0.1:10020/realms/rulink.
// Клиент — public `demo-api` (standard flow + PKCE, секрет не нужен); redirect URI
// сайта должен входить в redirectUris клиента Keycloak — см. README, раздел «Артефакты».
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

var identity = builder.Configuration.GetSection("Identity");
var authority = identity["Authority"]
    ?? throw new InvalidOperationException("Identity:Authority не задан (env: Identity__Authority)");

builder.Services.AddRazorPages();

builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme; // → редирект на IdP
        o.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;   // logout на IdP
    })
    .AddCookie(o =>
    {
        // после истечения cookie — снова редирект на логин IdP (access token живёт 5 мин,
        // refresh делает cookie-сессия: silent renew через OIDC при следующем входе)
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    })
    .AddOpenIdConnect(o =>
    {
        o.Authority = authority;
        o.ClientId = identity["ClientId"] ?? "demo-api";
        o.ResponseType = "code";            // authorization code flow
        o.UsePkce = true;                   // public-клиент — PKCE обязателен
        o.SaveTokens = true;                // access/id/refresh token в cookie (для вызова demo-api)
        o.GetClaimsFromUserInfoEndpoint = false; // расширенные клеймы уже в id/access token (мапперы task-02)

        // http-авторити только в dev; для https-issuer требуем https metadata
        var requireHttps = bool.TryParse(identity["RequireHttpsMetadata"], out var v) ? v
            : Uri.TryCreate(authority, UriKind.Absolute, out var u) && u.Scheme != "https" ? false : true;
        o.RequireHttpsMetadata = requireHttps;

        o.MapInboundClaims = false;         // оригинальные имена клеймов: organization, groups, scope…
        o.TokenValidationParameters = new()
        {
            NameClaimType = "preferred_username", // username = email (контракт §3)
            ValidIssuer = authority,
        };

        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("profile");             // name, given_name, family_name
        o.Scope.Add("email");               // email, email_verified
        // группы/организация/services — дефолтные client scope realm'а (task-02), отдельно запрашивать не нужно
    });

builder.Services.AddAuthorization();

// realm_access.roles → ClaimTypes.Role (паттерн контракта §7, тот же что в demo-api)
builder.Services.AddSingleton<IClaimsTransformation, RealmRolesTransformer>();

// вызов demo-api с access token пользователя
builder.Services.AddHttpClient("demo-api", (sp, client) =>
{
    client.BaseAddress = new Uri(builder.Configuration["Identity:DemoApi"] ?? "http://127.0.0.1:10010");
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();

/// <summary>Для тестов (WebApplicationFactory).</summary>
public partial class Program;

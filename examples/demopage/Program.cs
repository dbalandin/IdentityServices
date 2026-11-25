using IdentityService;

// ─────────────────────────────────────────────────────────────────────────────
// demopage — .NET 10 Razor Pages, демонстрация OIDC-логина через IdentityService
// (Keycloak realm rulink, задание 2 task-04):
//   • /Index — [AllowAnonymous];
//   • /Private — [Authorize]: редирект на IdP, после логина — клеймы + вызов demo-api;
//   • /Account/Logout — выход (эндпоинт библиотеки MapRulinkAccount): cookie + сессия Keycloak.
// Собственной реализации аутентификации здесь нет: схемы (cookie + OIDC, code + PKCE), диспетчер
// «[Authorize] без сессии → редирект на логин», роли и хелперы клеймов — из библиотеки IdentityService.
// Конфигурация — секция Identity (appsettings.json / appsettings.Development.json / env IDENTITY__*):
//   Authority, ClientId (public-клиент demo-api, секрет не нужен), Mode = Cookie (чистый UI, JWT не принимается).
// Prod: Identity:Authority = https://idp.rulink.io/realms/rulink (appsettings.json).
// Dev:  appsettings.Development.json → http://127.0.0.1:10020/realms/rulink.
// Redirect URI сайта должен входить в redirectUris клиента Keycloak — см. README, раздел «Артефакты».
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

// Аутентификация Keycloak — библиотека. Ошибки конфигурации (нет Authority/ClientId и т.п.)
// падают здесь, при старте. Pipeline: UseAuthentication → UseAuthorization (ниже).
builder.Services.AddRulinkIdentityService(builder.Configuration);

builder.Services.AddAuthorization();

// вызов demo-api с access token пользователя (адрес — appsettings/env Identity:DemoApi)
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

// GET/POST /Account/Logout: удаление cookie + завершение сессии в Keycloak (end_session_endpoint),
// возврат на «/» (должен входить в Valid post logout redirect URIs клиента).
app.MapRulinkAccount();

app.MapRazorPages();

app.Run();

/// <summary>Для тестов (WebApplicationFactory).</summary>
public partial class Program;

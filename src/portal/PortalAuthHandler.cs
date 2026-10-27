using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Portal;

/// <summary>
/// Диспетчер схем аутентификации портала:
///   • /api/*          → Bearer-JWT (challenge — 401 от JwtBearer, без OIDC-редиректа);
///   • остальные пути  → cookie-сессия (challenge — OIDC-логин на Keycloak);
///   • Forbid          → схема по типу запроса (403).
/// Заменяет PolicyScheme: в .NET 10 у PolicySchemeOptions нет селектора для Challenge
/// (только строка ForwardChallenge), а нам нужны разные схемы challenge для /api и UI.
/// </summary>
public sealed class PortalAuthHandler(IAuthenticationHandlerProvider handlers) : IAuthenticationHandler
{
    public const string SchemeName = "PortalAuth";

    private HttpContext _context = default!;

    private static string Target(HttpContext ctx, bool challenge) =>
        ctx.Request.Path.StartsWithSegments("/api")
            ? JwtBearerDefaults.AuthenticationScheme
            : challenge
                ? OpenIdConnectDefaults.AuthenticationScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;

    public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context)
    {
        _context = context;
        return Task.CompletedTask;
    }

    private async Task<IAuthenticationHandler> GetTargetAsync(bool challenge)
        => await handlers.GetHandlerAsync(_context, Target(_context, challenge))
            ?? throw new InvalidOperationException($"схема {Target(_context, challenge)} не зарегистрирована");

    public async Task<AuthenticateResult> AuthenticateAsync()
        => await (await GetTargetAsync(challenge: false)).AuthenticateAsync();

    public async Task ChallengeAsync(AuthenticationProperties? properties)
        => await (await GetTargetAsync(challenge: true)).ChallengeAsync(properties);

    public async Task ForbidAsync(AuthenticationProperties? properties)
        => await (await GetTargetAsync(challenge: false)).ForbidAsync(properties);
}

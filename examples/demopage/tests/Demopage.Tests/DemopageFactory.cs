using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;

namespace Demopage.Tests;

/// <summary>
/// demopage в TestServer'e с аутентификацией библиотеки IdentityService, подключённой в Program.cs
/// как в реальном приложении (режим Cookie из appsettings.json). Вместо Keycloak и demo-api — моки:
/// discovery-документ обработчик OIDC получает из <see cref="MockOidcAuthority"/> (backchannel),
/// именованный клиент "demo-api" ходит в <see cref="MockDemoApi"/>.
/// </summary>
public class DemopageFactory : WebApplicationFactory<Program>
{
    /// <summary>Access token, который тест кладёт в cookie-сессию (как OIDC при SaveTokens).</summary>
    public const string SessionAccessToken = "test-access-token";

    public MockOidcAuthority Authority { get; } = new();
    public MockDemoApi DemoApi { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development: без UseExceptionHandler — исключения видны в тесте
        builder.UseEnvironment("Development");

        // перекрывают appsettings*.json (Mode = Cookie и ClientId остаются из appsettings.json)
        builder.UseSetting("Identity:Authority", MockOidcAuthority.AuthorityUrl);
        builder.UseSetting("Identity:RequireHttpsMetadata", "false");
        builder.UseSetting("Identity:DemoApi", MockDemoApi.BaseUrl);

        builder.ConfigureServices(services =>
        {
            // HttpDocumentRetriever с RequireHttps=false — мок-адрес http
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    MockOidcAuthority.AuthorityUrl + "/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever(new HttpClient(Authority)) { RequireHttps = false });
                // code flow: обмен кода на токены идёт через backchannel — ведём его в мок (token endpoint)
                o.Backchannel = new HttpClient(Authority);
            });

            // та же именованная регистрация, что в Program.cs — заменяем только транспорт
            services.AddHttpClient("demo-api").ConfigurePrimaryHttpMessageHandler(() => DemoApi);

            // cookie-сессия тестового пользователя без OIDC-колбэка
            services.AddSingleton<IStartupFilter>(new TestSignInFilter(SessionAccessToken));
        });
    }

    /// <summary>
    /// Cookie-сессия тестового пользователя — как после OIDC-логина: клеймы ID-токена (контракт §5.1)
    /// и токены (access/id), сохранённые в сессии при SaveTokens=true.
    /// </summary>
    public async Task<string> SignInCookieAsync(string user = "alice")
        => await SignInCookieAsync(user, withTokens: true);

    /// <summary>Сессия без сохранённых токенов (как при SaveTokens=false): страница не сможет вызвать demo-api.</summary>
    public async Task<string> SignInWithoutTokensAsync(string user = "alice")
        => await SignInCookieAsync(user, withTokens: false);

    private async Task<string> SignInCookieAsync(string user, bool withTokens)
    {
        using var client = CreateClientNoRedirect();
        using var resp = await client.GetAsync("/test/signin?as=" + user + (withTokens ? "" : "&tokens=none"));
        resp.EnsureSuccessStatusCode();
        return Assert.Single(resp.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }

    /// <summary>
    /// Клиент без следования редиректам и без cookie-хранилища. Базовый <c>CreateClient()</c> WebApplicationFactory
    /// по умолчанию следует 302 — и шлёт Location (URL логина Keycloak) обратно в TestServer, где такого маршрута
    /// нет (404); для проверок «302 на IdP» это мешает. Cookie передаются явно (<see cref="WithCookie"/>).
    /// </summary>
    public HttpClient CreateClientNoRedirect()
        => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    /// <summary>
    /// Настоящий OIDC-логин (authorization code + PKCE) через мок-Keycloak — без браузера:
    /// GET <paramref name="path"/> без сессии → 302 на авторизацию (state, nonce, correlation- и nonce-cookie)
    /// → колбэк <c>/signin-oidc?code&amp;state</c> с этими cookie → token endpoint мока (пара токенов —
    /// <paramref name="tokens"/>, nonce из запроса авторизации) → cookie-сессия.
    /// Так сайт работает с реальным Keycloak: principal строит OIDC-обработчик библиотеки.
    /// </summary>
    /// <returns>Заголовок Cookie сессии, как его прислал бы браузер.</returns>
    public async Task<string> SignInWithAuthorizationCodeAsync(
        Func<string, (string IdToken, string AccessToken)> tokens, string path = "/Private")
    {
        using var client = CreateClientNoRedirect();
        using var challenge = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var location = challenge.Headers.Location!;
        Assert.StartsWith(MockOidcAuthority.AuthorizationEndpoint, location.ToString());
        var query = HttpUtility.ParseQueryString(location.Query);
        var challengeCookies = CookieHeader(challenge);

        Authority.CodeGrant = _ => tokens(query["nonce"]!);

        using var callback = await client.SendAsync(WithCookie(
            new HttpRequestMessage(HttpMethod.Get, $"/signin-oidc?code=test-auth-code&state={Uri.EscapeDataString(query["state"]!)}"),
            challengeCookies));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode); // сессия создана, возврат на исходную страницу
        Assert.Equal(path, callback.Headers.Location!.ToString());

        return CookieHeader(callback);
    }

    private static string CookieHeader(HttpResponseMessage response)
        => string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

    /// <summary>
    /// Фабрика с окружением Production — как на стенде (deploy/idp-test-appweb): в demopage включён
    /// UseExceptionHandler("/Error"), appsettings.Development.json не читается. Моки те же.
    /// </summary>
    public static DemopageFactory CreateProduction() => new ProductionFactory();

    private sealed class ProductionFactory : DemopageFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production"); // после base: перекрывает Development
        }
    }

    public static HttpRequestMessage WithCookie(HttpRequestMessage request, string cookie)
    {
        request.Headers.Add("Cookie", cookie);
        return request;
    }
}

/// <summary>
/// Тестовый вход: `/test/signin?as=&lt;user&gt;` создаёт cookie-сессию штатным SignInAsync
/// (через диспетчер схем библиотеки) — без браузерного OIDC-потока.
/// Промежуточное ПО добавляется перед конвейером приложения (IStartupFilter), код demopage не меняет.
/// </summary>
public sealed class TestSignInFilter(string accessToken) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipeline) =>
        {
            if (context.Request.Path.StartsWithSegments("/test/signin"))
            {
                var name = context.Request.Query["as"].ToString();
                var user = Users.TryGetValue(name, out var found) ? found : Users["alice"];
                var properties = new AuthenticationProperties();
                // tokens=none — сессия без токенов (SaveTokens=false)
                if (!context.Request.Query.ContainsKey("tokens"))
                {
                    properties.StoreTokens(
                    [
                        new AuthenticationToken { Name = "access_token", Value = accessToken },
                        new AuthenticationToken { Name = "id_token", Value = "test-id-token" },
                    ]);
                }

                await context.SignInAsync(TestPrincipals.FromIdToken(user), properties);
                return;
            }

            await pipeline();
        });
        next(app);
    };

    private static Dictionary<string, SeedUser> Users => SeedUser.Users;
}

public static class TestPrincipals
{
    /// <summary>Principal с составом клеймов ID-токена (так строит его OIDC-обработчик при логине).</summary>
    public static ClaimsPrincipal FromIdToken(SeedUser user)
    {
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new("preferred_username", user.Email),
            new("email", user.Email),
            new("email_verified", "true"),
            new("name", user.Email),
            new("organization", user.Organization),
            new("services", user.Services),
            new("realm_access", JsonSerializer.Serialize(new { roles = user.Roles })),
        };
        claims.AddRange(user.Groups.Select(g => new Claim("groups", g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc", "preferred_username", ClaimTypes.Role));
    }
}

/// <summary>Демо-пользователь контракта jwt-claims.md §4: email, организация, роли, группы, сервисы.</summary>
public sealed record SeedUser(
    string Email,
    string Organization,
    string[] Roles,
    string[] Groups,
    string Services)
{
    public static readonly Dictionary<string, SeedUser> Users = new()
    {
        ["alice"] = new SeedUser(
            "alice@orga.example.com", "orga", ["user", "admin"], ["orga-managers"],
            "cryptoservice cadastralservice"),
        ["bob"] = new SeedUser(
            "bob@orga.example.com", "orga", ["user"], ["orga-team-a"],
            "cryptoservice"),
    };
}

using System.Net;
using System.Web;
using Xunit;

namespace Demopage.Tests;

/// <summary>
/// Настоящий OIDC-вход на сайт (authorization code + PKCE): колбэк <c>/signin-oidc</c> обрабатывает
/// OIDC-обработчик библиотеки, обменивая код в token endpoint мока. Так сайт работает с реальным
/// Keycloak — в отличие от сессии, созданной напрямую (<see cref="TestSignInFilter"/>), где состав
/// клеймов задаёт тест.
/// <para>
/// У Keycloak по умолчанию organization, services и realm_access есть только в access-токене
/// (у мапперов realm выключен id.token.claim). Обработчик OIDC строит principal из ID-токена,
/// поэтому без дополнения библиотеки на /Private эти строки пустые — при том что /me (access token)
/// показывает всё. Библиотека довносит недостающие клеймы контракта из access-токена.
/// </para>
/// </summary>
public sealed class OidcCallbackTests(DemopageFactory factory) : IClassFixture<DemopageFactory>
{
    // значения seeded-пользователя carol (контракт jwt-claims.md §4) — их нет в ответе MockDemoApi,
    // поэтому проверки по HTML однозначно относятся к таблице клеймов, а не к блоку «вызов demo-api»
    private const string Email = "carol@orga.example.com";
    private static readonly string[] Groups = ["orgb-team-b"];
    private static readonly string[] Roles = ["user", "auditor", "default-roles-rulink", "offline_access", "uma_authorization"];

    /// <summary>Access-токен carol по контракту (§2–3): всё, что Keycloak кладёт в access-токен.</summary>
    private static Dictionary<string, object> AccessTokenClaims(string sub) => new()
    {
        ["sub"] = sub,
        ["azp"] = "demo-api",
        ["email"] = Email,
        ["email_verified"] = true,
        ["preferred_username"] = Email,
        ["name"] = "Carol Carlson",
        ["organization"] = "orgb",
        ["groups"] = Groups,
        ["realm_access"] = new { roles = Roles },
        ["resource_access"] = new Dictionary<string, object> { ["account"] = new { roles = new[] { "manage-account" } } },
        ["scope"] = "openid email profile",
        ["services"] = "cadastralanalyticservice",
    };

    private static async Task<string> HtmlAsync(HttpResponseMessage resp)
        => WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());

    [Fact]
    public async Task Private_After_Real_Oidc_Login_Shows_Claims_Missing_From_Id_Token()
    {
        // ID-токен — как у Keycloak без id.token.claim у мапперов (профиль + groups); access-токен — полный контракт
        var sub = Guid.NewGuid().ToString();
        var accessToken = factory.Authority.IssueToken(AccessTokenClaims(sub));
        var cookie = await factory.SignInWithAuthorizationCodeAsync(nonce =>
            (factory.Authority.IssueToken(MockOidcAuthority.IdTokenClaims(Email, nonce, sub, Groups)), accessToken));

        using var client = factory.CreateClientNoRedirect();
        using var resp = await client.SendAsync(DemopageFactory.WithCookie(new HttpRequestMessage(HttpMethod.Get, "/Private"), cookie));
        var html = await HtmlAsync(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // из ID-токена
        Assert.Contains(Email, html);
        Assert.Contains("<td>groups</td><td><code>orgb-team-b</code></td>", html);
        // из access-токена (в ID-токене не было)
        Assert.Contains("<td>organization</td><td><code>orgb</code></td>", html);
        Assert.Contains("<td>realm_access.roles</td><td><code>user, auditor, default-roles-rulink, offline_access, uma_authorization</code></td>", html);
        Assert.Contains("<td>services (сервисы)</td><td><code>cadastralanalyticservice</code></td>", html);
        // ни одна строка таблицы не пустая
        Assert.DoesNotContain("<code></code>", html[..html.IndexOf("<h2>", StringComparison.Ordinal)]);

        // demo-api вызван с access token, полученным при входе
        Assert.Equal("Bearer " + accessToken, factory.DemoApi.LastAuthorization);
        Assert.Contains("GET /me", html);
    }

    [Fact]
    public async Task Code_Flow_Redeems_Code_With_Pkce_For_Client_Demo_Api()
    {
        var sub = Guid.NewGuid().ToString();
        await factory.SignInWithAuthorizationCodeAsync(nonce =>
            (factory.Authority.IssueToken(MockOidcAuthority.IdTokenClaims(Email, nonce, sub, Groups)),
                factory.Authority.IssueToken(AccessTokenClaims(sub))));

        var request = factory.Authority.LastTokenRequest!;
        Assert.Equal("authorization_code", request["grant_type"]);
        Assert.Equal("demo-api", request["client_id"]);
        Assert.Equal("test-auth-code", request["code"]);
        Assert.Equal("http://localhost/signin-oidc", request["redirect_uri"]);
        Assert.InRange(request["code_verifier"].Length, 43, 128); // PKCE — public-клиент
        Assert.False(request.ContainsKey("client_secret"));      // секрет не нужен
    }

    [Fact]
    public async Task Index_After_Real_Oidc_Login_Shows_User_In_Header()
    {
        var sub = Guid.NewGuid().ToString();
        var cookie = await factory.SignInWithAuthorizationCodeAsync(nonce =>
            (factory.Authority.IssueToken(MockOidcAuthority.IdTokenClaims(Email, nonce, sub, Groups)),
                factory.Authority.IssueToken(AccessTokenClaims(sub))));

        using var resp = await factory.CreateClientNoRedirect().SendAsync(DemopageFactory.WithCookie(new HttpRequestMessage(HttpMethod.Get, "/Index"), cookie));
        var html = await HtmlAsync(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains(Email, html);                       // имя в шапке — preferred_username из ID-токена
        Assert.Contains("action=\"/Account/Logout\"", html);
    }

    [Fact]
    public async Task Real_Oidc_Login_Works_In_Production_Environment()
    {
        // стенд (deploy/idp-test-appweb): ASPNETCORE_ENVIRONMENT=Production, включён UseExceptionHandler
        using var production = DemopageFactory.CreateProduction();
        var sub = Guid.NewGuid().ToString();
        var cookie = await production.SignInWithAuthorizationCodeAsync(nonce =>
            (production.Authority.IssueToken(MockOidcAuthority.IdTokenClaims(Email, nonce, sub, Groups)),
                production.Authority.IssueToken(AccessTokenClaims(sub))));

        using var resp = await production.CreateClientNoRedirect().SendAsync(DemopageFactory.WithCookie(new HttpRequestMessage(HttpMethod.Get, "/Private"), cookie));
        var html = await HtmlAsync(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("<td>organization</td><td><code>orgb</code></td>", html);
        Assert.Contains("<td>services (сервисы)</td><td><code>cadastralanalyticservice</code></td>", html);
    }
}

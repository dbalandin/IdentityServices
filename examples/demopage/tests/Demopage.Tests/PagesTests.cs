using System.Net;
using System.Web;
using Xunit;

namespace Demopage.Tests;

/// <summary>
/// Тесты demopage на TestServer (без Keycloak и demo-api): аутентификация подключена библиотекой
/// IdentityService в Program.cs, внешние сервисы заменены моками (см. <see cref="DemopageFactory"/>).
/// Проверяют разметку приложения поверх библиотеки: публичная/защищённая страницы,
/// редирект на логин, выход через `/Account/Logout`, вызов demo-api с access token из сессии.
/// </summary>
public sealed class PagesTests(DemopageFactory factory) : IClassFixture<DemopageFactory>
{
    private const string IdpAuthEndpoint = MockOidcAuthority.AuthorizationEndpoint;
    private const string IdpLogoutEndpoint = MockOidcAuthority.EndSessionEndpoint;

    /// <summary>HTML страницы с раскрытыми сущностями: Razor кодирует не-Latin1 символы в выражениях (@Model…).</summary>
    private static async Task<string> HtmlAsync(HttpResponseMessage resp)
        => WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());

    // ── публичная страница ─────────────────────────────────────────────────────

    [Fact]
    public async Task Index_Is_Anonymous()
    {
        using var client = factory.CreateClientNoRedirect();
        using var resp = await client.GetAsync("/Index");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Публичная страница", await HtmlAsync(resp));
    }

    [Fact]
    public async Task Root_Serves_Index_Page()
    {
        // Razor Pages: Index.cshtml — страница по умолчанию для «/» (healthcheck контейнера ждёт 200)
        using var client = factory.CreateClientNoRedirect();
        using var resp = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Публичная страница", await HtmlAsync(resp));
    }

    // ── защищённая страница без сессии ─────────────────────────────────────────

    [Fact]
    public async Task Private_Without_Session_Redirects_To_Idp_Login()
    {
        using var resp = await factory.CreateClientNoRedirect().GetAsync("/Private");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = resp.Headers.Location!;
        Assert.StartsWith(IdpAuthEndpoint, location.ToString()); // сразу Keycloak, без промежуточной страницы логина

        var query = HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("demo-api", query["client_id"]);
        Assert.Equal("http://localhost/signin-oidc", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]); // PKCE — public-клиент
        Assert.Equal("openid profile email", query["scope"]);
    }

    [Fact]
    public async Task Private_Ignores_Bearer_Header_In_Cookie_Mode()
    {
        // Mode = Cookie (appsettings.json): сайт — чистый UI, JWT не принимает — даже с заголовком Bearer логин через IdP
        using var client = factory.CreateClientNoRedirect();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-jwt");

        using var resp = await client.GetAsync("/Private");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode); // 302, а не 401
        Assert.StartsWith(IdpAuthEndpoint, resp.Headers.Location!.ToString());
    }

    // ── защищённая страница с cookie-сессией ───────────────────────────────────

    [Fact]
    public async Task Private_With_Session_Shows_Claims_And_Calls_DemoApi_With_Token()
    {
        var cookie = await factory.SignInCookieAsync("alice");

        using var client = factory.CreateClientNoRedirect();
        using var resp = await client.SendAsync(DemopageFactory.WithCookie(new HttpRequestMessage(HttpMethod.Get, "/Private"), cookie));
        var html = await HtmlAsync(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // клеймы ID-токена (контракт §5.1) — через хелперы библиотеки
        Assert.Contains("alice@orga.example.com", html);           // preferred_username, email
        Assert.Contains("orga-managers", html);                    // groups
        Assert.Contains("user, admin", html);                      // realm_access.roles
        Assert.Contains("cryptoservice", html);                    // services

        // вызов demo-api /me с access token из сессии
        Assert.Equal("Bearer " + DemopageFactory.SessionAccessToken, factory.DemoApi.LastAuthorization);
        Assert.Contains("GET /me", html);
        Assert.Contains("HTTP 200", html);
        Assert.Contains("cadastralservice", html);                 // ответ demo-api отрендерен
    }

    [Fact]
    public async Task Private_Without_Token_In_Session_Reports_It()
    {
        // SaveTokens=false у клиента Keycloak: сессия есть, access token для demo-api — нет
        var cookie = await factory.SignInWithoutTokensAsync("bob");

        using var resp = await factory.CreateClientNoRedirect().SendAsync(DemopageFactory.WithCookie(new HttpRequestMessage(HttpMethod.Get, "/Private"), cookie));
        var html = await HtmlAsync(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("bob@orga.example.com", html);
        Assert.Contains("access_token отсутствует в сессии", html);
        Assert.Contains("orga-team-a", html);
    }

    // ── выход: эндпоинт библиотеки MapRulinkAccount ─────────────────────────────

    [Fact]
    public async Task Logout_Ends_Idp_Session_And_Clears_Cookie()
    {
        var cookie = await factory.SignInCookieAsync("alice");

        using var client = factory.CreateClientNoRedirect();
        using var resp = await client.SendAsync(DemopageFactory.WithCookie(new HttpRequestMessage(HttpMethod.Get, "/Account/Logout"), cookie));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = resp.Headers.Location!.ToString();
        Assert.StartsWith(IdpLogoutEndpoint, location); // сессия завершается на стороне Keycloak
        Assert.Contains("post_logout_redirect_uri=http%3A%2F%2Flocalhost%2F", location); // возврат на «/»
        Assert.Contains("client_id=demo-api", location);
        Assert.Contains("id_token_hint=test-id-token", location); // из сессии (SaveTokens)

        var setCookie = Assert.Single(resp.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("rulink_idp=", setCookie);   // cookie сессии библиотеки
        Assert.Contains("expires=", setCookie);        // удаляется
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Logout_Accepts_Get_And_Post(string method)
    {
        using var resp = await factory.CreateClientNoRedirect().SendAsync(new HttpRequestMessage(new HttpMethod(method), "/Account/Logout"));

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith(IdpLogoutEndpoint, resp.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Layout_Logout_Form_Targets_Account_Logout()
    {
        var cookie = await factory.SignInCookieAsync("alice");

        using var client = factory.CreateClientNoRedirect();
        using var resp = await client.SendAsync(DemopageFactory.WithCookie(new HttpRequestMessage(HttpMethod.Get, "/Index"), cookie));
        var html = await HtmlAsync(resp);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("alice@orga.example.com", html);                               // имя пользователя в шапке
        Assert.Contains("action=\"/Account/Logout\"", html);                           // выход — POST-формой на эндпоинт библиотеки
    }

    [Fact]
    public async Task Old_Logout_Page_Is_Gone()
    {
        // страница /Logout с собственной реализацией выхода удалена — выход только через библиотеку
        using var resp = await factory.CreateClientNoRedirect().GetAsync("/Logout");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── состав библиотеки ──────────────────────────────────────────────────────

    [Fact]
    public async Task Session_Cookie_Is_Signed_By_Library()
    {
        using var resp = await factory.CreateClientNoRedirect().GetAsync("/test/signin?as=alice");

        var setCookie = Assert.Single(resp.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("rulink_idp=", setCookie);        // CookieName библиотеки (appsettings его не переопределяет)
        Assert.Contains("httponly", setCookie);
        Assert.Contains("samesite=" , setCookie);
    }
}

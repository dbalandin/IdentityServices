using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// Безопасность realm (task-08, план §6/§7):
///   • 5 неверных паролей → блокировка (правильный пароль тоже отклоняется, пока действует lockout);
///   • неверный логин и несуществующий логин — неотличимы (одинаковая ошибка, близкий тайминг);
///   • forgot-password не раскрывает существование username.
/// </summary>
public sealed class SecurityTests : IClassFixture<OidcClient>
{
    private readonly OidcClient _oidc;

    public SecurityTests(OidcClient oidc) => _oidc = oidc;

    [LiveSecurityFact]
    public async Task Five_Bad_Passwords_Temporarily_Lock_Account()
    {
        // жертва — свежий пользователь (не портим seeded): создаём через портал, если он поднят;
        // иначе — bob (lockout временный, но тесты после него для bob сломаются — поэтому портал обязателен)
        if (!TestEnv.IsReachable(TestEnv.PortalBase) || TestEnv.DemoPassword is null)
        {
            Assert.Fail("нужен портал (PORTAL_BASE_URL) для создания временного пользователя");
            return;
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var email = $"lock-{stamp}@orga.example.com";
        var password = $"Lock-Me-{stamp}";
        using (var portal = new HttpClient { BaseAddress = new Uri(TestEnv.PortalBase) })
        {
            var alice = await _oidc.PasswordGrantAsync(SeedUser.All["alice"].Email, TestEnv.DemoPassword!);
            Assert.True(alice.HasToken, alice.Error);
            portal.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.AccessToken);
            using var created = await portal.PostAsJsonAsync("/api/users", new
            {
                email,
                role = "user",
                groups = Array.Empty<string>(),
                services = Array.Empty<string>(),
                password,
                temporaryPassword = false,
            });
            Assert.True(created.StatusCode == HttpStatusCode.Created,
                $"не удалось создать пользователя для теста: {created.StatusCode}");
        }

        // 5 неверных паролей подряд (brute force: failureFactor=5, task-02)
        for (var i = 1; i <= 5; i++)
        {
            var bad = await _oidc.PasswordGrantAsync(email, "wrong-password-0");
            Assert.False(bad.HasToken, $"попытка {i}: не ожидали токен");
        }

        // правильный пароль тоже отклонён — временная блокировка действует (waitIncrement=60с)
        var good = await _oidc.PasswordGrantAsync(email, password);
        Assert.False(good.HasToken, "после 5 неудач правильный пароль должен быть отклонён (lockout)");
        Assert.Equal("invalid_grant", good.Error);
    }

    [LiveSecurityFact]
    public async Task Wrong_Password_And_Unknown_User_Are_Indistinguishable()
    {
        Assert.NotNull(TestEnv.DemoPassword);
        var swWrong = Stopwatch.StartNew();
        var wrong = await _oidc.PasswordGrantAsync(SeedUser.All["bob"].Email, "definitely-wrong-pass");
        swWrong.Stop();

        var swUnknown = Stopwatch.StartNew();
        var unknown = await _oidc.PasswordGrantAsync($"nobody-{Guid.NewGuid():N}@nowhere.example", "definitely-wrong-pass");
        swUnknown.Stop();

        // одна и та же ошибка (единый ответ KC — не раскрывает существование username, план §6)
        Assert.Equal("invalid_grant", wrong.Error);
        Assert.Equal("invalid_grant", unknown.Error);
        Assert.False(wrong.HasToken);
        Assert.False(unknown.HasToken);

        // тайминг «в пределах разумного»: расхождение < 3 с
        var diff = Math.Abs(swWrong.ElapsedMilliseconds - swUnknown.ElapsedMilliseconds);
        Assert.True(diff < 3000, $"тайминг выдаёт существование username: {swWrong.ElapsedMilliseconds}ms vs {swUnknown.ElapsedMilliseconds}ms");
    }

    [LiveSecurityFact]
    public async Task Forgot_Password_Does_Not_Reveal_Username_Existence()
    {
        // форма forgot-password: /protocol/openid-connect/auth?... → логин-страница KC →
        // POST login-actions/reset-credentials с username; ответ не должен зависеть от существования
        using var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true,
        })
        { BaseAddress = new Uri(TestEnv.IdentityBase) };

        async Task<(HttpStatusCode status, Uri? location)> ResetFlowAsync(string username)
        {
            // 1) открыть логин-страницу (client_id=demo-api, стандартный auth-request)
            var authUrl = $"{TestEnv.RealmBase}/protocol/openid-connect/auth" +
                          "?client_id=demo-api&response_type=code&scope=openid" +
                          "&redirect_uri=http%3A%2F%2F127.0.0.1%3A10010%2Fcallback";
            using var page = await http.GetAsync(authUrl);
            if (page.StatusCode != HttpStatusCode.OK)
                return (page.StatusCode, null);

            // action формы reset-credentials: kc формирует ссылку «Forgot Password?»
            var html = await page.Content.ReadAsStringAsync();
            var actionMatch = System.Text.RegularExpressions.Regex.Match(
                html, "action=\"([^\"]*login-actions/reset-credentials[^\"]*)\"");
            if (!actionMatch.Success)
                return (HttpStatusCode.OK, null); // forgot-password выключен — не раскрывает ничего

            var action = actionMatch.Groups[1].Value.Replace("&amp;", "&");
            // 2) отправить username
            using var resp = await http.PostAsync(action, new FormUrlEncodedContent(
                new Dictionary<string, string> { ["username"] = username }));
            return (resp.StatusCode, resp.Headers.Location);
        }

        var existing = await ResetFlowAsync(SeedUser.All["alice"].Email);
        var ghost = await ResetFlowAsync($"ghost-{Guid.NewGuid():N}@nowhere.example");

        // одинаковый исход (не раскрывает существование): статус и редирект совпадают
        Assert.Equal(existing.status, ghost.status);
        Assert.Equal(
            existing.location?.GetLeftPart(UriPartial.Path),
            ghost.location?.GetLeftPart(UriPartial.Path));
    }
}

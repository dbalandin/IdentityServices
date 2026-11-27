using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Demopage.Tests;

/// <summary>
/// Мок OIDC-authority realm `rulink` (Keycloak) без docker: отдаёт discovery-документ и JWKS,
/// отвечает на token endpoint (обмен кода в code flow — пара токенов по рецепту теста) и выпускает
/// RS256-токены с клеймами Keycloak. Обработчик OIDC (библиотека IdentityService) запрашивает
/// метаданные и токены по backchannel через этот же хендлер.
/// Сессию можно создать и напрямую — cookie тестового пользователя (см. <see cref="TestSignInFilter"/>).
/// </summary>
public sealed class MockOidcAuthority : HttpMessageHandler
{
    public const string AuthorityUrl = "http://mock-idp/realms/rulink";
    public const string AuthorizationEndpoint = AuthorityUrl + "/protocol/openid-connect/auth";
    public const string EndSessionEndpoint = AuthorityUrl + "/protocol/openid-connect/logout";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly RSA _rsa = RSA.Create(2048);

    /// <summary>Трассировка запросов к моку (для отладки тестов).</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>
    /// Ответ token endpoint на <c>grant_type=authorization_code</c>: по форме запроса
    /// (client_id, code, redirect_uri, code_verifier…) возвращает пару (id_token, access_token) —
    /// их выпускает <see cref="IssueToken"/>. Тест задаёт до вызова колбэка.
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>, (string IdToken, string AccessToken)>? CodeGrant { get; set; }

    /// <summary>Последний запрос к token endpoint (параметры code flow — для проверок).</summary>
    public IReadOnlyDictionary<string, string>? LastTokenRequest { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rsa.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Выпустить токен (RS256, как Keycloak) с заданными клеймами; aud по умолчанию — клиент demo-api.
    /// </summary>
    public string IssueToken(IDictionary<string, object> claims, string? audience = "demo-api", TimeSpan? lifetime = null)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object>(claims)
        {
            ["iss"] = AuthorityUrl,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(lifetime ?? TimeSpan.FromMinutes(5)).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString(),
            ["typ"] = "Bearer",
        };
        if (audience is not null)
        {
            payload["aud"] = audience.Contains(',', StringComparison.Ordinal) ? audience.Split(',') : audience;
        }

        return new JsonWebTokenHandler().CreateToken(
            JsonSerializer.Serialize(payload, JsonOpts),
            new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "mock-kid-1" }, SecurityAlgorithms.RsaSha256));
    }

    /// <summary>
    /// Клеймы ID-токена Keycloak, когда у мапперов realm выключен id.token.claim (дефолт Keycloak):
    /// стандартные поля OIDC + профиль + groups; без organization/services/realm_access.
    /// </summary>
    public static Dictionary<string, object> IdTokenClaims(string email, string nonce, string sub, string[] groups,
        string clientId = "demo-api")
        => new()
        {
            ["sub"] = sub,
            ["aud"] = clientId,
            ["azp"] = clientId,
            ["nonce"] = nonce,
            ["session_state"] = Guid.NewGuid().ToString(),
            ["acr"] = "1",
            ["email"] = email,
            ["email_verified"] = true,
            ["name"] = email,
            ["preferred_username"] = email,
            ["groups"] = groups,
        };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue($"{request.Method} {request.RequestUri}");
        var path = request.RequestUri!.AbsolutePath;
        var resp = request.Method == HttpMethod.Post && path == "/realms/rulink/protocol/openid-connect/token"
            ? TokenResponse(ReadForm(request))
            : path switch
            {
                // discovery и JWKS уже собраны в JSON-строку — отдать как есть (не сериализовать повторно)
                "/realms/rulink/.well-known/openid-configuration" => Text(DiscoveryJson()),
                "/realms/rulink/protocol/openid-connect/certs" => Text(JwksJson()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };

        return Task.FromResult(resp);
    }

    private HttpResponseMessage TokenResponse(IReadOnlyDictionary<string, string> form)
    {
        LastTokenRequest = form;
        if (form.GetValueOrDefault("grant_type") != "authorization_code")
        {
            return Json(new { error = "unsupported_grant_type" }, HttpStatusCode.BadRequest);
        }

        if (CodeGrant is null)
        {
            return Json(new { error = "invalid_grant", error_description = "тест не задал ответ token endpoint" },
                HttpStatusCode.BadRequest);
        }

        try
        {
            var (idToken, accessToken) = CodeGrant(form);
            return Json(new { access_token = accessToken, token_type = "Bearer", expires_in = 300, id_token = idToken },
                HttpStatusCode.OK);
        }
        catch (Exception ex) // assert в рецепте теста → ошибка token endpoint, её увидит OIDC-обработчик
        {
            return Json(new { error = "invalid_grant", error_description = ex.Message }, HttpStatusCode.BadRequest);
        }
    }

    private static HttpResponseMessage Text(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Json(object body, HttpStatusCode status)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json"),
        };

    private static IReadOnlyDictionary<string, string> ReadForm(HttpRequestMessage request)
        => (request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty)
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(
                p => Uri.UnescapeDataString(p[0].Replace('+', ' ')),
                p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty);

    private static string DiscoveryJson() => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["issuer"] = AuthorityUrl,
        ["authorization_endpoint"] = AuthorizationEndpoint,
        ["token_endpoint"] = AuthorityUrl + "/protocol/openid-connect/token",
        ["jwks_uri"] = AuthorityUrl + "/protocol/openid-connect/certs",
        ["end_session_endpoint"] = EndSessionEndpoint,
    }, JsonOpts);

    private string JwksJson()
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = "mock-kid-1" };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
        return JsonSerializer.Serialize(new { keys = new object[] { jwk } }, JsonOpts);
    }
}

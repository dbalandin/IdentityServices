using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Demopage.Tests;

/// <summary>
/// Мок OIDC-authority realm `rulink` (Keycloak) без docker: отдаёт discovery-документ и JWKS.
/// Обработчик OIDC (библиотека IdentityService) запрашивает их по backchannel при первом редиректе
/// на логин и при выходе (ConfigurationManager в Microsoft.IdentityModel 10 забирает и ключи из jwks_uri).
/// Токены мок не выпускает — колбэк `/signin-oidc` в тестах не используется
/// (cookie-сессия создаётся напрямую, см. <see cref="TestSignInFilter"/>).
/// </summary>
public sealed class MockOidcAuthority : HttpMessageHandler, IDisposable
{
    public const string AuthorityUrl = "http://mock-idp/realms/rulink";
    public const string AuthorizationEndpoint = AuthorityUrl + "/protocol/openid-connect/auth";
    public const string EndSessionEndpoint = AuthorityUrl + "/protocol/openid-connect/logout";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly RSA _rsa = RSA.Create(2048);

    /// <summary>Трассировка запросов к моку (для отладки тестов).</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public void Dispose() => _rsa.Dispose();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue($"{request.Method} {request.RequestUri}");
        var body = request.RequestUri!.AbsolutePath switch
        {
            "/realms/rulink/.well-known/openid-configuration" => DiscoveryJson(),
            "/realms/rulink/protocol/openid-connect/certs" => JwksJson(),
            _ => null,
        };
        var resp = body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        return Task.FromResult(resp);
    }

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

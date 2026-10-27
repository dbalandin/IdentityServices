using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Portal.Tests;

/// <summary>
/// Мок OIDC-authority realm `rulink` для портала: discovery + JWKS + RS256-токены
/// по контракту docs/contracts/jwt-claims.md (копия паттерна DemoApi.Tests, task-04).
/// JwtBearer портала ходит за метаданными через подменённый backchannel.
/// </summary>
public sealed class MockOidcAuthority : IDisposable
{
    public const string AuthorityUrl = "http://mock-idp/realms/rulink";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public MockOidcAuthority() => _key = new RsaSecurityKey(_rsa) { KeyId = "mock-kid-1" };

    public HttpMessageHandler CreateHttpHandler() => new OidcHttpHandler(this);

    public string IssueToken(IDictionary<string, object> claims, string? audience = "demo-api",
        string issuer = AuthorityUrl, TimeSpan? lifetime = null)
    {
        var now = DateTimeOffset.UtcNow;
        lifetime ??= TimeSpan.FromMinutes(5);
        var payload = new Dictionary<string, object>(claims)
        {
            ["iss"] = issuer,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(lifetime.Value).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString(),
            ["typ"] = "Bearer",
            ["aud"] = (object?)(audience?.Contains(',') == true ? audience.Split(',') : audience) ?? "account",
        };
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(
            JsonSerializer.Serialize(payload, JsonOpts),
            new SigningCredentials(_key, SecurityAlgorithms.RsaSha256));
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public void Dispose() => _rsa.Dispose();

    private sealed class OidcHttpHandler(MockOidcAuthority owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/realms/rulink/.well-known/openid-configuration" => JsonSerializer.Serialize(
                    new Dictionary<string, object>
                    {
                        ["issuer"] = AuthorityUrl,
                        ["jwks_uri"] = AuthorityUrl + "/protocol/openid-connect/certs",
                        ["token_endpoint"] = AuthorityUrl + "/protocol/openid-connect/token",
                        ["authorization_endpoint"] = AuthorityUrl + "/protocol/openid-connect/auth",
                    }, JsonOpts),
                "/realms/rulink/protocol/openid-connect/certs" => JsonSerializer.Serialize(
                    new { keys = new object[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(owner._key) } }, JsonOpts),
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
    }
}

/// <summary>Служебные клеймы для тестового токена администратора/пользователя.</summary>
public static class TestTokens
{
    private static readonly string[] TechnicalRoles = ["default-roles-rulink", "offline_access", "uma_authorization"];

    /// <summary>Токен пользователя realm: organization, groups, realm_access.roles (+технические), scope.
    /// organization=null → клейм отсутствует (суперадмин, вне организаций).</summary>
    public static IDictionary<string, object> Build(string email, string? organization,
        string[] roles, string[] groups, string services = "")
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Guid.NewGuid().ToString(),
            ["azp"] = "demo-api",
            ["email"] = email,
            ["email_verified"] = true,
            ["preferred_username"] = email,
            ["groups"] = groups,
            ["realm_access"] = new { roles = roles.Concat(TechnicalRoles).ToArray() },
            ["scope"] = "openid email profile " + services,
        };
        if (organization is not null)
            claims["organization"] = organization;
        return claims;
    }
}

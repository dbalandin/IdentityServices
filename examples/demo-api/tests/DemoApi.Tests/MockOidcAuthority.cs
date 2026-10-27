using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DemoApi.Tests;

/// <summary>
/// Мок OIDC-authority realm `rulink`: отдаёт discovery-документ и JWKS,
/// выпускает RS256-токены с клеймами по контракту docs/contracts/jwt-claims.md.
/// Заменяет Keycloak в интеграционных тестах (docker в среде агента недоступен) —
/// JwtBearer ходит за метаданными через BackchannelHttpClient с этим хендлером.
/// </summary>
public sealed class MockOidcAuthority : IDisposable
{
    public const string AuthorityUrl = "http://mock-idp/realms/rulink";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly string _kid = "mock-kid-1";

    public MockOidcAuthority()
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = _kid };
    }

    /// <summary>Хендлер, отвечающий на discovery/JWKS запросы JwtBearer backchannel'а.</summary>
    public HttpMessageHandler CreateHttpHandler() => new OidcHttpHandler(this);

    /// <summary>Трассировка запросов, полученных моком (для отладки тестов).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Выпустить access token с произвольным набором клеймов (RS256, как Keycloak).</summary>
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
        };
        if (audience is not null)
        {
            payload["aud"] = audience.Contains(',') ? audience.Split(',') : audience;
        }

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(
            JsonSerializer.Serialize(payload, JsonOpts),
            new SigningCredentials(_key, SecurityAlgorithms.RsaSha256));
    }

    /// <summary>Токен seeded-пользователя по контракту §4 (organization/groups/roles/scope).</summary>
    public string IssueUserToken(SeedUser user, TimeSpan? lifetime = null)
        => IssueToken(user.ToClaims(), lifetime: lifetime);

    /// <summary>Seeded-пользователи контракта jwt-claims.md §4 (эталонные значения task-02).</summary>
    public static readonly Dictionary<string, SeedUser> Users = new()
    {
        ["alice"] = new SeedUser(
            "alice@orga.example.com", "orga", ["user", "admin"], ["orga-managers"],
            "cryptoservice cadastralservice cadastralanalyticservice"),
        ["bob"] = new SeedUser(
            "bob@orga.example.com", "orga", ["user"], ["orga-team-a"],
            "cryptoservice cadastralservice"),
        ["carol"] = new SeedUser(
            "carol@orga.example.com", "orga", ["user", "auditor"], ["orga-team-a"],
            "cadastralanalyticservice"),
        ["dave"] = new SeedUser(
            "dave@orgb.example.com", "orgb", ["user", "admin"], ["orgb-managers"],
            "cryptoservice cadastralservice cadastralanalyticservice"),
        ["erin"] = new SeedUser(
            "erin@orgb.example.com", "orgb", ["user"], ["orgb-team-b"],
            "cryptoservice"),
    };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public void Dispose() => _rsa.Dispose();

    // ── HTTP-часть: /.well-known/openid-configuration + JWKS ────────────────────
    private sealed class OidcHttpHandler(MockOidcAuthority owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            owner.Requests.Enqueue($"{request.Method} {request.RequestUri}");
            var body = path switch
            {
                "/realms/rulink/.well-known/openid-configuration" => DiscoveryJson(),
                "/realms/rulink/protocol/openid-connect/certs" => owner.JwksJson(),
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
            ["jwks_uri"] = AuthorityUrl + "/protocol/openid-connect/certs",
            ["token_endpoint"] = AuthorityUrl + "/protocol/openid-connect/token",
            ["authorization_endpoint"] = AuthorityUrl + "/protocol/openid-connect/auth",
        }, JsonOpts);
    }

    private string JwksJson()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
        return JsonSerializer.Serialize(new { keys = new object[] { jwk } }, JsonOpts);
    }
}

/// <summary>Seeded-пользователь контракта §4: email, организация, роли, группы, сервисы.</summary>
public sealed record SeedUser(
    string Email,
    string Organization,
    string[] Roles,
    string[] Groups,
    string Services)
{
    public IDictionary<string, object> ToClaims() => new Dictionary<string, object>
    {
        ["sub"] = Guid.NewGuid().ToString(),
        ["azp"] = "demo-api",
        ["email"] = Email,
        ["email_verified"] = true,
        ["preferred_username"] = Email,
        ["name"] = Email,
        ["organization"] = Organization,
        ["groups"] = Groups,                                   // JSON-массив, как у Keycloak
        ["realm_access"] = new { roles = Roles.Concat(TechnicalRoles).ToArray() },
        ["resource_access"] = new Dictionary<string, object>
        {
            ["account"] = new { roles = new[] { "manage-account", "view-profile" } },
        },
        ["scope"] = "openid email profile " + Services,
    };

    // технические роли Keycloak — присутствуют всегда, потребители обязаны игнорировать (§3)
    private static readonly string[] TechnicalRoles =
        ["default-roles-rulink", "offline_access", "uma_authorization"];
};

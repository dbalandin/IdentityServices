using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Xunit.Abstractions;

namespace Identity.IntegrationTests;

/// <summary>
/// Контракт JWT (task-08): токены seeded-пользователей (password grant, dev-only) сверяются
/// с эталоном docs/contracts/jwt-claims.md — правила сверки §8 (состав клеймов, значения §4,
/// подпись RS256 по JWKS realm'а).
/// </summary>
public sealed class JwtContractTests : IClassFixture<OidcClient>
{
    private readonly OidcClient _oidc;
    private readonly ITestOutputHelper _out;
    private static readonly HashSet<string> TechnicalRoles =
        ["default-roles-rulink", "offline_access", "uma_authorization"];

    // обязательный состав (§8.1): наличие, не точные значения
    private static readonly string[] RequiredClaims =
    [
        "iss", "sub", "exp", "iat", "jti", "typ", "azp", "aud",
        "email", "email_verified", "preferred_username",
        "organization", "groups", "realm_access", "scope",
    ];

    public JwtContractTests(OidcClient oidc, ITestOutputHelper output)
    {
        _oidc = oidc;
        _out = output;
    }

    [LiveIdentityFact]
    public async Task Password_Grant_Issues_Tokens()
    {
        Assert.NotNull(TestEnv.DemoPassword);
        var token = await _oidc.PasswordGrantAsync(SeedUser.All["alice"].Email, TestEnv.DemoPassword!);
        Assert.True(token.HasToken, $"нет access_token: {token.Error} {token.ErrorDescription}");
        Assert.NotNull(token.RefreshToken); // refresh выдаётся (контракт §5)
    }

    [LiveIdentityFact]
    public async Task All_Seeded_Users_Match_Contract()
    {
        Assert.NotNull(TestEnv.DemoPassword);
        foreach (var (key, seed) in SeedUser.All)
        {
            var token = await _oidc.PasswordGrantAsync(seed.Email, TestEnv.DemoPassword!);
            Assert.True(token.HasToken, $"{key}: логин не удался ({token.Error})");
            var claims = OidcClient.ParseClaims(token.AccessToken!);

            // §8.1: обязательный состав клеймов
            var missing = RequiredClaims.Where(c => !claims.ContainsKey(c)).ToList();
            Assert.True(missing.Count == 0, $"{key}: отсутствуют клеймы {string.Join(", ", missing)}");

            // §8.2: iss и username=email
            Assert.Equal($"{TestEnv.RealmBase}", claims["iss"].GetString());
            Assert.Equal(seed.Email, claims["preferred_username"].GetString());
            Assert.Equal(seed.Email, claims["email"].GetString());
            Assert.True(claims["email_verified"].GetBoolean(), $"{key}: email_verified");

            // §8.3: organization — имя организации строкой
            Assert.Equal(seed.Organization, claims["organization"].GetString());

            // §8.4: groups — массив строк без пути, значения §4
            var groups = claims["groups"].EnumerateArray().Select(g => g.GetString()).ToList();
            Assert.Equal(seed.Groups.OrderBy(g => g), groups.OrderBy(g => g));
            Assert.All(groups, g => Assert.False(g!.Contains('/'), $"{key}: группа с путём: {g}"));

            // §8.5: realm_access.roles содержит user и прикладные роли (вхождение)
            var roles = claims["realm_access"].GetProperty("roles")
                .EnumerateArray().Select(r => r.GetString()).ToList();
            foreach (var role in seed.Roles)
                Assert.Contains(role, roles);
            Assert.All(roles.Where(r => !TechnicalRoles.Contains(r!)),
                r => Assert.True(SeedUser.All.Values.SelectMany(u => u.Roles).Contains(r!) || r is "user" or "admin" or "auditor",
                    $"{key}: неожидаемая прикладная роль {r}"));

            // §8.6: scope содержит все сервисы пользователя (вхождение) и не содержит чужих
            var scopes = claims["scope"].GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var svc in seed.Services)
                Assert.Contains(svc, scopes);
            Assert.DoesNotContain(scopes, s => s.EndsWith("service") && !SeedUser.All.Values.SelectMany(u => u.Services).Contains(s));

            _out.WriteLine($"{key}: ok ({seed.Organization}, {string.Join(",", seed.Groups)})");
        }
    }

    [LiveIdentityFact]
    public async Task Signature_Is_Valid_Rs256_By_Realm_Jwks()
    {
        Assert.NotNull(TestEnv.DemoPassword);
        var token = await _oidc.PasswordGrantAsync(SeedUser.All["bob"].Email, TestEnv.DemoPassword!);
        Assert.True(token.HasToken, token.Error);

        // JWKS realm'а (контракт §1: RS256, ключ по kid)
        using var jwksHttp = new HttpClient();
        var jwksJson = await jwksHttp.GetStringAsync($"{TestEnv.RealmBase}/protocol/openid-connect/certs");
        var jwks = new JsonWebKeySet(jwksJson);
        Assert.NotEmpty(jwks.Keys);

        var handler = new JsonWebTokenHandler();
        var result = handler.ValidateToken(token.AccessToken!, new TokenValidationParameters
        {
            IssuerSigningKeys = jwks.Keys,
            ValidIssuer = TestEnv.RealmBase,
            ValidateAudience = false,          // aud опционален для потребителей (контракт §3)
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256], // только RS256
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });
        Assert.True(result.IsValid, $"подпись/iss/exp: {result.Exception?.Message}");

        // заголовок: alg RS256 и kid из JWKS (контракт §1)
        var header = ParseHeader(token.AccessToken!);
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        var kid = header.GetProperty("kid").GetString();
        Assert.Contains(jwks.Keys, k => k.Kid == kid);
    }

    private static JsonElement ParseHeader(string jwt)
    {
        var part = jwt.Split('.')[0].Replace('-', '+').Replace('_', '/');
        part = (part.Length % 4) switch { 2 => part + "==", 3 => part + "=", _ => part };
        return JsonDocument.Parse(Convert.FromBase64String(part)).RootElement.Clone();
    }
}

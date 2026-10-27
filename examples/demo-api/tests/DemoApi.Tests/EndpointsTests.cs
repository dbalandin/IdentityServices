using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DemoApi.Tests;

/// <summary>
/// Интеграционные тесты demo-api против мок OIDC-authority (контракт jwt-claims.md):
/// сценарии DoD task-04 — аноним 200, защищённые без токена 401, роли 200/403,
/// групповой ресурс: члены группы 200, остальные 403, валидация aud/iss/exp.
/// </summary>
public sealed class EndpointsTests(DemoApiFactory factory) : IClassFixture<DemoApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(
            await resp.Content.ReadAsStringAsync(), Json);
    }

    private string Token(string user, TimeSpan? lifetime = null)
        => factory.Authority.IssueUserToken(MockOidcAuthority.Users[user], lifetime);

    // ── анонимный endpoint ──────────────────────────────────────────────────────

    [Fact]
    public async Task Public_Endpoint_Is_Anonymous()
    {
        var resp = await factory.CreateClient().GetAsync("/public");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await GetJsonAsync(factory.CreateClient(), "/public");
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("demo-api", body.GetProperty("service").GetString());
    }

    // ── без токена защищённые отдают 401 ────────────────────────────────────────

    [Theory]
    [InlineData("/me")]
    [InlineData("/admin")]
    [InlineData("/audit")]
    [InlineData("/group-data")]
    public async Task Protected_Endpoints_Without_Token_Return_401(string path)
    {
        var resp = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── /me: состав клеймов по контракту §2/§4 ─────────────────────────────────

    [Fact]
    public async Task Me_Returns_Contract_Claims()
    {
        using var client = factory.CreateClientWithToken(Token("alice"));
        var me = await GetJsonAsync(client, "/me");

        Assert.Equal("alice@orga.example.com", me.GetProperty("email").GetString());
        Assert.Equal("alice@orga.example.com", me.GetProperty("preferredUsername").GetString());
        Assert.Equal("orga", me.GetProperty("organization").GetString());

        var groups = me.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).ToList();
        Assert.Equal(["orga-managers"], groups);

        var roles = me.GetProperty("realmRoles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("user", roles);
        Assert.Contains("admin", roles);
        Assert.DoesNotContain("auditor", roles);
        // технические роли Keycloak присутствуют в токене (§3) — /me отдаёт как есть
        Assert.Contains("default-roles-rulink", roles);

        var services = me.GetProperty("services").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains("cryptoservice", services);
        Assert.Contains("cadastralanalyticservice", services);

        var scope = me.GetProperty("scope").GetString()!;
        Assert.Contains("email", scope.Split(' '));
    }

    // ── роли: /admin, /audit ────────────────────────────────────────────────────

    [Theory]
    [InlineData("alice", HttpStatusCode.OK)]          // admin orga
    [InlineData("dave", HttpStatusCode.OK)]           // admin orgb — роль realm'а, организация не важна
    [InlineData("bob", HttpStatusCode.Forbidden)]     // без admin
    [InlineData("carol", HttpStatusCode.Forbidden)]   // auditor ≠ admin
    public async Task Admin_Endpoint_Requires_Admin_Role(string user, HttpStatusCode expected)
    {
        using var client = factory.CreateClientWithToken(Token(user));
        Assert.Equal(expected, (await client.GetAsync("/admin")).StatusCode);
    }

    [Theory]
    [InlineData("carol", HttpStatusCode.OK)]          // auditor
    [InlineData("alice", HttpStatusCode.Forbidden)]   // admin ≠ auditor
    [InlineData("bob", HttpStatusCode.Forbidden)]     // без auditor
    public async Task Audit_Endpoint_Requires_Auditor_Role(string user, HttpStatusCode expected)
    {
        using var client = factory.CreateClientWithToken(Token(user));
        Assert.Equal(expected, (await client.GetAsync("/audit")).StatusCode);
    }

    // ── групповой ресурс (P3, task-06): члены группы 200, остальные 403 ────────

    [Fact]
    public async Task GroupData_Accessible_To_Members_Of_Same_Group()
    {
        // bob и carol — одна группа orga-team-a (рабочая пара контракта §4)
        foreach (var user in new[] { "bob", "carol" })
        {
            using var client = factory.CreateClientWithToken(Token(user));
            var body = await GetJsonAsync(client, "/group-data");
            var owner = body.GetProperty("resourceGroups").EnumerateArray().Select(g => g.GetString()).ToList();
            Assert.Equal(["orga-team-a"], owner);
            var data = body.GetProperty("data").EnumerateArray().ToList();
            Assert.True(data.Count > 0);
        }
    }

    [Theory]
    [InlineData("alice")]  // orga, но группа orga-managers
    [InlineData("dave")]   // другая организация (orgb, orgb-managers)
    [InlineData("erin")]   // другая группа и организация (orgb, orgb-team-b)
    public async Task GroupData_Forbidden_For_Non_Members(string user)
    {
        using var client = factory.CreateClientWithToken(Token(user));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/group-data")).StatusCode);
    }

    // ── валидация токена: подпись/iss/aud/exp ──────────────────────────────────

    [Fact]
    public async Task Token_With_Wrong_Audience_Rejected()
    {
        var token = factory.Authority.IssueToken(
            MockOidcAuthority.Users["alice"].ToClaims(), audience: "account");
        using var client = factory.CreateClientWithToken(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Token_With_Wrong_Issuer_Rejected()
    {
        var token = factory.Authority.IssueUserToken(MockOidcAuthority.Users["alice"]);
        var forged = factory.Authority.IssueToken(
            MockOidcAuthority.Users["alice"].ToClaims(),
            audience: "account,demo-api", issuer: "http://evil/realms/other");
        using var client = factory.CreateClientWithToken(forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Expired_Token_Rejected()
    {
        var token = Token("alice", lifetime: TimeSpan.FromMinutes(-10)); // глубже 5-минутного ClockSkew
        using var client = factory.CreateClientWithToken(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Garbage_Token_Rejected()
    {
        using var client = factory.CreateClientWithToken("not-a-jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }
}

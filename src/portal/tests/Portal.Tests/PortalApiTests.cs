using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Portal.Tests;

/// <summary>
/// Интеграционные тесты портала (task-07, DoD из плана §7 P4) на фейке Admin REST:
/// org-изоляция (admin A ↔ org B), валидации (services ⊆ availableServices,
/// фиксированные роли/сервисы, префикс групп), суперадмин-функции.
/// Живой прогон против Keycloak — task-08 (tests/Identity.IntegrationTests).
/// </summary>
public sealed class PortalApiTests : IClassFixture<PortalFactory>
{
    private readonly PortalFactory _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PortalApiTests(PortalFactory factory) => _factory = factory;

    private HttpClient Alice() => _factory.CreateClientWithToken(_factory.TokenFor(
        "alice@orga.example.com", "orga", ["user", "admin"], ["orga-managers"]));
    private HttpClient Dave() => _factory.CreateClientWithToken(_factory.TokenFor(
        "dave@orgb.example.com", "orgb", ["user", "admin"], ["orgb-managers"]));
    private HttpClient Bob() => _factory.CreateClientWithToken(_factory.TokenFor(
        "bob@orga.example.com", "orga", ["user"], ["orga-team-a"]));
    private HttpClient Root() => _factory.CreateClientWithToken(_factory.TokenFor(
        "root@rulink.example", null, ["user"], []));

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, object body)
        => await client.PostAsJsonAsync(url, body, Json);

    private static async Task<HttpResponseMessage> PutJsonAsync(HttpClient client, string url, object body)
        => await client.PutAsJsonAsync(url, body, Json);

    // ── /api/me, каталог ────────────────────────────────────────────────────────

    [Fact]
    public async Task Me_Returns_Org_Roles_And_Catalog()
    {
        using var alice = Alice();
        var me = await GetJsonAsync(alice, "/api/me");
        Assert.Equal("alice@orga.example.com", me.GetProperty("name").GetString());
        Assert.Equal("orga", me.GetProperty("organization").GetString());
        Assert.False(me.GetProperty("isSuperAdmin").GetBoolean());
        var roles = me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("admin", roles);
        Assert.Equal(3, me.GetProperty("catalog").GetProperty("services").GetArrayLength());
    }

    [Fact]
    public async Task Catalog_Returns_Fixed_Constants()
    {
        using var bob = Bob();
        var catalog = await GetJsonAsync(bob, "/api/catalog");
        Assert.Equal(["user", "admin", "auditor"],
            catalog.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList());
        Assert.Equal(["cryptoservice", "cadastralservice", "cadastralanalyticservice"],
            catalog.GetProperty("services").EnumerateArray().Select(r => r.GetString()).ToList());
    }

    [Fact]
    public async Task Anonymous_Api_Calls_Are_401_Not_Redirect()
    {
        using var client = _factory.CreateClient();
        using var resp = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode); // не 302 на OIDC-логин
    }

    // ── org-изоляция (план §7 P4: admin org A получает 403 на org B) ────────────

    [Fact]
    public async Task Users_List_Contains_Only_Own_Org_Members()
    {
        using var alice = Alice();
        var users = await GetJsonAsync(alice, "/api/users");
        var usernames = users.EnumerateArray().Select(u => u.GetProperty("username").GetString()).ToList();
        Assert.Contains("alice@orga.example.com", usernames);
        Assert.Contains("bob@orga.example.com", usernames);
        Assert.Contains("carol@orga.example.com", usernames);
        Assert.DoesNotContain("dave@orgb.example.com", usernames);
        Assert.DoesNotContain("erin@orgb.example.com", usernames);
    }

    [Fact]
    public async Task Cross_Org_User_Access_Is_Forbidden()
    {
        var daveId = _factory.FakeAdmin.User("dave@orgb.example.com").Id;
        using var alice = Alice();

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/users/{daveId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PutJsonAsync(alice, $"/api/users/{daveId}", new
            {
                firstName = "Hacked", role = "user", groups = Array.Empty<string>(), services = Array.Empty<string>(),
            })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/users/{daveId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await PostJsonAsync(alice, $"/api/users/{daveId}/password", new { password = "new-password-123", temporary = true })).StatusCode);
    }

    [Fact]
    public async Task Own_Org_User_Management_Succeeds()
    {
        var bobId = _factory.FakeAdmin.User("bob@orga.example.com").Id;
        using var alice = Alice();

        var bob = await GetJsonAsync(alice, $"/api/users/{bobId}");
        Assert.Equal("bob@orga.example.com", bob.GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.NoContent,
            (await PutJsonAsync(alice, $"/api/users/{bobId}", new { lastName = "Backoff-2" })).StatusCode);
        Assert.Equal("Backoff-2", _factory.FakeAdmin.User("bob@orga.example.com").LastName);
    }

    [Fact]
    public async Task Non_Admin_User_Cannot_Manage()
    {
        using var bob = Bob();
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/groups")).StatusCode);
    }

    [Fact]
    public async Task SuperAdmin_Cannot_Manage_Users_And_OrgAdmin_Cannot_Manage_Orgs()
    {
        using var root = Root();
        using var alice = Alice();
        // суперадмин не управляет пользователями (нет организации)
        Assert.Equal(HttpStatusCode.Forbidden, (await root.GetAsync("/api/users")).StatusCode);
        // админ организации не управляет организациями
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/organizations")).StatusCode);
    }

    // ── создание пользователя (план §7 P4: создан пользователь в org A) ─────────

    [Fact]
    public async Task CreateUser_Assigns_Role_Groups_Services_Org_And_TempPassword()
    {
        using var alice = Alice();
        using var resp = await PostJsonAsync(alice, "/api/users", new
        {
            email = "new.user@orga.example.com",
            firstName = "New",
            lastName = "User",
            role = "auditor",
            groups = new[] { "orga-team-a" },
            services = new[] { "cryptoservice", "cadastralservice" },
            password = "temporary-Passw0rd",
            temporaryPassword = true,
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = _factory.FakeAdmin.User("new.user@orga.example.com");
        Assert.Equal("org-orga-id", created.OrgId);                       // член org A
        Assert.Contains("user", created.Roles);
        Assert.Contains("auditor", created.Roles);                        // роль назначена
        Assert.Contains("group-orga-team-a-id", created.GroupIds);        // группа назначена
        Assert.Equal("cryptoservice cadastralservice", created.ServicesAttr); // атрибут services
        Assert.True(created.PasswordTemporary);                           // temporary + UPDATE_PASSWORD
    }

    [Fact]
    public async Task CreateUser_With_Unknown_Service_Is_400()
    {
        using var alice = Alice();
        using var resp = await PostJsonAsync(alice, "/api/users", new
        {
            email = "bad@orga.example.com", role = "user",
            groups = Array.Empty<string>(), services = new[] { "notaservice" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateUser_With_Service_Outside_Org_AvailableServices_Is_400()
    {
        using var root = Root();
        // суперадмин сужает availableServices orga до cryptoservice
        using (var update = await PutJsonAsync(root, "/api/organizations/orga", new
        {
            services = new[] { "cryptoservice" },
        }))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        using var alice = Alice();
        using var resp = await PostJsonAsync(alice, "/api/users", new
        {
            email = "svc-outside@orga.example.com", role = "user",
            groups = Array.Empty<string>(), services = new[] { "cadastralservice" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // возвращаем полный набор для остальных тестов
        using var restore = await PutJsonAsync(root, "/api/organizations/orga", new
        {
            services = new[] { "cryptoservice", "cadastralservice", "cadastralanalyticservice" },
        });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
    }

    [Fact]
    public async Task CreateUser_With_Role_Outside_Fixed_Set_Is_400()
    {
        using var alice = Alice();
        using var resp = await PostJsonAsync(alice, "/api/users", new
        {
            email = "root2@orga.example.com", role = "superrole",
            groups = Array.Empty<string>(), services = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SetPassword_Requires_Min_Length()
    {
        var bobId = _factory.FakeAdmin.User("bob@orga.example.com").Id;
        using var alice = Alice();
        using var resp = await PostJsonAsync(alice, $"/api/users/{bobId}/password",
            new { password = "short", temporary = false });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── группы (префикс <org>-, изоляция) ───────────────────────────────────────

    [Fact]
    public async Task Groups_List_Contains_Only_Own_Org_Prefix()
    {
        using var alice = Alice();
        var groups = await GetJsonAsync(alice, "/api/groups");
        var names = groups.EnumerateArray().Select(g => g.GetProperty("name").GetString()).ToList();
        Assert.All(names, n => Assert.StartsWith("orga-", n));
        Assert.Contains("orga-team-a", names);
    }

    [Fact]
    public async Task CreateGroup_Adds_Org_Prefix_Automatically()
    {
        using var alice = Alice();
        using var resp = await PostJsonAsync(alice, "/api/groups", new { name = "team-c" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("orga-team-c", created.GetProperty("name").GetString());
        Assert.Contains(_factory.FakeAdmin.Groups.Values, g => g.Name == "orga-team-c");
    }

    [Fact]
    public async Task CreateGroup_With_Foreign_Prefix_Is_Rejected()
    {
        using var alice = Alice();
        using var resp = await PostJsonAsync(alice, "/api/groups", new { name = "orgb-hack" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteGroup_Of_Foreign_Org_Is_Forbidden()
    {
        var orgbGroupId = _factory.FakeAdmin.Groups.Values.First(g => g.Name == "orgb-managers").Id;
        using var alice = Alice();
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/groups/{orgbGroupId}")).StatusCode);
    }

    // ── суперадмин: организации и availableServices (план §7 P4) ────────────────

    [Fact]
    public async Task SuperAdmin_Lists_Creates_And_Updates_Organizations()
    {
        using var root = Root();

        var orgs = await GetJsonAsync(root, "/api/organizations");
        Assert.Contains(orgs.EnumerateArray(), o => o.GetProperty("alias").GetString() == "orga");

        using var created = await PostJsonAsync(root, "/api/organizations", new
        {
            alias = "orgc", name = "Org C",
            services = new[] { "cryptoservice", "cadastralservice" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var orgc = _factory.FakeAdmin.Orgs.Values.Single(o => o.Alias == "orgc");
        Assert.Equal(new[] { "cadastralservice", "cryptoservice" }, orgc.Services.OrderBy(s => s).ToList());

        using var updated = await PutJsonAsync(root, "/api/organizations/orgc", new
        {
            services = new[] { "cadastralanalyticservice" },
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(["cadastralanalyticservice"],
            _factory.FakeAdmin.Orgs.Values.Single(o => o.Alias == "orgc").Services.ToList());
    }

    [Fact]
    public async Task SuperAdmin_Cannot_Create_Org_With_Unknown_Service()
    {
        using var root = Root();
        using var resp = await PostJsonAsync(root, "/api/organizations", new
        {
            alias = "orgbad", name = "Bad", services = new[] { "not-in-catalog" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── UI smoke ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Root_Page_Challenges_With_Oidc_Redirect()
    {
        using var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var resp = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
        Assert.StartsWith(MockOidcAuthority.AuthorityUrl, resp.Headers.Location.ToString());
        Assert.Contains("client_id=portal", resp.Headers.Location.Query); // OIDC-клиент portal
    }
}

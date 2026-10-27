using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// Орг-изоляция портала (task-08, план §7 P4): admin org A управляет пользователями org A (200)
/// и получает 403 на чужие; без роли admin — 403. Против живого портала (task-07).
/// </summary>
public sealed class PortalIsolationTests : IClassFixture<OidcClient>
{
    private readonly OidcClient _oidc;
    private readonly HttpClient _portal = new() { BaseAddress = new Uri(TestEnv.PortalBase) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PortalIsolationTests(OidcClient oidc) => _oidc = oidc;

    private async Task<HttpClient> PortalClientForAsync(string seedKey)
    {
        var seed = SeedUser.All[seedKey];
        var token = await _oidc.PasswordGrantAsync(seed.Email, TestEnv.DemoPassword!);
        Assert.True(token.HasToken, $"{seedKey}: логин не удался ({token.Error})");
        var client = new HttpClient { BaseAddress = new Uri(TestEnv.PortalBase) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    [LivePortalFact]
    public async Task Admin_A_Sees_Only_Org_A_Users()
    {
        Assert.NotNull(TestEnv.DemoPassword);
        using var alice = await PortalClientForAsync("alice");
        using var resp = await alice.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var users = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var usernames = users.EnumerateArray().Select(u => u.GetProperty("username").GetString()).ToList();
        Assert.Contains("bob@orga.example.com", usernames);
        Assert.DoesNotContain("dave@orgb.example.com", usernames);   // org B невидима
        Assert.DoesNotContain("erin@orgb.example.com", usernames);
    }

    [LivePortalFact]
    public async Task Admin_A_Manages_Own_Org_User_200()
    {
        Assert.NotNull(TestEnv.DemoPassword);
        using var alice = await PortalClientForAsync("alice");

        // create (DoD P4: пользователь создан в org A через API портала)
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var email = $"it-{stamp}@orga.example.com";
        using var created = await alice.PostAsJsonAsync("/api/users", new
        {
            email,
            firstName = "Integration",
            lastName = "Test",
            role = "user",
            groups = Array.Empty<string>(),
            services = new[] { "cryptoservice" },
        }, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // читать можно (своей организации)
        var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var id = createdBody.GetProperty("id").GetString()!;
        using var got = await alice.GetAsync($"/api/users/{id}");
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);

        // удалить за собой (не мусорить realm между прогонами)
        using var deleted = await alice.DeleteAsync($"/api/users/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [LivePortalFact]
    public async Task Admin_A_Gets_403_On_Foreign_Users()
    {
        Assert.NotNull(TestEnv.DemoPassword);
        using var alice = await PortalClientForAsync("alice");

        // пользователь org B (dave) — прямой доступ по id недоступен без членства в org A;
        // портал обязан ответить 403 (не 200/404-leak) на любую операцию с чужим пользователем
        var foreignId = await GetForeignUserIdAsync();
        Assert.False(string.IsNullOrEmpty(foreignId));

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/users/{foreignId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await alice.PutAsJsonAsync($"/api/users/{foreignId}", new { lastName = "X" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/users/{foreignId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await alice.PostAsJsonAsync($"/api/users/{foreignId}/password",
                new { password = "whatever-123", temporary = true }, Json)).StatusCode);
    }

    [LivePortalFact]
    public async Task Non_Admin_And_Superadmin_Boundaries()
    {
        Assert.NotNull(TestEnv.DemoPassword);
        // bob (роль user): управление запрещено
        using var bob = await PortalClientForAsync("bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/users")).StatusCode);

        // admin организации не управляет организациями
        using var alice = await PortalClientForAsync("alice");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/organizations")).StatusCode);
    }

    /// <summary>id пользователя dave (org B) — dave видит список своей организации (свои id там есть).</summary>
    private async Task<string?> GetForeignUserIdAsync()
    {
        using var dave = await PortalClientForAsync("dave");
        using var resp = await dave.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var users = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return users.EnumerateArray()
            .FirstOrDefault(u => u.GetProperty("username").GetString() == SeedUser.All["dave"].Email)
            .GetProperty("id").GetString();
    }
}

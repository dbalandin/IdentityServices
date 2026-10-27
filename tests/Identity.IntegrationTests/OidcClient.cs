using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Identity.IntegrationTests;

/// <summary>Хелперы OIDC: password grant (dev-only, включён в realm для тестов, task-02).</summary>
public sealed class OidcClient : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(TestEnv.IdentityBase) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public record TokenResponse(string? AccessToken, string? RefreshToken, string? Error, string? ErrorDescription)
    {
        public bool HasToken => AccessToken is { Length: > 0 };
    }

    public async Task<TokenResponse> PasswordGrantAsync(string username, string password, string clientId = "demo-api")
    {
        using var resp = await _http.PostAsync(
            $"{TestEnv.RealmBase}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = clientId,
                ["username"] = username,
                ["password"] = password,
                ["scope"] = "openid profile email",
            }));
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            var desc = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return new TokenResponse(null, null, error ?? resp.StatusCode.ToString(), desc);
        }
        using var ok = JsonDocument.Parse(body);
        return new TokenResponse(
            ok.RootElement.GetProperty("access_token").GetString(),
            ok.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            null, null);
    }

    /// <summary>Сылрые клеймы access-токена (валидация подписи — отдельно в тестах).</summary>
    public static Dictionary<string, JsonElement> ParseClaims(string jwt)
    {
        var parts = jwt.Split('.');
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = (payload.Length % 4) switch
        {
            2 => payload + "==",
            3 => payload + "=",
            _ => payload,
        };
        var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Seeded-пользователи контракта jwt-claims.md §4 (эталонные значения).</summary>
public sealed record SeedUser(string Email, string Organization, string[] Roles, string[] Groups, string[] Services)
{
    public static readonly Dictionary<string, SeedUser> All = new()
    {
        ["alice"] = new("alice@orga.example.com", "orga", ["user", "admin"], ["orga-managers"],
            ["cryptoservice", "cadastralservice", "cadastralanalyticservice"]),
        ["bob"] = new("bob@orga.example.com", "orga", ["user"], ["orga-team-a"],
            ["cryptoservice", "cadastralservice"]),
        ["carol"] = new("carol@orga.example.com", "orga", ["user", "auditor"], ["orga-team-a"],
            ["cadastralanalyticservice"]),
        ["dave"] = new("dave@orgb.example.com", "orgb", ["user", "admin"], ["orgb-managers"],
            ["cryptoservice", "cadastralservice", "cadastralanalyticservice"]),
        ["erin"] = new("erin@orgb.example.com", "orgb", ["user"], ["orgb-team-b"],
            ["cryptoservice"]),
    };
}

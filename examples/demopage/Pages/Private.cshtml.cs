using System.Net.Http.Headers;
using System.Text.Json;
using DemoApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Demopage.Pages;

/// <summary>Защищённая страница: клеймы из OIDC-сессии + вызов demo-api c access token.</summary>
[Authorize]
public class PrivateModel(IHttpClientFactory httpClientFactory, ILogger<PrivateModel> logger) : PageModel
{
    public string? Sub { get; private set; }
    public string? Username { get; private set; }
    public string? Email { get; private set; }
    public bool EmailVerified { get; private set; }
    public string? Organization { get; private set; }
    public IReadOnlyList<string> Groups { get; private set; } = [];
    public IReadOnlyList<string> Roles { get; private set; } = [];
    public IReadOnlyList<string> Services { get; private set; } = [];

    public string? ApiResult { get; private set; }
    public string? ApiError { get; private set; }

    public async Task OnGetAsync()
    {
        Sub = User.FindFirst("sub")?.Value;
        Username = User.Identity?.Name; // NameClaimType = preferred_username
        Email = User.FindFirst("email")?.Value;
        EmailVerified = bool.TryParse(User.FindFirst("email_verified")?.Value, out var v) && v;
        Organization = User.Organization();
        Groups = User.Groups();
        Roles = User.RealmRoles();
        Services = User.Services();

        // access token сохранён в cookie OIDC-сессией (SaveTokens=true) — им же зовём demo-api
        var token = await HttpContext.GetTokenAsync("access_token");
        if (token is null)
        {
            ApiError = "access_token отсутствует в сессии";
            return;
        }

        var client = httpClientFactory.CreateClient("demo-api");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var resp = await client.GetAsync("/me");
            var body = await resp.Content.ReadAsStringAsync();
            ApiResult = $"GET /me → HTTP {(int)resp.StatusCode}:\n" + (resp.IsSuccessStatusCode
                ? JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(body),
                    new JsonSerializerOptions { WriteIndented = true })
                : body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "вызов demo-api не удался");
            ApiError = $"demo-api недоступен ({ex.Message}). Поднимите examples/demo-api: dotnet run";
        }
    }
}

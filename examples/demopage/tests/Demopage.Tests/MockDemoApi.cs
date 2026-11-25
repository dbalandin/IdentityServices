using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Demopage.Tests;

/// <summary>
/// Мок demo-api для тестов demopage: отвечает на `GET /me` (клеймы по контракту
/// docs/contracts/jwt-claims.md) и запоминает заголовок Authorization — так тест проверяет,
/// что страница передаёт access token из cookie-сессии.
/// </summary>
public sealed class MockDemoApi : HttpMessageHandler
{
    /// <summary>Базовый адрес именованного клиента "demo-api" в тестах; в сеть запросы не уходят.</summary>
    public const string BaseUrl = "http://demo-api/";

    /// <summary>Ответ `GET /me` — та же форма, что у Program.cs demo-api (строка JSON без отступов).</summary>
    public static readonly string MeJson = JsonSerializer.Serialize(new
    {
        sub = "b6a1c2d0-0000-4000-8000-000000000001",
        preferredUsername = "alice@orga.example.com",
        email = "alice@orga.example.com",
        emailVerified = true,
        organization = "orga",
        groups = new[] { "orga-managers" },
        realmRoles = new[] { "user", "admin" },
        scope = "openid email profile",
        services = new[] { "cryptoservice", "cadastralservice" },
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Заголовок Authorization последнего запроса (или null).</summary>
    public string? LastAuthorization { get; private set; }

    /// <summary>Трассировка запросов к моку (для отладки тестов).</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastAuthorization = request.Headers.Authorization?.ToString();
        Requests.Enqueue($"{request.Method} {request.RequestUri} auth=[{LastAuthorization}]");
        if (request.RequestUri!.AbsolutePath != "/me")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MeJson, Encoding.UTF8, "application/json"),
        });
    }
}

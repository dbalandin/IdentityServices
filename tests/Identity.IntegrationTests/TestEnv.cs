using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// Конфигурация живого окружения (env с dev-дефолтами; см. tests/README.md):
///   IDENTITY_BASE_URL — Keycloak (по умолчанию http://127.0.0.1:10020)
///   IDENTITY_REALM    — rulink
///   PORTAL_BASE_URL   — портал task-07 (по умолчанию http://127.0.0.1:10030)
///   INIT_DEMO_PASSWORD — демо-пароль seeded-пользователей из .env (без него тесты токенов skip)
/// </summary>
public static class TestEnv
{
    public static string IdentityBase { get; } = Env("IDENTITY_BASE_URL", "http://127.0.0.1:10020").TrimEnd('/');
    public static string Realm { get; } = Env("IDENTITY_REALM", "rulink");
    public static string PortalBase { get; } = Env("PORTAL_BASE_URL", "http://127.0.0.1:10030").TrimEnd('/');
    public static string? DemoPassword { get; } = Environment.GetEnvironmentVariable("INIT_DEMO_PASSWORD");

    public static string RealmBase => $"{IdentityBase}/realms/{Realm}";

    private static string Env(string name, string dflt)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : dflt;

    /// <summary>TCP-доступность хоста:порта (быстрая проверка ~600мс).</summary>
    public static bool IsReachable(string baseUrl)
    {
        try
        {
            var uri = new Uri(baseUrl);
            var host = uri.Host == "localhost" ? "127.0.0.1" : uri.Host;
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, uri.Port);
            return task.Wait(600) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Тест контракта JWT: требует живой Keycloak (compose + init, task-01/02).</summary>
public sealed class LiveIdentityFactAttribute : FactAttribute
{
    public LiveIdentityFactAttribute()
    {
        if (!TestEnv.IsReachable(TestEnv.IdentityBase))
            Skip = $"Keycloak недоступен {TestEnv.IdentityBase}: поднимите deploy/docker-compose.yml + deploy/init-realm.sh (tests/README.md)";
    }
}

/// <summary>Тест портала: требует живой Keycloak + портал task-07.</summary>
public sealed class LivePortalFactAttribute : FactAttribute
{
    public LivePortalFactAttribute()
    {
        if (!TestEnv.IsReachable(TestEnv.IdentityBase) || !TestEnv.IsReachable(TestEnv.PortalBase))
            Skip = $"нужно живое окружение: Keycloak {TestEnv.IdentityBase} + портал {TestEnv.PortalBase} (tests/README.md)";
    }
}

/// <summary>Тест безопасности (brute force): нужен Keycloak + пароль из env.</summary>
public sealed class LiveSecurityFactAttribute : FactAttribute
{
    public LiveSecurityFactAttribute()
    {
        if (!TestEnv.IsReachable(TestEnv.IdentityBase))
        {
            Skip = $"Keycloak недоступен {TestEnv.IdentityBase}: поднимите deploy/docker-compose.yml + deploy/init-realm.sh (tests/README.md)";
            return;
        }
        if (TestEnv.DemoPassword is null or { Length: 0 })
            Skip = "INIT_DEMO_PASSWORD не задан (возьмите из .env)";
    }
}

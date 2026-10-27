namespace Portal;

/// <summary>
/// Конфигурация портала (секция Portal в appsettings / env Portal__*).
/// Секрет клиента portal в файле конфигурации НЕ хранится — только из окружения:
/// переменная PORTAL_CLIENT_SECRET (см. .envexample) или Portal__ClientSecret.
/// </summary>
public sealed class PortalOptions
{
    /// <summary>Realm Keycloak (по умолчанию rulink).</summary>
    public string Realm { get; set; } = "rulink";

    /// <summary>OIDC-клиент портала (confidential, task-02).</summary>
    public string ClientId { get; set; } = "portal";

    /// <summary>Секрет клиента; заполняется из env (PORTAL_CLIENT_SECRET), не из appsettings.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Базовый URL Keycloak для Admin REST API и client_credentials (dev: http://127.0.0.1:10020).</summary>
    public string KeycloakBaseUrl { get; set; } = "http://127.0.0.1:10020";
}

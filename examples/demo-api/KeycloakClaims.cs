using System.Text.Json;
using System.Security.Claims;

namespace DemoApi;

/// <summary>
/// Хелперы чтения клеймов Keycloak по контракту docs/contracts/jwt-claims.md (§3, §7; v1.1):
/// organization (строка), groups (массив строк), services (строка сервисов через пробел).
/// </summary>
/// <remarks>
/// MapInboundClaims=false — имена клеймов приходят как в токене (organization, groups, services…).
/// ВНИМАНИЕ на формат groups: обработчик JsonWebTokens «разворачивает» JSON-массив примитивов
/// в НЕСКОЛЬКО клеймов одного типа (по одному на группу), поэтому хелпер поддерживает оба вида:
/// N клеймов-строк и один клейм со значением-JSON-массивом (старое поведение JwtSecurityTokenHandler).
/// </remarks>
public static class KeycloakClaims
{
    public const string OrganizationClaim = "organization";
    public const string GroupsClaim = "groups";
    public const string ServicesClaim = "services"; // контракт v1.1: отдельный клейм, НЕ scope
    public const string ScopeClaim = "scope";
    public const string RealmAccessClaim = "realm_access";

    /// <summary>Организация пользователя — строка (alias); у пользователя ровно одна (контракт §3).</summary>
    public static string? Organization(this ClaimsPrincipal user)
        => user.FindFirst(OrganizationClaim)?.Value;

    /// <summary>Группы пользователя — имена без пути (mapper full.path=false, task-02).</summary>
    public static IReadOnlyList<string> Groups(this ClaimsPrincipal user)
    {
        var claims = user.FindAll(GroupsClaim).ToList();
        if (claims.Count == 0)
            return [];

        // вариант «один клейм — JSON-массив строк»
        if (claims.Count == 1 && claims[0].Value.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(claims[0].Value) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        // вариант «N клеймов по одному на группу» (JsonWebTokensHandler)
        return claims.Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
    }

    /// <summary>Пользователь состоит в группе (проверка — вхождение имени, контракт §3).</summary>
    public static bool IsInGroup(this ClaimsPrincipal user, string group)
        => user.Groups().Contains(group, StringComparer.Ordinal);

    /// <summary>Список имён сервисов из клейма services (split по пробелам; контракт v1.1 — отдельный клейм, не scope).</summary>
    public static IReadOnlyList<string> Services(this ClaimsPrincipal user)
        => user.FindFirst(ServicesClaim)?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>Доступ к сервису разрешён — имя сервиса входит в клейм services (контракт §3: вхождение, не равенство).</summary>
    public static bool HasServiceScope(this ClaimsPrincipal user, string service)
        => user.Services().Contains(service, StringComparer.Ordinal);

    /// <summary>Прикладные realm-роли пользователя из realm_access.roles (без технических ролей Keycloak).</summary>
    public static IReadOnlyList<string> RealmRoles(this ClaimsPrincipal user)
    {
        // после RealmRolesTransformer роли также продублированы в ClaimTypes.Role;
        // здесь читаем исходный клейм — для отображения в /me.
        // JsonDocument (а не Deserialize<T>): имена полей Keycloak в нижнем регистре,
        // STJ-десериализация регистрозависима
        var json = user.FindFirst(RealmAccessClaim)?.Value;
        if (json is null)
            return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("roles", out var roles)
                ? roles.EnumerateArray().Select(r => r.GetString()).OfType<string>().ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

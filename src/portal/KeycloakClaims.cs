using System.Security.Claims;

namespace Portal;

/// <summary>
/// Хелперы чтения клеймов Keycloak по контракту docs/contracts/jwt-claims.md (§3, §7).
/// Тот же паттерн, что в examples/demo-api (KeycloakClaims.cs, task-04).
/// </summary>
public static class KeycloakClaims
{
    public const string OrganizationClaim = "organization";
    public const string GroupsClaim = "groups";

    /// <summary>Организация пользователя — alias строкой; у пользователя ровно одна (контракт §3). null = суперадмин (вне организаций).</summary>
    public static string? Organization(this ClaimsPrincipal user)
        => user.FindFirst(OrganizationClaim)?.Value;

    /// <summary>Группы пользователя — имена без пути (mapper full.path=false, task-02).</summary>
    public static IReadOnlyList<string> Groups(this ClaimsPrincipal user)
    {
        var claims = user.FindAll(GroupsClaim).ToList();
        if (claims.Count == 0)
            return [];
        if (claims.Count == 1 && claims[0].Value.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string[]>(claims[0].Value) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }
        return claims.Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
    }
}

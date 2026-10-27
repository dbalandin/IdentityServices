using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace DemoApi;

/// <summary>
/// Трансформация realm_access.roles → ClaimTypes.Role, чтобы работали
/// [Authorize(Roles = "…")] / RequireRole (паттерн из docs/contracts/jwt-claims.md §7).
/// realm_access — вложенный JSON-объект, стандартный RoleClaimType его не читает.
/// </summary>
public sealed class RealmRolesTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is ClaimsIdentity id
            && !id.HasClaim(c => c.Type == ClaimTypes.Role)
            && principal.FindFirst(KeycloakClaims.RealmAccessClaim)?.Value is { } json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("roles", out var roles))
            {
                foreach (var role in roles.EnumerateArray())
                {
                    if (role.GetString() is { } r && r.Length > 0)
                    {
                        id.AddClaim(new Claim(ClaimTypes.Role, r));
                    }
                }
            }
        }

        return Task.FromResult(principal);
    }
}

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace Portal;

/// <summary>
/// realm_access.roles → ClaimTypes.Role: работают [Authorize(Roles=…)]/IsInRole (контракт §7).
/// Паттерн тот же, что в examples/demo-api (task-04): realm_access — вложенный JSON,
/// RoleClaimType="realm_access.roles" не работает.
/// </summary>
public sealed class RealmRolesTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is ClaimsIdentity id
            && !id.HasClaim(c => c.Type == ClaimTypes.Role)
            && principal.FindFirst("realm_access")?.Value is { } json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("roles", out var roles))
                foreach (var role in roles.EnumerateArray())
                    if (role.GetString() is { Length: > 0 } r)
                        id.AddClaim(new Claim(ClaimTypes.Role, r));
        }
        return Task.FromResult(principal);
    }
}

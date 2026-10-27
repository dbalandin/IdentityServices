using System.Security.Claims;
using Xunit;

namespace DemoApi.Tests;

/// <summary>Юнит-тесты хелперов клеймов (обе формы groups-клейма, scope-split, роли).</summary>
public sealed class KeycloakClaimsTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "TestAuth"));

    // Keycloak выпускает groups как JSON-массив; JsonWebTokensHandler разворачивает его
    // в N клеймов одного типа — основная форма для .NET 10
    [Fact]
    public void Groups_From_Multiple_Claims()
    {
        var user = Principal(
            new Claim("groups", "orga-team-a"),
            new Claim("groups", "orga-managers"));
        Assert.Equal(["orga-team-a", "orga-managers"], user.Groups());
        Assert.True(user.IsInGroup("orga-team-a"));
        Assert.False(user.IsInGroup("orgb-team-b"));
    }

    // альтернативная форма: один клейм-строка с JSON-массивом (старый JwtSecurityTokenHandler)
    [Fact]
    public void Groups_From_Json_String_Claim()
    {
        var user = Principal(new Claim("groups", "[\"orga-team-a\"]"));
        Assert.Equal(["orga-team-a"], user.Groups());
    }

    [Fact]
    public void Groups_Empty_When_Absent()
    {
        var user = Principal(new Claim("sub", "x"));
        Assert.Empty(user.Groups());
    }

    [Fact]
    public void Organization_Returns_String()
    {
        var user = Principal(new Claim("organization", "orga"));
        Assert.Equal("orga", user.Organization());
    }

    [Fact]
    public void Services_Split_Scope_By_Space()
    {
        var user = Principal(new Claim("scope", "openid email profile cryptoservice cadastralservice"));
        Assert.Equal(["openid", "email", "profile", "cryptoservice", "cadastralservice"], user.Services());
        Assert.True(user.HasServiceScope("cryptoservice"));
        Assert.False(user.HasServiceScope("cadastralanalyticservice"));
    }

    [Fact]
    public void RealmRoles_Parses_RealmAccess_Json()
    {
        var user = Principal(new Claim("realm_access", "{\"roles\":[\"user\",\"admin\",\"default-roles-rulink\"]}"));
        Assert.Equal(["user", "admin", "default-roles-rulink"], user.RealmRoles());
    }

    [Fact]
    public void RealmRoles_Empty_When_Absent()
    {
        var user = Principal(new Claim("sub", "x"));
        Assert.Empty(user.RealmRoles());
    }
}

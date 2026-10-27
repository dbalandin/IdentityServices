using System.Text.Json.Serialization;

namespace Portal.KeycloakAdmin;

/// <summary>UserRepresentation Keycloak Admin REST (используемые порталом поля).</summary>
public sealed class KcUser
{
    public string? Id { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool Enabled { get; set; } = true;
    public bool EmailVerified { get; set; } = true;
    public Dictionary<string, List<string>>? Attributes { get; set; }
    public List<string>? RequiredActions { get; set; }

    /// <summary>Сервисы пользователя из атрибута services (строка через пробел — формат task-02).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Services =>
        Attributes is not null && Attributes.TryGetValue("services", out var values)
            ? (values.SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList())
            : [];
}

/// <summary>GroupRepresentation (портал работает с группами верхнего уровня `<org>-*`).</summary>
public sealed class KcGroup
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

/// <summary>RoleRepresentation (realm-роли user/admin/auditor).</summary>
public sealed class KcRole
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

/// <summary>OrganizationRepresentation Keycloak Organizations (KC 26+).</summary>
public sealed class KcOrganization
{
    public string? Id { get; set; }
    public string? Alias { get; set; }
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    public Dictionary<string, List<string>>? Attributes { get; set; }

    /// <summary>availableServices организации — задаёт суперадмин (PRD FR 6.6); каждый сервис отдельным значением атрибута.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AvailableServices =>
        Attributes is not null && Attributes.TryGetValue("availableServices", out var values)
            ? values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList()
            : [];
}

namespace Portal;

/// <summary>
/// Фиксированные константы фазы 1 (PRD FR 6): прикладные realm-роли и базовый набор сервисов.
/// Портал НЕ позволяет создавать роли и элементы scope — списки ниже исчерпывающие;
/// расширение каталога — отдельное решение (версия контракта + init-скрипт + CHANGELOG).
/// </summary>
public static class ServiceCatalog
{
    /// <summary>Прикладные realm-роли (realm `rulink`, task-02); технические роли KC не входят.</summary>
    public static readonly IReadOnlyList<string> Roles = ["user", "admin", "auditor"];

    /// <summary>Базовый scope экосистемы: имена сервисов (PRD FR 6.6).</summary>
    public static readonly IReadOnlyList<string> Services =
        ["cryptoservice", "cadastralservice", "cadastralanalyticservice"];

    public static bool IsKnownRole(string? role) => role is not null && Roles.Contains(role);

    public static bool IsKnownService(string? service) => service is not null && Services.Contains(service);
}

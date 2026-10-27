namespace Portal;

/// <summary>Нарушение org-изоляции: админ пытается работать вне своей организации → HTTP 403.</summary>
public sealed class PortalForbiddenException(string message) : Exception(message);

/// <summary>Сущность не найдена → HTTP 404.</summary>
public sealed class PortalNotFoundException(string message) : Exception(message);

/// <summary>Ошибка валидации запроса (роль/сервис вне каталога, services ⊄ availableServices, имя группы и т.п.) → HTTP 400.</summary>
public sealed class PortalValidationException : Exception
{
    public IReadOnlyList<string> Details { get; }

    public PortalValidationException(string message, IReadOnlyList<string>? details = null)
        : base(message)
        => Details = details ?? [];
}

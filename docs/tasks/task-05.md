# Task 05: docs/integrations/dotnet-oauth.md — инструкция P2 (подключение OIDC к .NET 10)

- **Статус:** open
- **Фаза:** PRD P2
- **Зависимости:** task-04 (demo-api — среда проверки инструкции)
- **Источник:** план §5.5; PRD FR 4, milestone P2

## Цель

Пошаговая инструкция для команд-потребителей: как подключить аутентификацию через Keycloak к ASP.NET Core (.NET 10) сервису, чтобы работали `[Authorize]` и `[Authorize(Roles = ...)]`.

## Что сделать

Написать `docs/integrations/dotnet-oauth.md`:

1. Предпосылки: клиент в realm `rulink` (какой тип клиента для web api), authority/audience.
2. Пакеты NuGet (например, `Microsoft.AspNetCore.Authentication.JwtBearer`).
3. Конфигурация `Program.cs`:
   - `AddJwtBearer`: authority, audience, `TokenValidationParameters` (`RoleClaimType = "realm_access.roles"`, `NameClaimType = "preferred_username"`) — с объяснением, зачем;
   - CORS/dev-настройки по необходимости.
4. Использование: `[Authorize]`, `[Authorize(Roles = "admin")]`, анонимные endpoints, чтение клеймов (`User.FindFirst(...)`).
5. Получение токена для тестов: direct access grants (dev), ссылка на seeded-пользователей.
6. Logout и refresh token: что делает сервис (ничего — stateless JWT) и что делает клиент (refresh через token endpoint).
7. Проверка инструкции: каждый шаг воспроизводится на `examples/demo-api` из чистого клона (compose up + init + dotnet run — без дополнительных недокументированных шагов).
8. Зафиксировать изменение в `docs/CHANGELOG.md`.

## Критерии готовности

- [ ] Файл `docs/integrations/dotnet-oauth.md` существует и покрывает пункты 1–6
- [ ] Инструкция проверена на demo-api: воспроизводима без дополнительных шагов
- [ ] `[Authorize]` и роли работают как описано (см. DoD task-04)
- [ ] Запись в `docs/CHANGELOG.md`

## Ссылки

- план §7 «Валидация» — критерий P2
- docs/contracts/jwt-claims.md, docs/tasks/task-04.md

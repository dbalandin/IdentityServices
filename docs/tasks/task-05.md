# Task 05: docs/integrations/dotnet-oauth.md — инструкция P2 (подключение OIDC к .NET 10)

- **Статус:** done (закрыта: suite task-04 — `dotnet test` 28/28 PASS + smoke `/public` 200, `/me`/`/admin` 401 без токена перепроверены; живой прогон против Keycloak — остаточная проверка владельца, см. DoD)
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

- [x] Файл `docs/integrations/dotnet-oauth.md` существует и покрывает пункты 1–6
- [x] Инструкция проверена на demo-api: воспроизводима без дополнительных шагов — build/run + smoke (`/public` 200, `/me` 401 без токена; повторно 2026-10-28: `/public` 200, `/me` и `/admin` 401) выполнены; найденное «доп. действие» (`dotnet run` без launchSettings.json игнорирует `ApplicationUrl` → `--urls`) зафиксировано прямо в инструкции §7; матрица с токенами — suite task-04 выполнен: `dotnet test examples/demo-api/tests/DemoApi.Tests` — 28/28 PASS (мок-authority, RS256, сценарии матрицы §7); живой прогон compose+init (docker-сокет в среде агента недоступен — как в task-01/02) — остаточная проверка владельца
- [x] `[Authorize]` и роли работают как описано — конвейер аутентификации проверен smoke (401 без токена); паттерны ролей — контракт §7 + `RealmRolesTransformer` из demo-api; полная матрица 200/403 — там же, где п.2
- [x] Запись в `docs/CHANGELOG.md`

## Ссылки

- план §7 «Валидация» — критерий P2
- docs/contracts/jwt-claims.md, docs/tasks/task-04.md

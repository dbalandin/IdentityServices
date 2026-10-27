# Task 04: examples/demopage/demo-api — .NET 10 демо-сервис

- **Статус:** open
- **Фаза:** PRD P1/P3 (живая проверка инструкций P2/P3)
- **Зависимости:** task-02 (realm, клиент `demo-api`), task-03 (контракт клеймов)
- **Источник:** план §5.4; PRD FR 4–5

## Цель

Минимальный .NET 10 web api, который валидирует JWT Keycloak и демонстрирует все паттерны из будущих инструкций P2/P3: `[Authorize]`, роли, групповые данные. По нему проверяются инструкции task-05/task-06.

## Что сделать

### Задание 1.

1. `examples/demo-api/` — ASP.NET Core **.NET 10** minimal api:
   - JWT bearer-валидация против realm `rulink` (authority `http://127.0.0.1:10020/realms/rulink`, audience `demo-api`);
   - role claim transformation: роли из `realm_access.roles` работают с `[Authorize(Roles = ...)]`.
2. Endpoints:
   - анонимный (например, `GET /public` — health/echo);
   - `GET /me` — `[Authorize]`, возвращает клеймы пользователя (sub, email, organization, groups, roles, scope);
   - `GET /admin` — `[Authorize(Roles = "admin")]`;
   - `GET /audit` — `[Authorize(Roles = "auditor")]`;
   - `GET /group-data` — групповой ресурс: доступен только членам заданной группы (проверка клейма `groups`).
3. Конфигурация — из appsettings + env (authority, audience), без секретов в репо.
4. Зафиксировать изменение в `docs/CHANGELOG.md`.

### Задание 2.

1. сделать простое web applicaiton на ASP.Net Core **.Net 10** Razor
2. сделать станицу с `[Authorize]` и страницу c AllowAnonymous
3. Сделать редирект на IdentityService, он будет работать на url= `https://id.rulink.io`
4. собири все артифакты, которые нужны для того, чтобы токен работал с сайтом.

## Критерии готовности

- [ ] `dotnet run` поднимает сервис, endpoints отвечают согласно авторизации:
  - анонимный — 200; защищённые без токена — 401
  - `/admin` с ролью admin — 200, без роли — 403
  - `/audit` с ролью auditor — 200, без роли — 403
  - `/group-data`: два пользователя одной группы — 200; пользователь другой группы/организации — 403
- [ ] Сервис соответствует контракту `docs/contracts/jwt-claims.md`
- [ ] Запись в `docs/CHANGELOG.md`

## Ссылки

- docs/contracts/jwt-claims.md
- docs/tasks/task-05.md, docs/tasks/task-06.md — инструкции, проверяемые на этом сервисе

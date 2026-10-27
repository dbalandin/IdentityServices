# Task 04: examples/demopage/demo-api — .NET 10 демо-сервис

- **Статус:** in progress (код готов; автотесты на мок-OIDC 28/28, smoke-прогоны ок; живой прогон против Keycloak — владельцем: docker в среде агента недоступен)
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

- [x] `dotnet run` поднимает сервис (smoke: `/public` 200, защищённые без токена 401); авторизация endpoints — автотесты на мок-OIDC (28/28):
  - [x] анонимный — 200; защищённые без токена — 401
  - [x] `/admin` с ролью admin — 200, без роли — 403 (alice/dave 200, bob/carol 403)
  - [x] `/audit` с ролью auditor — 200, без роли — 403 (carol 200, alice/bob 403)
  - [x] `/group-data`: bob+carol (orga-team-a) — 200; alice/dave/erin (другая группа/организация) — 403
  - [ ] то же живьём против compose-Keycloak (password grant) — владелец, сценарии в `examples/demo-api/README.md`
- [x] Сервис соответствует контракту `docs/contracts/jwt-claims.md` — хелперы клеймов (§7), валидация iss/aud/подписи RS256/lifetime — автотесты (`MockOidcAuthority`, seeded-пользователи §4)
- [x] Задание 2: demopage — `/Index` AllowAnonymous 200, `/Private` 302 на IdP (`…/protocol/openid-connect/auth`, client_id=demo-api, PKCE, redirect_uri `…/signin-oidc`) — smoke dev+Production (профили appsettings);
      артефакты токена — README demopage §«Артефакты» + bootstrap: redirect URIs клиента demo-api параметризованы env (`DEMO_API_REDIRECT_URIS`/`DEMO_API_WEB_ORIGINS`)
- [ ] Живой OIDC-логин на demopage (браузер + Keycloak) — владелец
- [x] Запись в `docs/CHANGELOG.md`

## Ссылки

- docs/contracts/jwt-claims.md
- docs/tasks/task-05.md, docs/tasks/task-06.md — инструкции, проверяемые на этом сервисе

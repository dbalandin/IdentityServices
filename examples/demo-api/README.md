# demo-api — .NET 10 демо-сервис (task-04, задание 1)

Минимальный ASP.NET Core **.NET 10** web api: приём токенов Keycloak (realm `rulink`)
и демонстрация паттерней P2/P3 — `[Authorize]`, роли из `realm_access.roles`,
групповой ресурс по клейму `groups`. Живая среда для проверки инструкций
`docs/integrations/dotnet-oauth.md` (task-05) и `group-data.md` (task-06).

Аутентификация — **библиотека `IdentityService`** (`AddRulinkIdentityService`, режим `Jwt`).
Собственной реализации в проекте нет: ни настройки JwtBearer, ни хелперов клеймов, ни
`RealmRolesTransformer` — `user.Organization()`, `Groups()`, `RealmRoles()`, `Services()` и
преобразование ролей приходят из библиотеки (её README — `IdentityService/README.md`).
Контракт токена: `docs/contracts/jwt-claims.md`.

## Запуск

Предусловия: Keycloak realm `rulink` поднят (`deploy/` — compose + init), .NET 10 SDK,
каталог библиотеки — рядом с репозиторием (`../IdentityService/IdentityService/IdentityService.csproj`;
тот же относительный путь в `examples/example.slnx`).

```bash
cd examples/demo-api
dotnet run            # http://127.0.0.1:10010 (Urls в appsettings.json)
```

Конфигурация (appsettings.json + env, секретов нет — это resource server). Секция `Identity` —
модель библиотеки `IdentityConfiguration`; остальные её ключи (`RequireHttpsMetadata` и др.) — в README библиотеки.

| Что | appsettings | env | По умолчанию |
|---|---|---|---|
| Issuer/Authority | `Identity:Authority` = `http://127.0.0.1:10020/realms/rulink` | `IDENTITY__AUTHORITY` | обязателен |
| Режим | `Identity:Mode` = `Jwt` | `IDENTITY__MODE` | `Hybrid`; для чистого API нужен `Jwt` — 401 на всех маршрутах, без редиректов |
| Audience | `Identity:Audience` = `demo-api` | `IDENTITY__AUDIENCE` | пусто — проверяются только подпись и `iss` |
| Группы-владельцы `/group-data` | `GroupData:AllowedGroups` = `orga-team-a` | `GROUPDATA__ALLOWEDGROUPS` | `orga-team-a` |

Непустой `Audience` — строгая проверка `aud` (токен выдан в том числе клиенту `demo-api`, audience mapper
в bootstrap realm). Чтобы принимать токены любого клиента realm — удалите ключ. Ошибки конфигурации
(нет `Authority`, путь вместо URL…) падают при старте с перечнем полей.

## Endpoints

| Endpoint | Доступ | Что возвращает |
|---|---|---|
| `GET /public` | анонимно | health/echo (`identityAuthority` — из конфигурации библиотеки) |
| `GET /me` | `[Authorize]` | клеймы: sub, email, organization, groups, realmRoles, scope/services |
| `GET /admin` | роль `admin` | 200/403 |
| `GET /audit` | роль `auditor` | 200/403 |
| `GET /group-data` | члены групп из `GroupData:AllowedGroups` | групповой ресурс; остальные — 403 |

Роли: `realm_access.roles` → `ClaimTypes.Role` (`RealmRolesTransformer` библиотеки, регистрируется
автоматически) — работают `[Authorize(Roles = "…")]` / `RequireAuthorization(new AuthorizeAttribute { Roles = … })`.

## Проверка сценариев DoD (curl)

Пароль seeded-пользователей — из env `INIT_DEMO_PASSWORD` (см. `deploy/keycloak-rulink/.envexample`).
Пользователи: alice (admin, orga-managers), bob/carol (orga-team-a), dave (orgb, admin), erin (orgb).

```bash
TOKEN=$(curl -s http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/token \
  -d grant_type=password -d client_id=demo-api \
  -d username=bob@orga.example.com -d password="$INIT_DEMO_PASSWORD" | jq -r .access_token)

curl http://127.0.0.1:10010/public                       # 200 анонимно
curl http://127.0.0.1:10010/me                           # 401 без токена
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:10010/me           # 200 + клеймы
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:10010/group-data   # 200 (bob в orga-team-a)
# токеном alice → /admin 200, /audit 403, /group-data 403 (orga-managers ≠ orga-team-a)
# токеном carol → /audit 200; токеном dave (orgb) → /group-data 403
```

## Тесты (без Keycloak — мок OIDC authority)

`tests/DemoApi.Tests`: мок realm'а (discovery + JWKS + RS256-токены seeded-пользователей
по контракту §4) поверх `WebApplicationFactory` — честная валидация подписи/iss/aud/exp/lifetime
схемами, зарегистрированными библиотекой. Юнит-тесты хелперов клеймов — в самой библиотеке
(`IdentityService.Tests`).

```bash
dotnet test examples/demo-api/tests/DemoApi.Tests   # 21 тест: DoD-сценарии
```

Живой прогон против compose-инсталляции Keycloak — владельцем (docker в среде
агента недоступен, см. CHANGELOG).

## Образ (task-11)

Библиотека подключена `ProjectReference` на каталог вне репозитория, поэтому при сборке образа
он передаётся именованным контекстом (BuildKit; команда — из корня репозитория):

```bash
docker build --build-context identityservice=../IdentityService/IdentityService \
  --provenance=false --sbom=false \
  -t cr.yandex/crp9et2dv05vocv8pagt/idptestappapi:latest examples/demo-api
```

Деплой — `deploy/idp-test-appapi/` (https://idp-test2.rulink.io).

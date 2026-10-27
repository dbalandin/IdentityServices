# portal — ASP.NET 10 портал администратора (task-07, PRD P4)

CRUD пользователей и групп **своей** организации, назначение ролей/сервисов, смена пароля;
суперадмин управляет организациями и их `availableServices`. Поверх Keycloak Admin REST API;
org-изоляция — в логике портала (`PortalAdminService` — единственное место инвариантов).

## Как устроено

| Компонент | Что делает |
|---|---|
| OIDC-логин (UI) | confidential-клиент `portal` (создаётся init-скриптом task-02); cookie-сессия |
| API `/api/*` | Bearer-JWT realm `rulink` (проверка iss+подпись; aud опционален — контракт §3) |
| Admin REST | сервис-аккаунт клиента `portal` (client_credentials, `AdminTokenProvider` кэширует токен) |
| Инварианты | `PortalAdminService`: org-изоляция (403), `services ⊆ availableServices` (400), фиксированные роли/сервисы, префикс групп `<org>-` |

Запреты (PRD FR 6): портал **не позволяет** создавать realm-роли (только `user`/`admin`/`auditor`)
и элементы scope (каталог фиксирован константами `ServiceCatalog`). Суперадмин — аутентифицированный
пользователь **вне организаций** (клейм `organization` отсутствует; seeded `root@rulink.example`).

## Запуск (dev)

Keycloak должен работать (`deploy/docker-compose.yml` + `deploy/init-realm.sh`, task-01/02):

```bash
cd src/portal
export PORTAL_CLIENT_SECRET=…   # из .env (PORTAL_CLIENT_SECRET, выдаёт init-скрипт)
dotnet run --urls http://127.0.0.1:10030
```

- Портал: http://127.0.0.1:10030 (redirect URI `http://127.0.0.1:10030/*` — дефолт
  `PORTAL_REDIRECT_URIS` из `.envexample`,task-02)
- Вход: админ организации — `alice@orga.example.com` (orga) / `dave@orgb.example.com` (orgb);
  суперадмин — `root@rulink.example` (пароли — `INIT_DEMO_PASSWORD` из `.env`)
- Конфиг: `appsettings.json` + env `Identity__Authority`, `Portal__KeycloakBaseUrl`,
  `Portal__ClientSecret` (альтернатива `PORTAL_CLIENT_SECRET`)

Контейнер: есть `Dockerfile` (SDK→ASP.NET 10). В compose портал фазы 1 **не включён**:
внутри compose-сети discovery Keycloak отдаётся по внутреннему адресу `keycloak:8080`,
а issuer/redirect — по `127.0.0.1:10020` (hostname v2) — связка требует проверки владельца
на живом окружении; dev-режим фазы 1 — `dotnet run` на хосте (как demo-api/demopage).

## API

| Endpoint | Кто | Что |
|---|---|---|
| `GET /api/me`, `GET /api/catalog` | любой аутентифицированный | клеймы / фиксированные константы |
| `GET /api/users?search=` | admin своей org | пользователи своей организации |
| `GET/PUT/DELETE /api/users/{id}` | admin своей org | пользователь своей org (чужая → 403) |
| `POST /api/users` | admin своей org | создать: email, firstName/lastName, role, groups[], services[], password (temporary) |
| `POST /api/users/{id}/password` | admin своей org | смена/сброс: `{password, temporary}` (temporary → required action UPDATE_PASSWORD) |
| `GET/POST/DELETE /api/groups[/{id}]` | admin своей org | группы своей org; имя создаётся с префиксом `<org>-` |
| `GET/POST /api/organizations`, `PUT /api/organizations/{alias}` | суперадмин | организации и `availableServices` |

Ошибки: 400 (валидация, `details[]`), 403 (org-изоляция / нет роли), 404, 502 (Keycloak недоступен).

```bash
# токен администратора (password grant, dev-only — direct access grants включены для тестов)
TOKEN=$(curl -s http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/token \
  -d grant_type=password -d client_id=demo-api \
  -d username=alice@orga.example.com -d password="$INIT_DEMO_PASSWORD" | jq -r .access_token)

curl -s http://127.0.0.1:10030/api/users -H "Authorization: Bearer $TOKEN" | jq .
curl -s -X POST http://127.0.0.1:10030/api/users -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"email":"new.user@orga.example.com","role":"user","groups":["orga-team-a"],
       "services":["cryptoservice"],"password":"Temp-Passw0rd","temporaryPassword":true}'
# пользователь org B от alice → 403:
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:10030/api/users/<dave-id> -H "Authorization: Bearer $TOKEN"
```

## Тесты

```bash
dotnet test src/portal/tests/Portal.Tests     # 20/20: изоляция, валидации, суперадмин, UI-challenge
```

Интеграционные тесты против живого Keycloak + портала — `tests/Identity.IntegrationTests` (task-08).

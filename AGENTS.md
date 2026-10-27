# IdentityService — правила для агентов и разработчиков

Общий сервис аутентификации экосистемы на keycloak.
Что сделано и как запускать — `README.md`.  
Задача и план: внедрить keycloak сервис как idp, основной проект где будет использоваться identity - это .net 10 core приложения.

## Устройство

| Путь | Что |
|---|---|
| `deploy` | запуск сервиса: `docker-compose.yml` (Keycloak 26.x + внешний PostgreSQL), `init-realm.sh` → идемпотентный bootstrap realm `rulink` (`init/realm-bootstrap.sh`), `verify.sh` (ручная сверка JWT) |
| `docs` | `PRD.md`, `Plan.md` (фаза 1), `tasks/` (task-00…09), `CHANGELOG.md`, `contracts/jwt-claims.md` (эталон JWT), `integrations/` (инструкции P2/P3 потребителям) |
| `src/portal` | портал администратора — ASP.NET Core **.NET 10** поверх Keycloak Admin REST API (клиент `portal`, org-изоляция в логике портала, task-07) |
| `examples/demo-api` | демо resource-server .NET 10 (контракт JWT, роли, группы; P2/P3) + `examples/demopage` — демо-сайт (OIDC-логин) |
| `tests/Identity.IntegrationTests` | интеграционные тесты живого окружения: контракт JWT, орг-изоляция портала, безопасность (task-08) |

Все секреты и переменные docker должны храниться только в переменных окружения (`.env`)
Все volume должны подключаться только через `mount volume`.

## Нельзя менять без решения владельца

- Названия папок проекта

## Безопасность

- Секреты — только из переменных окружения (`JWT_SECRET_KEY`, `PERSON_HASH_KEY`, `INTERNAL_API_TOKENS`, `CLIENT_SECRETS`, `LDAP_*`).
  Не логировать пароли, токены, хеши, e-mail и телефоны.

## Сборка и тесты

SDK — .NET 10 (`dotnet`). Порты dev: Keycloak `127.0.0.1:10020`, demo-api `10010`, портал `10030`, demopage `10040`.

```bash
# окружение (Keycloak + realm rulink, task-01/02)
cd deploy && cp .envexample ../.env  # заполнить один раз
docker compose --env-file ../.env up -d && ./init-realm.sh

# тесты без живого окружения (моки):
dotnet test examples/demo-api/tests/DemoApi.Tests   # 28 (контракт JWT/эндпоинты, мок-authority)
dotnet test src/portal/tests/Portal.Tests           # 20 (орг-изоляция портала, мок Admin REST)

# интеграционные тесты живого окружения (сами skip, если не поднято; tests/README.md):
set -a; . ./.env; set +a
dotnet test tests/Identity.IntegrationTests

# запуск сервисов:
cd examples/demo-api && dotnet run --urls http://127.0.0.1:10010
cd src/portal && PORTAL_CLIENT_SECRET=… dotnet run --urls http://127.0.0.1:10030
cd examples/demopage && dotnet run --urls http://127.0.0.1:10040
```

Полная инструкция — README.md, тестов — tests/README.md.

## Git

- Репозиторий пока только локальный; push и слияние в `main` — только по явному решению.

## CHANGELOG

Если в процессе реализации плана возникают какие то изменения, всегда фиксируй их в `docs/CHANGELOG.md`. Добавляй в конец файла данные о время | задаче которую делал | изменения, которые внес для реализации | кратко почему это потребовалось

# IdentityService

Общий сервис аутентификации экосистемы rulink. Единая формулировка стека (фаза 1):

- **Keycloak 26.x — IdP**: аутентификация, JWT, самообслуживание пароля, организации/группы/роли,
  realm `rulink`; внешний PostgreSQL; кастомного Java-кода нет — только штатные мапперы клеймов.
- **.NET 10 (ASP.NET Core)** — потребители и админ-портал: `src/portal` (портал администратора поверх
  Keycloak Admin REST API), `examples/demo-api` + `examples/demopage` (живые образцы интеграции).

Контракт JWT для потребителей — `docs/contracts/jwt-claims.md` (эталон); инструкции:
`docs/integrations/dotnet-oauth.md` (P2, `[Authorize]`/роли), `docs/integrations/group-data.md`
(P3, групповые данные). План фазы и задачи — `docs/Plan.md`, `docs/tasks/`.

## Запуск (dev, из чистого клона)

Порты: Keycloak `127.0.0.1:10020`, demo-api `10010`, портал `10030`, demopage `10040`.

```bash
cp .envexample .env                    # заполнить значения (секреты — только здесь)

# 1) Keycloak + realm rulink (task-01/02)
cd deploy
docker compose --env-file ../.env up -d
./init-realm.sh                        # идемпотентный bootstrap: realm, клиенты, организации, 5+1 пользователей
./verify.sh                            # (опционально) ручная сверка JWT с контрактом

# 2) портал администратора (task-07; отдельный терминал)
cd ../src/portal
set -a; . ../../.env; set +a           # PORTAL_CLIENT_SECRET, INIT_DEMO_PASSWORD
dotnet run --urls http://127.0.0.1:10030     # → http://127.0.0.1:10030

# 3) демо-ресурсы (опционально)
cd examples/demo-api && dotnet run --urls http://127.0.0.1:10010   # resource-server (P2/P3)
cd examples/demopage  && dotnet run --urls http://127.0.0.1:10040  # OIDC-сайт (задание 2 task-04)
```

Вход: администратор организации — `alice@orga.example.com` (orga) / `dave@orgb.example.com` (orgb);
суперадмин — `root@rulink.example`; пользователи — `bob@`, `carol@`, `erin@`. Пароли —
`INIT_DEMO_PASSWORD` из `.env` (не хардкод, не в репо).

## Тесты

```bash
set -a; . ./.env; set +a
dotnet test examples/demo-api/tests/DemoApi.Tests       # 28: контракт/эндпоинты demo-api (мок-authority)
dotnet test src/portal/tests/Portal.Tests               # 20: орг-изоляция/валидации портала (мок Admin REST)
dotnet test tests/Identity.IntegrationTests             # 10: живое окружение (без Keycloak — сами skip)
```

Подробности — `tests/README.md` (запуск из чистого состояния), `AGENTS.md` (сборка/тесты).

## Конфигурация / безопасность

- PostgreSQL — внешний сервер (доступы `KC_DB_*` в `.env`; compose БД не поднимает).
- Секреты — только `.env` (в `.gitignore`): `KC_BOOTSTRAP_ADMIN_*`, `KC_DB_*`, `PORTAL_CLIENT_SECRET`,
  `INIT_DEMO_PASSWORD`, `SMTP_*`.
- Realm `rulink`: brute force detection (5 неудач → блокировка), password policy ≥ 8, email-as-username,
  forgot password (SMTP), direct access grants — только для dev-тестов.
- Публикация в Интернет (nginx reverse-proxy) — отдельная будущая фаза; сейчас только `127.0.0.1`.

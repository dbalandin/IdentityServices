# План IdentityService — фаза 1 (P1–P4)

- **Дата:** 2026-10-26
- **Источник:** docs/PRD.md (Draft 2026-10-25) + интервью с владельцем
- **Статус:** фаза 1 (P1–P4) реализована — задачи task-01…09 закрыты (см. docs/tasks/ и CHANGELOG); живые эксплуатационные прогоны на хосте владельца
- **Целевое место в репо:** `docs/plans/2026-10-26-identity-service-phase1.md` (перенести при старте работ — см. задачу 0)

## 0. Перенос плана (первый шаг исполнителя)

- Скопировать этот файл в `docs/plans/2026-10-26-identity-service-phase1.md`.

## 1. Контекст

Экосистеме rulink нужен общий IdP: аутентификация/авторизация для web ui и api, самообслуживание пароля, мультитенантность через организации, группы, роли `user`/`admin`/`auditor`, scope сервисов. Потребители — .NET 10 (ASP.NET Core) сервисы. Репозиторий сейчас пуст (deploy/, src/, tests/), есть PRD и .envexample.

## 2. Принятые решения (согласованы с владельцем)

| # | Вопрос | Решение |
|---|--------|---------|
| 1 | Ядро сервиса | **Keycloak — IdP** (аутентификация, JWT, самообслуживание). Кастомный код — только админ-портал на **.NET 10** поверх Admin REST API. README/AGENTS привести к единой формулировке |
| 2 | Организации | **Keycloak Organizations** (KC 26+), **один realm**. У пользователя ровно одна организация |
| 3 | Роли и scope | `user`/`admin`/`auditor` — **realm roles**; scope сервисов — **атрибуты + штатные mappers**, без кастомного Java-кода |
| 4 | Админ-портал | **Свой ASP.NET 10 портал** (Web API + минимальный Web UI) поверх Keycloak Admin REST API; admin видит только свою организацию; суперадмин управляет организациями и их scope |
| 5 | Масштаб фазы 1 | **Без HA**: один инстанс Keycloak + внешний PostgreSQL. 1 млн пользователей / кластер / нагрузочное тестирование — отдельная будущая фаза |
| 6 | Passwordless | **Вне скоупа** (Non-Goal), как и LDAP и кастомная тема логина |
| 7 | Идентификация | **username = email**, email обязателен; сброс пароля — штатный forgot-password через **SMTP** (доступы в `.env`) |
| 8 | PostgreSQL | **Внешний сервер** (адрес/доступы в `.env`, как в `.envexample`); в compose только keycloak и портал |
| 9 | JWT контракт | Стандартный набор клеймов (см. §4); эталон — в `docs/contracts/` |
| 10 | Bootstrap realm | **Init-скрипт** в `deploy/` (kcadm/realm-import): воспроизводимое создание realm, ролей, организаций, групп, суперадмина, клиентов |
| 11 | Валидация P2/P3 | **Demo-сервис .NET 10 в репо** (`examples/demo-api`) — живая проверка инструкций и контракта JWT |

## 3. Границы фазы

**Включено (фазы PRD):**
- P1 — схема пользователя (организация, группы, роли, scope) в Keycloak + корректный JWT
- P2 — инструкция подключения OAuth/OIDC к .NET 10 сервисам (`[Authorize]`, `[Authorize(Roles=...)]`)
- P3 — инструкция по групповым данным в сервисах
- P4 — админ-портал: API + минимальный Web UI

**Не включено (Non-Goals, отразить в PRD):**
- passwordless/passkeys, LDAP-федерация
- HA-кластер, репликация PostgreSQL, нагрузочное тестирование на 1 млн
- кастомные Java SPI/mappers, кастомная тема логина (дефолтная тема KC)
- публикация в Интернет через nginx reverse-proxy (только локальный 127.0.0.1:10020)

## 4. Целевая архитектура

### Компоненты

- **Keycloak 26.x** (один контейнер, docker-compose, порт `127.0.0.1:10020`), realm `rulink`, внешний PostgreSQL.
- **`src/portal`** — ASP.NET 10: confidential OIDC-клиент (логин администраторов через Keycloak), сервис-аккаунт для вызова Admin REST API. Изоляция по организации — в логике портала.
- **`examples/demo-api`** — .NET 10 web api: OIDC-валидация JWT, endpoints с `[Authorize]`, `[Authorize(Roles = "admin")]`, групповые данные.
- **`deploy/`** — docker-compose, `.env`/`.envexample`, init-скрипт realm.

### Модель данных Keycloak

- **Organizations**: по одной на клиента-компанию. Атрибут организации `availableServices` — список доступных сервисов (базовый: `cryptoservice`, `cadastralservice`, `cadastralanalyticservice`). Задаёт суперадмин.
- **Пользователь**: обязателен email (= username); атрибут `services` — **подмножество** `availableServices` его организации; членство в группах организации; realm role.
- **Группы**: создаются и линкуются внутри организации; администратор управляет только группами своей организации.
- **Роли (realm, фиксированные)**: `user`, `admin`, `auditor`. Никто, кроме суперадмина, не создаёт роли.

### Контракт JWT (эталон — `docs/contracts/jwt-claims.md`)

| Claim | Тип | Источник |
|-------|-----|----------|
| `sub`, `email`, `email_verified`, `preferred_username` | стандарт | KC |
| `organization` | string (имя организации) | встроенный organization mapper |
| `groups` | array of string (имена групп) | group membership mapper |
| `realm_access.roles` | array (вкл. `user`/`admin`/`auditor`) | стандарт |
| `scope` | string, имена сервисов через пробел | user attribute `services` → mapper |

Подпись RS256; токены: access (JWT), refresh, id_token. Для сервис-сервис — client_credentials.

## 5. Задачи (порядок выполнения)

1. **deploy/ инфраструктура**
   - `docker-compose.yml`: keycloak 26.x (конкретный тег зафиксировать, ≥26.1 для Organizations+атрибутов), `127.0.0.1:10020:8080`, БД из `.env`, healthcheck.
   - Расширить `.envexample`: `KC_BOOTSTRAP_ADMIN_*`, `KC_DB_*` (из существующих `DBSERVER*`), `SMTP_HOST/PORT/USER/PASSWORD/FROM`, `PORTAL_CLIENT_SECRET`, `DEMO_CLIENT_SECRET`. Секреты — только в `.env`, в compose только `${VAR}`; volume — только через mount volume.
2. **deploy/init — bootstrap realm** (идемпотентный скрипт kcadm или realm import при старте)
   - realm `rulink`; realm roles `user`/`admin`/`auditor`; клиенты: `portal` (confidential), `demo-api` (public, для инструкций);
   - 2 организации (для exit criteria P1): атрибут `availableServices`; группы; пользователи: 3 в org A, 2 в org B (демо-пароли — из env init-скрипта, не хардкод в репо);
   - суперадмин (realm-admin, вне организаций); мапперы клеймов из §4; brute force detection ON; password policy (мин. 8 символов); forgot password + SMTP; email as username ON.
3. **docs/contracts/jwt-claims.md** — эталонный пример JWT (все claims из §4) для потребителей.
4. **examples/demo-api** — .NET 10 minimal api: OIDC (JWT bearer) против realm; endpoints: анонимный, `[Authorize]`, `[Authorize(Roles="admin")]`, `[Authorize(Roles="auditor")]`, групповой ресурс (данные видны всем членам группы).
5. **docs/integrations/dotnet-oauth.md** (P2) — пошаговая инструкция: пакеты, `AddJwtBearer`/OIDC, authority/audience, роли из `realm_access.roles`, logout, refresh. Проверена на demo-api.
6. **docs/integrations/group-data.md** (P3) — инструкция: чтение `groups`/`organization`/`scope` клеймов, паттерн «данные доступны всем пользователям группы» (+ схема расширения БД сервиса, пример фильтра).
7. **src/portal** (P4) — ASP.NET 10:
   - Auth: OIDC-логин администратора; сервис-аккаунт для Admin REST;
   - API: CRUD пользователей своей организации (создание с email, назначение групп/ролей/`services ⊆ availableServices`), CRUD групп своей организации; смена/сброс пароля пользователя (required action);
   - суперадмин: CRUD организаций, управление `availableServices` организации;
   - минимальный Web UI (список пользователей/групп, формы создания);
   - запрет: создание ролей, создание элементов scope (список сервисов фиксирован константами портала, валидация `⊆ availableServices`).
8. **tests/** — автотест контракта JWT: токен seeded-пользователя (dev direct access grants), сверка claims с эталоном; тест орг-изоляции портала (admin A не видит org B).
9. **Документация**: README (единый стек: Keycloak IdP + .NET портал, запуск, порты), AGENTS.md (заполнить «Сборка и тесты»), PRD (Non-Goals из §3; exit criteria P1: заменить хардкод `qwe123` на «демо-пароли из env»).

## 6. Безопасность

- Brute force detection: блокировка после 5 неудач, нарастающее ожидание.
- Ответ при неверном логине/пароле — единая ошибка KC (не раскрывает существование username). Проверить ветку forgot-password на энумерацию username (валидация в §7).
- Password policy ≥ 8 символов; демо-пароли — из env init-скрипта.
- Секреты только в `.env`; не логировать пароли, токены, email (правило AGENTS.md).
- Портал: операции Admin REST — от сервис-аккаунта портала; org-изоляция и проверки ролей — в логике портала (единственное место инварианта «admin только своей организации» — покрыть тестами).

## 7. Валидация (критерии готовности)

Итог закрытия фазы 1 (2026-10-28, детали — CHANGELOG и задачи task-01…09):

- ✅ **P1:** из чистого состояния: `docker compose up` + init → 2 организации, 3+2 пользователя, вход по email; JWT сверен с эталоном `docs/contracts/jwt-claims.md` (автотест из задачи 8).
  *Реализовано: init-скрипт идемпотентен (task-02), 31-проверочная ручная сверка verify.sh + автотест `tests/Identity.IntegrationTests/JwtContractTests` (task-08); живой прогон compose — остаточная проверка владельца (docker-сокет в среде агента).*
- ✅ **P2:** demo-api запускается по инструкции P2 без дополнительных шагов; `[Authorize]` и роли работают.
  *Инструкция docs/integrations/dotnet-oauth.md (task-05), каждый шаг воспроизведён на demo-api: suite 28/28 (мок-authority: 401/200/403-матрица, роли, подпись/iss/aud/exp).*
- ✅ **P3:** два пользователя одной группы видят групповой ресурс demo-api; пользователи другой группы/организации — 403.
  *Инструкция docs/integrations/group-data.md (task-06); bob+carol → 200, alice/dave/erin → 403 — тесты GroupData_* в DemoApi.Tests.*
- ✅ **P4:** через API портала создан пользователь в org A; admin org A получает 403 на пользователей org B; суперадмин создаёт организацию и меняет её `availableServices`; назначить пользователю сервис вне набора организации — 400.
  *Portal.Tests 20/20 (мок Admin REST) + PortalIsolationTests (живой прогон, task-08).*
- ✅ **Безопасность:** 5 неверных паролей → блокировка; неверный логин и несуществующий логин — неотличимы.
  *Настройки realm (task-02): bruteForceProtected, failureFactor=5, нарастающее ожидание; автотесты SecurityTests (task-08) — блокировка, единая ошибка+тайминг, forgot-password без энумерации.*

## 8. Риски и открытые вопросы

- **Версия Keycloak**: зафиксировать конкретный тег 26.x и проверить GA-статус атрибутов Organizations (нужен ≥26.1); fallback — `availableServices` в атрибуте группы-организации.
- **SMTP**: доступы выдать до валидации forgot-password (вопрос к DevOps, срок по PRD — 2026-10-30).
- **1 млн пользователей / 99% uptime** — осознанно вне фазы 1; зафиксировать в PRD отдельной фазой (HA, кластер, нагрузочное тестирование).
- **Портал в интернет** — после nginx reverse-proxy (вне фазы).

## 9. Ссылки

- PRD: `docs/PRD.md` (обновить по задаче §5.9)
- Краткая задача исполнителю: `docs/tasks/task-01.md` (создаётся задачей 0)
- Эталон контракта: `docs/contracts/jwt-claims.md` (создаётся задачей 3)

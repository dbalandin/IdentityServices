# Task 02: deploy/init — идемпотентный bootstrap realm `rulink`

- **Статус:** in progress (скрипт готов, свера verify.sh отлажена на моке; живой прогон — владельцем, см. task-01 «Проверка»)
- **Фаза:** PRD P1
- **Зависимости:** task-01 (docker-compose + .envexample)
- **Источник:** план §4 (модель данных), §5.2, §6; PRD FR 1–3, P1 exit criteria

## Цель

Воспроизводимая инициализация realm `rulink` с организациями, ролями, группами, пользователями и мапперами клеймов — из чистого состояния одним запуском, без ручных кликов в консоли.

## Что сделать

Реализовать в `deploy/init/` идемпотентный скрипт (kcadm через `docker compose exec`, либо realm-import JSON при старте — выбрать один способ, повторный запуск не должен ломать существующее):

1. **Realm**: `rulink`.
2. **Настройки realm**:
   - email as username ON (username = email);
   - brute force detection ON (блокировка после 5 неудач, нарастающее ожидание — параметры из плана §6);
   - password policy: минимум 8 символов;
   - forgot password ON + SMTP из `.env`;
   - direct access grants ON только для dev/тестов (токен для автотеста task-08).
3. **Realm roles** (фиксированные, никто кроме суперадмина не создаёт роли): `user`, `admin`, `auditor`.
4. **Клиенты**:
   - `portal` — confidential (секрет `PORTAL_CLIENT_SECRET`), сервис-аккаунт для Admin REST;
   - `demo-api` — public (для инструкций и примеров).
5. **Организации** (Keycloak Organizations, 2 шт. для exit criteria P1):
   - org A и org B, атрибут `availableServices` (базовый: `cryptoservice`, `cadastralservice`, `cadastralanalyticservice`);
   - группы внутри каждой организации.
6. **Пользователи**: 3 в org A, 2 в org B. Демо-пароль — из env (`INIT_DEMO_PASSWORD`), не хардкод в репо. Каждому: email, роль, группы, атрибут `services` ⊆ `availableServices` организации.
7. **Суперадмин**: realm-admin (например, `realm-management` роли), вне организаций.
8. **Мапперы клеймов** (штатные, без Java-кода), чтобы access token соответствовал контракту §4 плана / task-03:
   - `organization` — встроенный organization mapper;
   - `groups` — group membership mapper;
   - `realm_access.roles` — стандарт;
   - `scope` — user attribute `services` → mapper.
9. Зафиксировать изменение в `docs/CHANGELOG.md`.

## Критерии готовности

- [ ] Из чистого состояния: `docker compose up -d` + init-скрипт → realm `rulink` со всем перечисленным — **живой прогон владельца**
- [ ] Повторный запуск init идемпотентен (не создаёт дубликаты, не падает) — живой прогон (`deploy/init-realm.sh` дважды)
- [ ] 5 пользователей: 3 в org A, 2 в org B, вход по email — живой прогон
- [ ] JWT пользователя содержит все клеймы контракта — живой прогон через `deploy/verify.sh` (логика сверы отлажена: мок-сервер, 31/31 PASS, негатив ловится; автотест — task-08)
- [x] Пароли пользователей не в репо, только env (`INIT_DEMO_PASSWORD`; grep по репо — чисто)
- [x] Запись в `docs/CHANGELOG.md`

## Проверка (живой прогон)

```bash
cd deploy
docker compose --env-file ../.env up -d
./init-realm.sh          # ждёт healthcheck → bootstrap realm rulink внутри контейнера
./init-realm.sh          # повторно — проверка идемпотентности (дубликатов быть не должно)
./verify.sh              # сверка JWT alice с контрактом (31 проверка, jwt-claims.md §8)
./verify.sh bob@orga.example.com   # и остальные: carol@orga…, dave@orgb…, erin@orgb…
```

## Ссылки

- план §4 «Модель данных Keycloak», §4 «Контракт JWT»
- docs/tasks/task-03.md — эталон JWT (контракт)
- docs/tasks/task-08.md — автотест контракта

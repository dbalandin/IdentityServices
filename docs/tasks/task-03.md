# Task 03: docs/contracts/jwt-claims.md — эталон контракта JWT

- **Статус:** in progress — документ готов (контракт v1.0); сверка живого токена — вручную в task-02 и автотестом task-08 (окружение поднимается там)
- **Фаза:** PRD P1
- **Зависимости:** task-02 (мапперы настроены — для сверки живого токена; документ можно писать параллельно)
- **Источник:** план §4 «Контракт JWT», §5.3

## Цель

Зафиксировать эталонный формат access token для всех потребителей экосистемы (.NET сервисы), чтобы интеграции писались против контракта, а не против конкретной инсталляции Keycloak.

## Что сделать

Создать `docs/contracts/jwt-claims.md`:

1. Полный пример access token (payload с реальными значениями seeded-пользователя из task-02, без раскрытия секретов).
2. Таблица клеймов — тип и источник каждого:

| Claim | Тип | Источник |
|-------|-----|----------|
| `sub`, `email`, `email_verified`, `preferred_username` | стандарт | Keycloak |
| `organization` | string (имя организации) | organization mapper |
| `groups` | array of string (имена групп) | group membership mapper |
| `realm_access.roles` | array (вкл. `user`/`admin`/`auditor`) | стандарт |
| `scope` | string, имена сервисов через пробел | user attribute `services` → mapper |

3. Описание формата: подпись RS256 (где взять JWKS/public key), lifetime токенов (access / refresh / id_token).
4. Для сервис-сервис аутентификации: client_credentials (какой клиент, какие клеймы).
5. Пример чтения клеймов на .NET (`ClaimsPrincipal`: `realm_access.roles` → role claim transformation, `groups`, `organization`, `scope`).
6. Зафиксировать изменение в `docs/CHANGELOG.md`.

## Критерии готовности

- [x] Файл `docs/contracts/jwt-claims.md` существует
- [x] Покрыты все клеймы из таблицы выше + подпись, lifetime, client_credentials
- [ ] Реальный токен seeded-пользователя совпадает с эталоном по составу клеймов — *проверяется после поднятия окружения task-01/02 (правила сверки зафиксированы в §8 контракта; автотест — task-08)*
- [x] Есть пример чтения клеймов на .NET
- [x] Запись в `docs/CHANGELOG.md`

## Ссылки

- план §4 «Контракт JWT»
- docs/tasks/task-02.md — мапперы, которые формируют клеймы
- docs/tasks/task-08.md — автотест сверки токена с этим эталоном

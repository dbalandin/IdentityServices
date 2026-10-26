# Задачи фазы 1 IdentityService

Источник: `docs/plans/2026-10-26-identity-service-phase1.md` §5 (до переноса — `docs/Plan.md`).
Статусы: `open` / `in progress` / `done`. Отметки DoD ставить по факту проверки.

## Порядок и зависимости

| # | Файл | Задача | Фаза PRD | Зависимости |
|---|------|--------|----------|-------------|
| 00 | [task-00.md](task-00.md) | Перенос плана в `docs/plans/` | подготовка | — |
| 01 | [task-01.md](task-01.md) | `deploy/`: docker-compose Keycloak + внешний PostgreSQL | P1 | 00 |
| 02 | [task-02.md](task-02.md) | `deploy/init`: идемпотентный bootstrap realm `rulink` | P1 | 01 |
| 03 | [task-03.md](task-03.md) | `docs/contracts/jwt-claims.md`: эталон JWT | P1 | 02 (параллельно) |
| 04 | [task-04.md](task-04.md) | `examples/demo-api`: .NET 10 демо-сервис | P1/P3 | 02, 03 |
| 05 | [task-05.md](task-05.md) | `docs/integrations/dotnet-oauth.md`: инструкция OIDC для .NET 10 | P2 | 04 |
| 06 | [task-06.md](task-06.md) | `docs/integrations/group-data.md`: групповые данные в сервисах | P3 | 04 |
| 07 | [task-07.md](task-07.md) | `src/portal`: ASP.NET 10 портал администратора | P4 | 02 |
| 08 | [task-08.md](task-08.md) | `tests/`: контракт JWT, орг-изоляция, безопасность | P1/P4 | 02, 03, 04, 07 |
| 09 | [task-09.md](task-09.md) | Синхронизация README / AGENTS / PRD | закрытие | 01–08 |

Ветки 05/06 (инструкции) и 07 (портал) можно вести параллельно после task-02/04.

## Правила

- Каждая задача фиксирует изменения в `docs/CHANGELOG.md` (правило AGENTS.md).
- Секреты — только `.env`; демо-пароли — из env init-скрипта.
- Не менять названия папок проекта без решения владельца (AGENTS.md).

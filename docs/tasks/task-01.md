# Task 01: deploy/ — docker-compose для Keycloak + PostgreSQL

- **Статус:** in progress (артефакты готовы и проверены статически; живой прогон docker — на стороне владельца, см. «Проверка» ниже)
- **Фаза:** PRD P1
- **Зависимости:** task-00 (перенос плана)
- **Источник:** план §4 (архитектура), §5.1, §6 (безопасность)

## Цель

Воспроизводимый локальный запуск Keycloak 26.x с внешним PostgreSQL через docker compose, все секреты — в `.env`.

## Что сделать

1. `deploy/docker-compose.yml`:
   - сервис `keycloak` на образе **quay.io/keycloak/keycloak:26.x** — зафиксировать конкретный тег (≥26.1, требование для GA-статуса Organizations и их атрибутов; проверить при выборе, см. риск в плане §8);
   - проброс порта только на localhost: `127.0.0.1:10020:8080`;
   - БД — внешний PostgreSQL (в compose только keycloak, БД не поднимаем), параметры из `.env`;
   - healthcheck (например, `/health` или `kcadm get realms/rulink`);
   - `command: start` (не `start-dev`) — окружение dev/test, но конфигурация близка к прод-режиму; режим старта согласовать с владельцем.
2. Расширить `.envexample` (значения-заглушки, без реальных секретов):
   - `KC_BOOTSTRAP_ADMIN_USERNAME`, `KC_BOOTSTRAP_ADMIN_PASSWORD`
   - `KC_DB`, `KC_DB_URL_HOST`, `KC_DB_URL_PORT`, `KC_DB_URL_DATABASE`, `KC_DB_USERNAME`, `KC_DB_PASSWORD` (источник — существующие `DBSERVER*` переменные)
   - `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM`
   - `PORTAL_CLIENT_SECRET`, `DEMO_CLIENT_SECRET` (для task-02)
   - `INIT_DEMO_PASSWORD` (демо-пароль seeded-пользователей для task-02)
3. Правила (из AGENTS.md):
   - в compose только `${VAR}` подстановки, никаких секретов в репо;
   - volume — только через mount volume (именованные или bind);
   - `.env` в `.gitignore` (уже есть — проверить).
4. Зафиксировать изменение в `docs/CHANGELOG.md`.

## Критерии готовности

- [ ] `docker compose up -d` из `deploy/` поднимает keycloak, healthcheck становится healthy — **живой прогон владельца** (у агента нет доступа к docker-сокету)
- [ ] Keycloak доступен на `http://127.0.0.1:10020`, админ-вход по `KC_BOOTSTRAP_ADMIN_*` — живой прогон
- [x] Тег Keycloak зафиксирован (не `latest`), версия ≥26.1 — `quay.io/keycloak/keycloak:26.7.5`
- [x] `.envexample` покрывает все переменные compose/init, реальных секретов в репо нет — кросс-чек переменных скриптом + grep по репо
- [x] Запись в `docs/CHANGELOG.md`

## Проверка (живой прогон)

```bash
cd deploy
cp ../.envexample ../.env   # заполнить реальными значениями (внешняя PG и пр.)
docker compose --env-file ../.env up -d
docker ps   # identityservices-keycloak: (healthy)
# админ-консоль: http://127.0.0.1:10020  (KC_BOOTSTRAP_ADMIN_* из .env)
```

## Ссылки

- план §4 «Целевая архитектура», §8 риски (версия Keycloak)
- docs/tasks/task-02.md — bootstrap realm поверх этой инфраструктуры

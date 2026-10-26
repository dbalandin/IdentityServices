#!/usr/bin/env bash
#
# Запуск идемпотентного bootstrap realm `rulink` (task-02).
# Снаружи контейнера: ждёт healthcheck keycloak и выполняет
# deploy/init/realm-bootstrap.sh внутри контейнера (env уже в контейнере —
# их передаёт docker-compose из .env).
#
# Использование (из любого каталога):
#   deploy/init-realm.sh
#
# Перед первым запуском: cp .envexample .env && заполнить, затем
#   docker compose --env-file ../.env up -d   (из deploy/)
set -euo pipefail

cd "$(dirname "$0")"

ENV_FILE=../.env
CONTAINER=identityservices-keycloak

log() { printf '[init-realm] %s\n' "$*"; }
die() { printf '[init-realm] ОШИБКА: %s\n' "$*" >&2; exit 1; }

command -v docker >/dev/null 2>&1 || die "docker не найден"
[ -f "$ENV_FILE" ] || die "нет $ENV_FILE — скопируйте .envexample в .env и заполните"

if ! docker inspect "$CONTAINER" >/dev/null 2>&1; then
  die "контейнер $CONTAINER не запущен. Из deploy/: docker compose --env-file ../.env up -d"
fi

log "жду healthcheck $CONTAINER (до 5 минут)…"
i=0
until [ "$(docker inspect -f '{{.State.Health.Status}}' "$CONTAINER" 2>/dev/null || true)" = "healthy" ]; do
  i=$((i + 1))
  [ "$i" -ge 60 ] && die "контейнер не стал healthy за ~5 минут (см. docker logs $CONTAINER)"
  sleep 5
done
log "контейнер healthy — запускаю bootstrap"

docker exec "$CONTAINER" bash /keycloak-init/realm-bootstrap.sh

log "готово. Сверка JWT с контрактом: deploy/verify.sh [username]"

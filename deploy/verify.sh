#!/usr/bin/env bash
#
# Ручная сверка access token с контрактом docs/contracts/jwt-claims.md (§8).
# Получает токен по password grant (клиент demo-api, direct access grants —
# dev/test), декодирует payload и проверяет состав клеймов по эталону §4.
#
# Использование (из любого каталога):
#   deploy/verify.sh [username]     # по умолчанию alice@orga.example.com
#
# Пароль — INIT_DEMO_PASSWORD из ../.env (сами пароли/токены в вывод не печатаем).
set -euo pipefail

cd "$(dirname "$0")"

ENV_FILE=../.env
BASE_URL="${KEYCLOAK_URL:-http://127.0.0.1:10020}"
REALM=rulink
TOKEN_URL="$BASE_URL/realms/$REALM/protocol/openid-connect/token"
DEFAULT_USER=alice@orga.example.com
ALL_SERVICES="cryptoservice cadastralservice cadastralanalyticservice"

log()  { printf '[verify] %s\n' "$*"; }
fail() { printf '[verify] FAIL: %s\n' "$*" >&2; FAILED=1; }
die()  { printf '[verify] ОШИБКА: %s\n' "$*" >&2; exit 2; }

FAILED=0
command -v curl >/dev/null 2>&1 || die "curl не найден"
command -v jq >/dev/null 2>&1 || die "jq не найден"
command -v python3 >/dev/null 2>&1 || die "python3 не найден"
[ -f "$ENV_FILE" ] || die "нет $ENV_FILE — скопируйте .envexample в .env и заполните"

# .env — доверенный локальный файл (без source нельзя получить значения с = и пробелами)
set -a
# shellcheck disable=SC1091
. "$ENV_FILE"
set +a

: "${INIT_DEMO_PASSWORD:?нужна переменная INIT_DEMO_PASSWORD в .env}"
USER_NAME="${1:-$DEFAULT_USER}"

# ---------- эталонные ожидания (контракт JWT §4) ----------
declare -A E_ORG E_ROLES E_GROUPS E_SERVICES
E_ORG[alice@orga.example.com]=orga
E_ORG[bob@orga.example.com]=orga
E_ORG[carol@orga.example.com]=orga
E_ORG[dave@orgb.example.com]=orgb
E_ORG[erin@orgb.example.com]=orgb
E_ROLES[alice@orga.example.com]="user admin"
E_ROLES[bob@orga.example.com]="user"
E_ROLES[carol@orga.example.com]="user auditor"
E_ROLES[dave@orgb.example.com]="user admin"
E_ROLES[erin@orgb.example.com]="user"
E_GROUPS[alice@orga.example.com]="orga-managers"
E_GROUPS[bob@orga.example.com]="orga-team-a"
E_GROUPS[carol@orga.example.com]="orga-team-a"
E_GROUPS[dave@orgb.example.com]="orgb-managers"
E_GROUPS[erin@orgb.example.com]="orgb-team-b"
E_SERVICES[alice@orga.example.com]="$ALL_SERVICES"
E_SERVICES[bob@orga.example.com]="cryptoservice cadastralservice"
E_SERVICES[carol@orga.example.com]="cadastralanalyticservice"
E_SERVICES[dave@orgb.example.com]="$ALL_SERVICES"
E_SERVICES[erin@orgb.example.com]="cryptoservice"

[ -n "${E_ORG[$USER_NAME]:-}" ] || die "$USER_NAME — не seeded-пользователь контракта §4"

# ---------- получение токена ----------
log "token endpoint: $TOKEN_URL (пользователь: $USER_NAME, клиент: demo-api)"
resp=$(curl -fsS -X POST "$TOKEN_URL" \
  -d grant_type=password -d client_id=demo-api \
  --data-urlencode "username=$USER_NAME" \
  --data-urlencode "password=$INIT_DEMO_PASSWORD" 2>&1) \
  || die "запрос токена не удался: $resp (realm инициализирован? deploy/init-realm.sh)"

access_token=$(jq -er '.access_token // empty' <<<"$resp" 2>/dev/null) \
  || die "в ответе нет access_token: $(jq -r '.error_description // .error // "неизвестная ошибка"' <<<"$resp" 2>/dev/null)"

# ---------- декодирование (без печати сырого токена) ----------
decode_jwt_part() { # $1 = jwt, $2 = номер части (0=header, 1=payload)
  local part
  part=$(cut -d. -f"$(( $2 + 1 ))" <<<"$1")
  python3 -c 'import base64,json,sys; p=sys.argv[1]; p+="="*(-len(p)%4);
print(json.dumps(json.loads(base64.urlsafe_b64decode(p)),indent=2,ensure_ascii=False))' "$part"
}
header=$(decode_jwt_part "$access_token" 0)
payload=$(decode_jwt_part "$access_token" 1)

# ---------- §8: проверки ----------
jqp() { jq -r "$@" <<<"$payload"; }
jqh() { jq -r "$@" <<<"$header"; }

check() { # $1 = описание, $2 = результат (1/0 или true/false от jq)
  case "$2" in
    1|true) log "PASS: $1" ;;
    *) fail "$1" ;;
  esac
}

# 1. обязательный состав клеймов
for c in iss sub exp iat jti typ azp aud email email_verified preferred_username organization groups realm_access scope; do
  check "клейм $c присутствует" "$(jqp "has(\"$c\")")"
done

# 2. iss и preferred_username = email
check "iss = $BASE_URL/realms/$REALM" \
  "$([ "$(jqp .iss)" = "$BASE_URL/realms/$REALM" ] && echo 1 || echo 0)"
check "preferred_username = email" \
  "$([ "$(jqp .preferred_username)" = "$(jqp .email)" ] && echo 1 || echo 0)"

# 3. organization — строка с ожидаемым именем
check "organization — string" "$(jqp '.organization | type == "string"')"
check "organization = ${E_ORG[$USER_NAME]}" \
  "$([ "$(jqp .organization)" = "${E_ORG[$USER_NAME]}" ] && echo 1 || echo 0)"

# 4. groups — массив строк без '/', соответствует §4
check "groups — массив строк" "$(jqp '.groups | (type == "array") and all(.[]; type == "string")')"
check "groups без '/' в именах (full path OFF)" "$(jqp '.groups | all(.[]; contains("/") | not)')"
check "groups = ${E_GROUPS[$USER_NAME]}" \
  "$([ "$(jqp '.groups | sort | join(",")')" = "$(tr ' ' '\n' <<<"${E_GROUPS[$USER_NAME]}" | sort | paste -sd,)" ] && echo 1 || echo 0)"

# 5. realm_access.roles содержит роли пользователя (вхождение)
for r in ${E_ROLES[$USER_NAME]}; do
  check "realm_access.roles содержит $r" "$(jqp --arg r "$r" '.realm_access.roles | index($r) != null')"
done

# 6. scope: все сервисы пользователя присутствуют; сервисов вне availableServices нет
for s in ${E_SERVICES[$USER_NAME]}; do
  check "scope содержит $s" "$(jqp --arg s "$s" '.scope | split(" ") | index($s) != null')"
done
check "scope без сервисов вне availableServices" \
  "$(jqp --arg all "$ALL_SERVICES" '[.scope | split(" ")[] | select(. as $s | ($all | split(" ")) | index($s))] | length > 0')"
check "scope без посторонних значений сервис-типа" \
  "$(python3 -c 'import json,sys
scope=set(json.loads(sys.argv[1])["scope"].split())
known=set(sys.argv[2].split())
bad=scope-known-{"openid","email","profile"}
sys.exit(0 if not bad else 1)' "$payload" "$ALL_SERVICES" && echo 1 || echo 0)"

# 7. подпись RS256 + kid
check "заголовок: alg = RS256" "$([ "$(jqh .alg)" = RS256 ] && echo 1 || echo 0)"
check "заголовок: kid присутствует" "$(jqh 'has("kid")')"

# ---------- итог ----------
if [ "$FAILED" = 0 ]; then
  log "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ — токен соответствует контракту jwt-claims.md §8"
else
  log "ЕСТЬ ПРОВАЛЫ (см. FAIL выше)"
fi

printf '\n[verify] Декодированный payload (для ручного осмотра, сырой токен не печатаем):\n%s\n' "$payload"
exit "$FAILED"

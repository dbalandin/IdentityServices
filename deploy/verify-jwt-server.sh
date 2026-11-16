#!/usr/bin/env bash
# Разовая проверка JWT-контракта (jwt-claims.md §8) на сервере srv-05.
# Секреты (пароль, токен) в stdout не печатаются.
set -uo pipefail
ENV=/opt/services/rulink-compose/.env
BASE=${BASE_URL:-https://idp.rulink.io}
REALM=rulink
ISS=https://idp.rulink.io/realms/rulink
FAILED=0

PW=$(grep -E '^INIT_DEMO_PASSWORD=' "$ENV" | cut -d= -f2- | tr -d '\r')
[ -n "$PW" ] || { echo "FAIL: INIT_DEMO_PASSWORD не найден"; exit 1; }

check() { if [ "$2" = 1 ] || [ "$2" = true ]; then echo "PASS: $1"; else echo "FAIL: $1"; FAILED=1; fi; }

jwt_part() { # $1 = jwt, $2 = номер части (0=header,1=payload)
  python3 -c 'import base64,json,sys
p=sys.argv[1].split(".")[int(sys.argv[2])]; p+="="*(-len(p)%4)
print(json.dumps(json.loads(base64.urlsafe_b64decode(p))))' "$1" "$2"
}

verify_user() { # $1 user, $2 org, $3 roles, $4 groups, $5 services
  local user=$1 org=$2 roles=$3 groups=$4 services=$5
  local resp access payload header gexp
  echo "=== $user (org=$org) ==="
  resp=$(curl -fsS ${CURL_INSECURE:-} -X POST "$BASE/realms/$REALM/protocol/openid-connect/token" \
    -d grant_type=password -d client_id=demo-api \
    --data-urlencode "username=$user" --data-urlencode "password=$PW" 2>&1) \
    || { echo "FAIL: запрос токена ($user): $(head -c 200 <<<"$resp")"; FAILED=1; return; }
  access=$(jq -er '.access_token // empty' <<<"$resp" 2>/dev/null) \
    || { echo "FAIL: нет access_token ($user)"; FAILED=1; return; }
  payload=$(jwt_part "$access" 1); header=$(jwt_part "$access" 0)

  for c in iss sub exp iat jti typ azp aud email email_verified preferred_username organization groups realm_access scope services; do
    check "клейм $c присутствует" "$(jq "has(\"$c\")" <<<"$payload")"
  done
  check "iss = https://idp.rulink.io/realms/rulink" \
    "$([ "$(jq -r .iss <<<"$payload")" = "$ISS" ] && echo 1 || echo 0)"
  check "preferred_username = email" \
    "$([ "$(jq -r .preferred_username <<<"$payload")" = "$(jq -r .email <<<"$payload")" ] && echo 1 || echo 0)"
  check "organization = $org" "$([ "$(jq -r .organization <<<"$payload")" = "$org" ] && echo 1 || echo 0)"
  check "groups — строки без '/'" "$(jq 'all(.groups[]; type=="string" and (contains("/")|not))' <<<"$payload")"
  gexp=$(tr ' ' '\n' <<<"$groups" | sort | paste -sd,)
  check "groups = $gexp" "$([ "$(jq -r '.groups|sort|join(",")' <<<"$payload")" = "$gexp" ] && echo 1 || echo 0)"
  local r s
  for r in $roles; do
    check "realm role: $r" "$(jq --arg r "$r" '.realm_access.roles|index($r)!=null' <<<"$payload")"
  done
  for s in $services; do
    check "services: $s" "$(jq --arg s "$s" '.services|split(" ")|index($s)!=null' <<<"$payload")"
  done
  check "services без посторонних" \
    "$(python3 -c 'import json,sys
d=json.loads(sys.argv[1]); svcs=set(d["services"].split())
known={"cryptoservice","cadastralservice","cadastralanalyticservice"}
sys.exit(0 if svcs<=known and svcs else 1)' "$payload" && echo 1 || echo 0)"
  check "scope без сервисов (v1.1)" \
    "$(python3 -c 'import json,sys
scope=set(json.loads(sys.argv[1])["scope"].split())
sys.exit(0 if not any(s.endswith("service") for s in scope) else 1)' "$payload" && echo 1 || echo 0)"
  check "заголовок alg=RS256" "$([ "$(jq -r .alg <<<"$header")" = RS256 ] && echo 1 || echo 0)"
  check "заголовок kid" "$(jq 'has("kid")' <<<"$header")"
}

ALL_SVC="cryptoservice cadastralservice cadastralanalyticservice"
verify_user alice@orga.example.com orga "user admin" "orga-managers" "$ALL_SVC"
verify_user dave@orgb.example.com orgb "user admin" "orgb-managers" "$ALL_SVC"
verify_user erin@orgb.example.com orgb "user" "orgb-team-b" "cryptoservice"

echo
[ "$FAILED" = 0 ] && echo "ИТОГ: ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ" || echo "ИТОГ: ЕСТЬ ПРОВАЛЫ"
exit "$FAILED"

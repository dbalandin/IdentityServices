#!/usr/bin/env bash
#
# Идемпотентный bootstrap realm `rulink` (Keycloak 26.x).
# Запускается ВНУТРИ контейнера keycloak; с хоста — через deploy/init-realm.sh:
#   docker exec identityservices-keycloak bash /keycloak-init/realm-bootstrap.sh
#
# Свойства:
#   - повторный запуск безопасен: существующие сущности обновляются, дубликаты не создаются;
#   - все секреты — только из переменных окружения (в репо секретов нет);
#   - парсинг ответов — штатный `--format csv --noquotes` kcadm (jq в образе отсутствует;
#     CSV у kcadm без строки-заголовка: одна строка = один объект, поля через запятую).
#
# Архитектурные решения (подробно — docs/CHANGELOG.md, задача task-02):
#   - claim `organization` — СТРОКА (alias организации): у Organization Membership mapper
#     выставлен multivalued=false (OrganizationMembershipMapper.resolveValue -> alias);
#   - группы — обычные realm-группы с уникальными именами `<org-alias>-*`, а НЕ
#     organization-typed группы: organization-группы штатно попадают только внутрь
#     object-claim `organization` (organization.<alias>.groups), а стандартный
#     group-membership mapper на них даёт мусор в виде внутренней группы-UUID;
#     плоский claim `groups` по контракту JWT (план §4) важнее типизации групп.
#     Принадлежность группы организации — префиксом имени + в логике портала (P4).

set -euo pipefail

REALM=rulink
SERVER_URL="${KEYCLOAK_URL:-http://127.0.0.1:8080}"
KCADM="${KCADM:-/opt/keycloak/bin/kcadm.sh}"

# log — в stderr: stdout внутри подстановок команд (напр. org_a=$(ensure_org …))
# должен содержать ТОЛЬКО идентификаторы, иначе id "загрязняется" логами
log() { printf '[init][%s] %s\n' "$(date +%H:%M:%S)" "$*" >&2; }
die() { printf '[init] ОШИБКА: %s\n' "$*" >&2; exit 1; }

# ---------- проверки окружения ----------
: "${KC_BOOTSTRAP_ADMIN_USERNAME:?нужна переменная KC_BOOTSTRAP_ADMIN_USERNAME}"
: "${KC_BOOTSTRAP_ADMIN_PASSWORD:?нужна переменная KC_BOOTSTRAP_ADMIN_PASSWORD}"
: "${INIT_DEMO_PASSWORD:?нужна переменная INIT_DEMO_PASSWORD (демо-пароль seeded-пользователей)}"
: "${PORTAL_CLIENT_SECRET:?нужна переменная PORTAL_CLIENT_SECRET}"
[ "${#INIT_DEMO_PASSWORD}" -ge 8 ] || die "INIT_DEMO_PASSWORD короче 8 символов (password policy realm)"
[ -n "${SMTP_HOST:-}" ] || log "ПРЕДУПРЕЖДЕНИЕ: SMTP_HOST пуст — forgot password работать не будет"

# ---------- утилиты ----------
json_escape() { local s=$1; s=${s//\\/\\\\}; s=${s//\"/\\\"}; printf '%s' "$s"; }

# из stdin/аргумента с csv-выводом kcadm взять первый id (uuid)
id_first() { grep -oE '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' | head -n1 || true; }

# stdin: csv "id,<поле>"; $1 — точное значение поля, $2 — номер поля (по умолчанию 2)
id_by_field() { awk -F, -v v="$1" -v n="${2:-2}" '$n==v {print $1; exit}' || true; }

kc() { "$KCADM" "$@"; }

# ---------- вход в master (сервер может ещё подниматься) ----------
log "подключение к $SERVER_URL (realm master)"
i=1
until kc config credentials --server "$SERVER_URL" --realm master \
      --user "$KC_BOOTSTRAP_ADMIN_USERNAME" --password "$KC_BOOTSTRAP_ADMIN_PASSWORD" >/dev/null 2>&1; do
  [ "$i" -ge 30 ] && die "не удалось аутентифицироваться в master за 30 попыток (сервер не готов?)"
  i=$((i + 1)); sleep 2
done
log "успешный вход в master"

# ---------- 1. realm ----------
if kc get "realms/$REALM" --fields id --format csv --noquotes 2>/dev/null | id_first | grep -q .; then
  log "realm $REALM существует — обновляю настройки"
else
  log "создаю realm $REALM"
  kc create realms -s "realm=$REALM" -s enabled=true -s organizationsEnabled=true -s "displayName=RuLink"
fi

# SMTP (realm-level)
SMTP_JSON='{"host":"'"$(json_escape "${SMTP_HOST:-}")"'","port":"'"${SMTP_PORT:-587}"'","from":"'"$(json_escape "${SMTP_FROM:-}")"'"'
if [ -n "${SMTP_USER:-}" ]; then
  SMTP_JSON+=',"auth":"true","user":"'"$(json_escape "$SMTP_USER")"'","password":"'"$(json_escape "${SMTP_PASSWORD:-}")"'"'
else
  SMTP_JSON+=',"auth":"false"'
fi
SMTP_JSON+=',"starttls":"'"${SMTP_STARTTLS:-false}"'"}'

# brute force: блокировка после 5 неудач, нарастающее ожидание (план §6);
# password policy: минимум 8 символов; email as username; forgot password ON;
# direct access grants ON — ТОЛЬКО для dev/тестов (токен для автотеста task-08).
kc update "realms/$REALM" \
  -s loginWithEmailAllowed=true \
  -s registrationEmailAsUsername=true \
  -s duplicateEmailsAllowed=false \
  -s organizationsEnabled=true \
  -s bruteForceProtected=true \
  -s failureFactor=5 \
  -s waitIncrementSeconds=60 \
  -s maxFailureWaitSeconds=900 \
  -s maxDeltaTimeSeconds=86400 \
  -s permanentLockout=false \
  -s 'passwordPolicy=length(8)' \
  -s resetPasswordAllowed=true \
  -s directAccessGrantsEnabled=true \
  -s "smtpServer=$SMTP_JSON"
log "настройки realm применены (email-as-username, brute force 5/нарастающее ожидание, policy 8+, SMTP, forgot password)"

# ---------- 2. realm roles (фиксированные; создаёт только этот скрипт/суперадмин) ----------
for role in user admin auditor; do
  if kc get "roles/$role" -r "$REALM" --fields name --format csv --noquotes 2>/dev/null | grep -q .; then
    log "role $role уже есть"
  else
    kc create roles -r "$REALM" -s "name=$role"
    log "создана realm role $role"
  fi
done

# ---------- 3. client scopes и мапперы клеймов ----------
get_scope_id() { kc get client-scopes -r "$REALM" --fields id,name --format csv --noquotes 2>/dev/null | id_by_field "$1" 2 | id_first; }

# scope `groups` + group membership mapper (плоский claim groups, имена групп).
# include.in.token.scope=false: имя scope НЕ попадает в claim scope — там по контракту
# только OIDC-значения + имена сервисов (jwt-claims.md §3)
groups_scope=$(get_scope_id groups)
if [ -z "$groups_scope" ]; then
  kc create client-scopes -r "$REALM" -s name=groups -s protocol=openid-connect \
    -s 'attributes={"include.in.token.scope":"false","display.on.consent.screen":"false"}'
  groups_scope=$(get_scope_id groups)
  log "создан client scope groups"
fi
if ! kc get "client-scopes/$groups_scope/protocol-mappers/models" -r "$REALM" --fields name --format csv --noquotes 2>/dev/null | grep -qx groups; then
  kc create "client-scopes/$groups_scope/protocol-mappers/models" -r "$REALM" \
    -s name=groups -s protocol=openid-connect -s protocolMapper=oidc-group-membership-mapper \
    -s 'config={"claim.name":"groups","full.path":"false","access.token.claim":"true","id.token.claim":"true","introspection.endpoint.claim":"true"}'
  log "создан mapper groups (group membership -> claim groups)"
fi

# scope `services` + user attribute mapper (атрибут services -> claim scope, строка через пробел)
services_scope=$(get_scope_id services)
if [ -z "$services_scope" ]; then
  kc create client-scopes -r "$REALM" -s name=services -s protocol=openid-connect \
    -s 'attributes={"include.in.token.scope":"false","display.on.consent.screen":"false"}'
  services_scope=$(get_scope_id services)
  log "создан client scope services"
fi
if ! kc get "client-scopes/$services_scope/protocol-mappers/models" -r "$REALM" --fields name --format csv --noquotes 2>/dev/null | grep -qx services; then
  kc create "client-scopes/$services_scope/protocol-mappers/models" -r "$REALM" \
    -s name=services -s protocol=openid-connect -s protocolMapper=oidc-usermodel-attribute-mapper \
    -s 'config={"user.attribute":"services","claim.name":"scope","jsonType.label":"String","multivalued":"false","aggregate.attrs":"false","access.token.claim":"true","id.token.claim":"false","introspection.endpoint.claim":"true"}'
  log "создан mapper services (user attribute services -> claim scope)"
fi

# встроенный scope `organization`: приводим Organization Membership mapper к multivalued=false,
# чтобы claim organization был строкой (alias), как в контракте JWT (план §4).
org_scope=$(get_scope_id organization)
if [ -z "$org_scope" ]; then
  kc create client-scopes -r "$REALM" -s name=organization -s protocol=openid-connect \
    -s 'attributes={"include.in.token.scope":"false","display.on.consent.screen":"false"}'
  org_scope=$(get_scope_id organization)
  log "создан client scope organization (не было встроенного)"
fi
org_mapper=$(kc get "client-scopes/$org_scope/protocol-mappers/models" -r "$REALM" --fields id,protocolMapper --format csv --noquotes 2>/dev/null | awk -F, '$2=="oidc-organization-membership-mapper"{print $1}' | id_first)
if [ -z "$org_mapper" ]; then
  kc create "client-scopes/$org_scope/protocol-mappers/models" -r "$REALM" \
    -s name=organization -s protocol=openid-connect -s protocolMapper=oidc-organization-membership-mapper \
    -s 'config={"claim.name":"organization","multivalued":"false"}'
  log "создан Organization Membership mapper (multivalued=false)"
else
  kc update "client-scopes/$org_scope/protocol-mappers/models/$org_mapper" -r "$REALM" \
    -s name=organization -s protocol=openid-connect -s protocolMapper=oidc-organization-membership-mapper \
    -s 'config={"claim.name":"organization","multivalued":"false"}'
  log "Organization Membership mapper приведён к multivalued=false (claim organization = alias строкой)"
fi

# группы и services — дефолтные scope всего realm; organization — опциональный realm-wide,
# но дефолт для клиентов portal и demo-api (см. ниже)
kc update "default-default-client-scopes/$groups_scope" -r "$REALM" 2>/dev/null || true
kc update "default-default-client-scopes/$services_scope" -r "$REALM" 2>/dev/null || true
log "client scopes groups/services добавлены в default realm scopes"

# ---------- 4. клиенты ----------
get_client_id() { kc get clients -r "$REALM" -q "clientId=$1" --fields id --format csv --noquotes 2>/dev/null | id_first; }

PORTAL_REDIRECT_JSON='["'"$(json_escape "${PORTAL_REDIRECT_URIS:-http://127.0.0.1:10030/*}")"'"]'
PORTAL_ORIGINS_JSON='["'"$(json_escape "${PORTAL_WEB_ORIGINS:-http://127.0.0.1:10030}")"'"]'

# portal — confidential + сервис-аккаунт (Admin REST для портала, P4)
portal_id=$(get_client_id portal)
if [ -z "$portal_id" ]; then
  kc create clients -r "$REALM" \
    -s clientId=portal -s name="RuLink Admin Portal" -s enabled=true \
    -s publicClient=false -s clientAuthenticatorType=client-secret \
    -s "secret=$(json_escape "$PORTAL_CLIENT_SECRET")" \
    -s serviceAccountsEnabled=true -s standardFlowEnabled=true -s directAccessGrantsEnabled=false \
    -s "redirectUris=$PORTAL_REDIRECT_JSON" -s "webOrigins=$PORTAL_ORIGINS_JSON"
  portal_id=$(get_client_id portal)
  log "создан клиент portal (confidential, service account)"
else
  kc update "clients/$portal_id" -r "$REALM" \
    -s enabled=true -s publicClient=false -s clientAuthenticatorType=client-secret \
    -s "secret=$(json_escape "$PORTAL_CLIENT_SECRET")" \
    -s serviceAccountsEnabled=true -s standardFlowEnabled=true -s directAccessGrantsEnabled=false \
    -s "redirectUris=$PORTAL_REDIRECT_JSON" -s "webOrigins=$PORTAL_ORIGINS_JSON"
  log "клиент portal обновлён"
fi
kc update "clients/$portal_id/default-client-scopes/$org_scope" -r "$REALM" 2>/dev/null || true

# demo-api — public (инструкции P2/P3, direct access grants для автотеста task-08)
demo_id=$(get_client_id demo-api)
if [ -z "$demo_id" ]; then
  kc create clients -r "$REALM" \
    -s clientId=demo-api -s name="RuLink Demo API" -s enabled=true \
    -s publicClient=true \
    -s standardFlowEnabled=true -s directAccessGrantsEnabled=true \
    -s 'redirectUris=["http://127.0.0.1:*/*","http://localhost:*/*"]' -s 'webOrigins=["+"]'
  demo_id=$(get_client_id demo-api)
  log "создан клиент demo-api (public, direct access grants ON — dev/test)"
else
  kc update "clients/$demo_id" -r "$REALM" \
    -s enabled=true -s publicClient=true \
    -s standardFlowEnabled=true -s directAccessGrantsEnabled=true \
    -s 'redirectUris=["http://127.0.0.1:*/*","http://localhost:*/*"]' -s 'webOrigins=["+"]'
  log "клиент demo-api обновлён"
fi
kc update "clients/$demo_id/default-client-scopes/$org_scope" -r "$REALM" 2>/dev/null || true

# audience mapper для demo-api: claim aud = [account, demo-api] (контракт JWT §3:
# «для demo-api — audience mapper, task-02»)
if ! kc get "clients/$demo_id/protocol-mappers/models" -r "$REALM" --fields name --format csv --noquotes 2>/dev/null | grep -qx demo-api-audience; then
  kc create "clients/$demo_id/protocol-mappers/models" -r "$REALM" \
    -s name=demo-api-audience -s protocol=openid-connect -s protocolMapper=oidc-audience-mapper \
    -s 'config={"included.client.audience":"demo-api","access.token.claim":"true"}'
  log "создан audience mapper demo-api (claim aud)"
fi

# ---------- 5. группы (realm, <org>-<имя>; см. шапку) ----------
get_group_id() { kc get groups -r "$REALM" -q "search=$1" --fields id,name --format csv --noquotes 2>/dev/null | id_by_field "$1" 2 | id_first; }
for g in orga-managers orga-team-a orgb-managers orgb-team-b; do
  if [ -n "$(get_group_id "$g")" ]; then
    log "группа $g уже есть"
  else
    kc create groups -r "$REALM" -s "name=$g"
    log "создана группа $g"
  fi
done

# ---------- 6. организации (Keycloak Organizations) ----------
get_org_id() { kc get organizations -r "$REALM" -q max=100 --fields id,alias --format csv --noquotes 2>/dev/null | id_by_field "$1" 2 | id_first; }
BASE_SERVICES_JSON='["cryptoservice","cadastralservice","cadastralanalyticservice"]'

ensure_org() { # $1 alias, $2 name, $3 domain
  local alias=$1 name=$2 domain=$3 id
  id=$(get_org_id "$alias")
  if [ -z "$id" ]; then
    kc create organizations -r "$REALM" \
      -s "name=$name" -s "alias=$alias" -s enabled=true \
      -s "attributes={\"availableServices\":$BASE_SERVICES_JSON}" \
      -s "domains=[{\"name\":\"$domain\",\"verified\":true}]"
    id=$(get_org_id "$alias")
    log "создана организация $name ($alias)"
  else
    kc update "organizations/$id" -r "$REALM" \
      -s "name=$name" -s enabled=true \
      -s "attributes={\"availableServices\":$BASE_SERVICES_JSON}"
    log "организация $name ($alias) обновлена"
  fi
  printf '%s' "$id"
}
org_a=$(ensure_org orga "Org A" orga.example.com)
org_b=$(ensure_org orgb "Org B" orgb.example.com)

org_has_member() { # $1 org id, $2 username
  kc get "organizations/$1/members" -r "$REALM" -q max=100 --fields id,username --format csv --noquotes 2>/dev/null \
    | awk -F, -v u="$2" '$2==u{f=1} END{exit !f}'
}
org_add_member() { # $1 org id, $2 user id
  printf '"%s"' "$2" | kc create "organizations/$1/members" -r "$REALM" -f -
}

# ---------- 7. пользователи ----------
get_user_id() { kc get users -r "$REALM" -q "username=$1" -q exact=true --fields id --format csv --noquotes 2>/dev/null | id_first; }

user_has_realm_role() { # $1 user id, $2 role
  kc get "users/$1/role-mappings/realm" -r "$REALM" --fields name --format csv --noquotes 2>/dev/null | grep -qx "$2"
}
user_in_group() { # $1 user id, $2 group id
  kc get "users/$1/groups" -r "$REALM" -q max=100 --fields id --format csv --noquotes 2>/dev/null | grep -qx "$2"
}

# username;firstName;lastName;realmRoles(через запятую);group;orgAlias
# Seed-данные — эталонные значения контракта JWT docs/contracts/jwt-claims.md §4:
# alice..erin, организации orga/orgb, группы orga-* / orgb-*.
SEED_USERS="
alice@orga.example.com;Alice;Admin;user,admin;orga-managers;orga
bob@orga.example.com;Bob;Backoff;user;orga-team-a;orga
carol@orga.example.com;Carol;Carlson;user,auditor;orga-team-a;orga
dave@orgb.example.com;Dave;Davidson;user,admin;orgb-managers;orgb
erin@orgb.example.com;Erin;Erson;user;orgb-team-b;orgb
"

while IFS=';' read -r username first last roles group org_alias; do
  [ -n "${username:-}" ] || continue
  uid=$(get_user_id "$username")
  # services-атрибут: подмножество availableServices организации пользователя
  # (эталонные значения — контракт JWT §4)
  case "$username" in
    alice@*) services='cryptoservice cadastralservice cadastralanalyticservice' ;;
    bob@*)   services='cryptoservice cadastralservice' ;;
    carol@*) services='cadastralanalyticservice' ;;
    dave@*)  services='cryptoservice cadastralservice cadastralanalyticservice' ;;
    erin@*)  services='cryptoservice' ;;
    *) die "нет описания services для $username" ;;
  esac
  attrs='{"services":["'"$(json_escape "$services")"'"]}'

  if [ -z "$uid" ]; then
    kc create users -r "$REALM" \
      -s "username=$username" -s "email=$username" -s emailVerified=true -s enabled=true \
      -s "firstName=$first" -s "lastName=$last" -s "attributes=$attrs"
    uid=$(get_user_id "$username")
    log "создан пользователь $username"
  else
    kc update "users/$uid" -r "$REALM" \
      -s "email=$username" -s emailVerified=true -s enabled=true \
      -s "firstName=$first" -s "lastName=$last" -s "attributes=$attrs"
    log "пользователь $username обновлён"
  fi

  # демо-пароль (не в репо, только env); password policy >= 8 проверена выше
  kc set-password -r "$REALM" --username "$username" --new-password "$INIT_DEMO_PASSWORD" >/dev/null
  # realm roles (у каждого есть user, admin/auditor — дополнительные; контракт §4)
  for role in ${roles//,/ }; do
    if ! user_has_realm_role "$uid" "$role"; then
      kc add-roles -r "$REALM" --uid "$uid" --rolename "$role"
      log "  роль $role назначена"
    fi
  done
  # группа своей организации
  gid=$(get_group_id "$group")
  [ -n "$gid" ] || die "не найдена группа $group"
  if ! user_in_group "$uid" "$gid"; then
    kc update "users/$uid/groups/$gid" -r "$REALM"
    log "  добавлен в группу $group"
  fi
  # членство в организации
  case "$org_alias" in
    orga) oid=$org_a ;;
    orgb) oid=$org_b ;;
    *) die "неизвестная организация $org_alias" ;;
  esac
  if ! org_has_member "$oid" "$username"; then
    org_add_member "$oid" "$uid"
    log "  добавлен в организацию $org_alias"
  fi
done <<<"$SEED_USERS"

# ---------- 8. суперадмин (realm-admin, вне организаций) ----------
SUPERADMIN=root@rulink.example
su_id=$(get_user_id "$SUPERADMIN")
if [ -z "$su_id" ]; then
  kc create users -r "$REALM" \
    -s "username=$SUPERADMIN" -s "email=$SUPERADMIN" -s emailVerified=true -s enabled=true \
    -s firstName=Super -s lastName=Admin
  su_id=$(get_user_id "$SUPERADMIN")
  log "создан суперадмин $SUPERADMIN"
fi
kc set-password -r "$REALM" --username "$SUPERADMIN" --new-password "$INIT_DEMO_PASSWORD" >/dev/null

# realm-management (client roles) для суперадмина: realm-admin (композитная — все права realm-management)
rm_cid=$(get_client_id realm-management)
[ -n "$rm_cid" ] || die "встроенный клиент realm-management не найден"
su_has_client_role() { # $1 user id, $2 role
  kc get "users/$1/role-mappings/clients/$rm_cid" -r "$REALM" --fields name --format csv --noquotes 2>/dev/null | grep -qx "$2"
}
if ! su_has_client_role "$su_id" realm-admin; then
  kc add-roles -r "$REALM" --uid "$su_id" --cclientid realm-management --rolename realm-admin
  log "суперадмину выдана роль realm-management/realm-admin"
fi

# сервис-аккаунту portal — минимальные права Admin REST (портал, P4)
SA_USERNAME=service-account-portal
sa_id=$(get_user_id "$SA_USERNAME")
[ -n "$sa_id" ] || die "не найден сервис-аккаунт $SA_USERNAME (клиент portal)"
for r in manage-users view-users query-users view-realm manage-organizations view-organizations query-organizations; do
  if ! su_has_client_role "$sa_id" "$r"; then
    kc add-roles -r "$REALM" --uid "$sa_id" --cclientid realm-management --rolename "$r"
  fi
done
log "сервис-аккаунту portal выданы роли realm-management (users/orgs)"

log "ГОТОВО: realm $REALM инициализирован (идемпотентно)."
log "Проверка JWT: deploy/verify.sh [username] (по умолчанию alice@orga.example.com)"

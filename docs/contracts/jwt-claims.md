# Контракт JWT — realm `rulink`

- **Версия контракта:** 1.0
- **Дата:** 2026-10-26
- **Статус:** эталон (reference). Живой токен сверяется с этим файлом: вручную в task-02, автотестом в task-08.
- **Источник:** план §4 «Контракт JWT», §5.3; PRD FR 2.
- **Аудитория:** потребители экосистемы (.NET 10 / ASP.NET Core сервисы). Интеграции пишутся против этого контракта, а не против конкретной инсталляции Keycloak.

Базовые адреса локальной dev-инсталляции (task-01):

| Что | URL |
|---|---|
| Issuer (realm) | `http://127.0.0.1:10020/realms/rulink` |
| Discovery (OIDC) | `http://127.0.0.1:10020/realms/rulink/.well-known/openid-configuration` |
| JWKS (ключи подписи) | `http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/certs` |
| Token endpoint | `http://127.0.0.1:10020/realms/rulink/protocol/openid-connect/token` |

В production issuer меняется (свой hostname), остальные пути относительно issuer неизменны — сервисы настраиваются одним значением Authority.

## 1. Формат и подпись

- Access token — **JWS compact serialization**, полезная нагрузка — JWT.
- Алгоритм подписи: **RS256** (RSA, realm-ключ Keycloak). Потребитель проверяет подпись по JWKS issuer'а (см. таблицу выше); ключ идентифицируется заголовком `kid`, Keycloak ротирует ключи — JWKS надо перечитывать при неизвестном `kid` (стандартные OIDC-библиотеки .NET делают это сами).
- Пример JOSE-заголовка:

```json
{
  "alg": "RS256",
  "typ": "JWT",
  "kid": "Nlq0Z2RjLT…"
}
```

- Секретов в токене нет: подпись закрытым ключом realm'а, проверка — открытым ключом. Выпуск и перевыпуск токенов — только Keycloak.

## 2. Эталонный access token (пользователь)

Живой пример: seeded-пользователь `alice@orga.example.com` (администратор организации `orga`), вход через клиент `demo-api`. Мапперы, формирующие расширенные клеймы, — task-02 (§8).

```json
{
  "exp": 1794870600,
  "iat": 1794870300,
  "jti": "e4f9a1c2-7b3d-4e5f-8a9b-0c1d2e3f4a5b",
  "iss": "http://127.0.0.1:10020/realms/rulink",
  "aud": ["account", "demo-api"],
  "sub": "6d9e1c66-2f3a-4b8e-9d2f-1a2b3c4d5e6f",
  "typ": "Bearer",
  "azp": "demo-api",
  "sid": "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
  "acr": "1",
  "allowed-origins": ["http://127.0.0.1:10020"],
  "realm_access": {
    "roles": [
      "user",
      "admin",
      "default-roles-rulink",
      "offline_access",
      "uma_authorization"
    ]
  },
  "resource_access": {
    "account": {
      "roles": ["manage-account", "manage-account-links", "view-profile"]
    }
  },
  "email_verified": true,
  "name": "Alice Admin",
  "given_name": "Alice",
  "family_name": "Admin",
  "preferred_username": "alice@orga.example.com",
  "email": "alice@orga.example.com",
  "organization": "orga",
  "groups": ["orga-managers"],
  "scope": "email profile cryptoservice cadastralservice cadastralanalyticservice"
}
```

Экземпляр-зависимые значения (генерируются Keycloak при каждом выпуске, при сверке составом клеймов не сравниваются по значению): `exp`, `iat`, `jti`, `sub`, `sid`, `acr`, `allowed-origins`, `kid` в заголовке.

## 3. Таблица клеймов

| Claim | Тип | Обязателен | Источник |
|-------|-----|-----------|----------|
| `iss` | string (issuer) | да | стандарт: всегда `…/realms/rulink` |
| `sub` | string (UUID пользователя) | да | стандарт Keycloak |
| `exp`, `iat` | int (unix time) | да | стандарт |
| `jti`, `sid`, `acr`, `typ` | string | да | стандарт |
| `aud` | array of string: технический клиент `account` + аудитории клиента (для `demo-api` — audience mapper, task-02) | да | стандарт + audience mapper |
| `azp` | string: идентификатор клиента, которому выдан токен | да | стандарт |
| `email`, `email_verified`, `preferred_username` | string / bool; `preferred_username` = `email` (username = email, план §2.7) | да | стандарт Keycloak |
| `name`, `given_name`, `family_name` | string | нет (если заполнены в профиле) | стандарт (client scope `profile`) |
| `organization` | **string — имя организации** | да (для пользователей) | встроенный organization mapper (task-02): у пользователя ровно одна организация, поэтому значение — плоское имя, не массив |
| `groups` | **array of string — имена групп без пути** | да (может быть пустым) | group membership mapper (task-02): «Full group path» = OFF → только имена групп |
| `realm_access.roles` | array of string | да | стандарт: роли пользователя в realm |
| `resource_access.<client>.roles` | object | нет | стандарт: клиентские роли |
| `scope` | **string — имена сервисов через пробел** (подмножество `availableServices` организации пользователя) | да | user attribute `services` → user attribute mapper (task-02, aggregate + single value → строка через пробел) |

Роли `realm_access.roles`:

- прикладные роли фиксированы: `user`, `admin`, `auditor`; у каждого пользователя есть роль `user`, `admin`/`auditor` — дополнительные;
- технические роли Keycloak (`default-roles-rulink`, `offline_access`, `uma_authorization`) присутствуют всегда — **потребители обязаны их игнорировать**;
- проверка роли — вхождение имени в массив (см. §7), не точное равенство всего массива.

Клейм `scope`:

- кроме сервисов содержит стандартные OIDC-значения (`openid`, `email`, `profile` — что запрошено клиентом);
- сервисы фазы 1: `cryptoservice`, `cadastralservice`, `cadastralanalyticservice`; список расширяем на уровне организаций (атрибут `availableServices`), значение у пользователя — всегда подмножество набора его организации;
- проверка доступа к сервису — вхождение имени сервиса в `scope` (split по пробелу), не точное равенство строки.

`aud`: сервисы-потребители проверяют `iss` + подпись обязательно; `aud` — если сервис заявил себя аудиторией (audience mapper). Для демо-сервисов фазы 1 достаточно дефолтного `account` + проверки `iss`/подписи (см. `docs/integrations/dotnet-oauth.md`, task-05).

## 4. Seeded-пользователи (эталонные значения)

Эталонные данные, которыми task-02 сеет realm (пароли — только из env `INIT_DEMO_PASSWORD`, в репо и в этом файле их нет; `email_verified = true` у всех):

| Пользователь (username = email) | Организация | Роли (`realm_access.roles`) | Группы (`groups`) | `services` (`scope`) |
|---|---|---|---|---|
| `alice@orga.example.com` | `orga` | `user`, `admin` | `orga-managers` | `cryptoservice cadastralservice cadastralanalyticservice` |
| `bob@orga.example.com` | `orga` | `user` | `orga-team-a` | `cryptoservice cadastralservice` |
| `carol@orga.example.com` | `orga` | `user`, `auditor` | `orga-team-a` | `cadastralanalyticservice` |
| `dave@orgb.example.com` | `orgb` | `user`, `admin` | `orgb-managers` | `cryptoservice cadastralservice cadastralanalyticservice` |
| `erin@orgb.example.com` | `orgb` | `user` | `orgb-team-b` | `cryptoservice` |

`availableServices`: `orga` и `orgb` — все три сервиса фазы 1 (различие наборов проверяется админ-порталом, task-07). Пара `bob`+`carol` состоит в одной группе `orga-team-a` — рабочая пара для проверки групповых данных (P3, task-04/06).

## 5. Lifetime токенов

Дефолты realm (task-02 их не меняет — это и есть контракт; при изменении фиксировать здесь и в CHANGELOG):

| Токен | Время жизни | Где настраивается в KC |
|---|---|---|
| Access token | **5 минут** (300 c) | Realm Settings → Tokens → Access Token Lifespan |
| ID token | как access token (5 минут) | следует за access token |
| Refresh token | сессия: idle **30 минут**, абсолютный максимум **10 часов** | Realm Settings → Tokens → SSO Session Idle / SSO Session Max |

Правила для потребителей:

- access token не кэшировать дольше `exp`; обновление — refresh token (или повторный OIDC-логин);
- refresh token одноразового использования KC не требует (Revoke Refresh Token = OFF, дефолт);
- часовые метки — unix time; допустимый перекос часов — до 60 с (JwtBearer .NET по умолчанию допускает 5 минут — этого достаточно).

## 6. Сервис-сервис: client_credentials

- Клиент: **confidential с включённым service account** (Service Accounts Enabled). В фазе 1 — клиент `portal` (сервис-аккаунт для вызова Admin REST API, план §5.2/§5.7). Новому сервис-потребителю — свой confidential-клиент с service account, а не переиспользование `portal`.
- Выпуск: `POST …/protocol/openid-connect/token`, `grant_type=client_credentials`, `client_id`, `client_secret` (секрет — из `.env`).
- Пример payload service-токена (клиент `portal`):

```json
{
  "exp": 1794870600,
  "iat": 1794870300,
  "jti": "f1e2d3c4-…",
  "iss": "http://127.0.0.1:10020/realms/rulink",
  "aud": "account",
  "sub": "9a8b7c6d-5e4f-4a3b-2c1d-0e9f8a7b6c5d",
  "typ": "Bearer",
  "azp": "portal",
  "preferred_username": "service-account-portal",
  "realm_access": { "roles": ["default-roles-rulink"] },
  "scope": "email profile"
}
```

- Отличия от пользовательского токена: нет `email`/`organization`/`groups` и других профильных клеймов; `azp` = идентификатор клиента; `preferred_username` = `service-account-<client>`; роли — роли сервис-аккаунта в realm.
- Авторизация вызовов: по `azp` (какой сервис) и/или ролям сервис-аккаунта. Атрибут `services` сервис-аккаунту фазы 1 не назначается; если межсервисному клиенту нужен набор сервисов — это фиксируется здесь отдельной версией контракта.

## 7. Чтение клеймов на .NET 10 (ASP.NET Core)

Валидация (пакет `Microsoft.AspNetCore.Authentication.JwtBearer`): Authority = issuer realm'а, JWKS .NET забирает и кэширует сам, ротация ключей — по `kid`.

```csharp
// Program.cs
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = "http://127.0.0.1:10020/realms/rulink";
        o.RequireHttpsMetadata = false; // только локальная dev-инсталляция; в prod — https
        o.MapInboundClaims = false;     // сохраняем оригинальные имена клеймов: organization, groups, scope…
    });
```

`realm_access.roles` → role claims (трансформация) — тогда работают `[Authorize(Roles = "…")]`:

```csharp
builder.Services.AddSingleton<IClaimsTransformation, RealmRolesTransformer>();

sealed class RealmRolesTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is ClaimsIdentity id
            && !id.HasClaim(c => c.Type == ClaimTypes.Role)
            && principal.FindFirst("realm_access")?.Value is { } json)
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var role in doc.RootElement.GetProperty("roles").EnumerateArray())
                id.AddClaim(new Claim(ClaimTypes.Role, role.GetString()!));
        }
        return Task.FromResult(principal);
    }
}
```

Хелперы для остальных клеймов контракта:

```csharp
public static class KeycloakClaimsExtensions
{
    public static string? Organization(this ClaimsPrincipal user)
        => user.FindFirst("organization")?.Value;

    // groups приходит одним клеймом-строкой с JSON-массивом
    public static IReadOnlyList<string> Groups(this ClaimsPrincipal user)
        => user.FindFirst("groups")?.Value is { } json
            ? JsonSerializer.Deserialize<string[]>(json) ?? []
            : [];

    // scope — строка имён через пробел
    public static bool HasServiceScope(this ClaimsPrincipal user, string service)
        => user.FindFirst("scope")?.Value.Split(' ').Contains(service) ?? false;
}
```

Использование (полный пример — `examples/demo-api`, task-04; инструкция — task-05):

```csharp
app.MapGet("/me", (ClaimsPrincipal user) => new
{
    user.Identity!.Name,
    Organization = user.Organization(),
    Groups = user.Groups(),
    Services = user.FindFirst("scope")!.Value.Split(' '),
}).RequireAuthorization();

app.MapGet("/admin", () => "admin ok").RequireAuthorization(policy =>
    policy.RequireRole("admin"));
```

## 8. Правила сверки токена с эталоном (для task-02 вручную и task-08 автотеста)

1. **Обязательный состав клеймов** (наличие, не точные значения): `iss`, `sub`, `exp`, `iat`, `jti`, `typ`, `azp`, `aud`, `email`, `email_verified`, `preferred_username`, `organization`, `groups`, `realm_access`, `scope`.
2. `iss` = `<base>/realms/rulink`; `preferred_username` = `email`.
3. `organization` — string, имя организации пользователя (одна из §4).
4. `groups` — array of string, имена без пути (`/` в значениях недопустим), соответствует §4.
5. `realm_access.roles` содержит `user` и прикладные роли этого пользователя из §4 (проверка — вхождение; технические `default-roles-rulink`, `offline_access`, `uma_authorization` допустимы и игнорируются).
6. `scope` — набор сервисов пользователя из §4 (вхождение каждого имени), без сервисов вне `availableServices` организации.
7. Подпись RS256 валидна по JWKS realm'а; заголовок `kid` присутствует.
8. Экземпляр-зависимые значения (`sub`, `jti`, `sid`, `iat`, `exp`, `kid`, `acr`, `allowed-origins`, `aud` сверх обязательных, `resource_access`, `name`/`given_name`/`family_name`) — не причина провала сверки.

## Ссылки

- План: `docs/plans/2026-10-26-identity-service-phase1.md` §4 «Контракт JWT» (до переноса — `docs/Plan.md`)
- Мапперы, формирующие клеймы: `docs/tasks/task-02.md`
- Автотест сверки: `docs/tasks/task-08.md`
- Демо-потребитель контракта: `docs/tasks/task-04.md` (`examples/demo-api`)
- Инструкция OIDC для .NET: `docs/tasks/task-05.md` → `docs/integrations/dotnet-oauth.md`
